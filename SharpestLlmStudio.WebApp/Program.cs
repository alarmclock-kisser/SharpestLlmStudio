using Radzen;
using SharpestLlmStudio.Runtime;
using SharpestLlmStudio.Shared;
using SharpestLlmStudio.WebApp.Components;
using SharpestLlmStudio.WebApp.ViewModels;

namespace SharpestLlmStudio.WebApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            WebAppSettings webAppSettings = builder.Configuration.GetSection("WebAppSettings").Get<WebAppSettings>() ?? new WebAppSettings();

            // CORS für API-Zugriff
            const string CorsPolicy = "AllowApi";
            builder.Services.AddCors(options =>
            {
                options.AddPolicy(CorsPolicy, policy =>
                {
                    policy
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials()
                        .SetIsOriginAllowed(_ => true);
                });
            });

            // StaticLogger init
            StaticLogger.InitializeLogFiles(string.IsNullOrEmpty(webAppSettings.LogDirectory) ? Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory) : webAppSettings.LogDirectory, webAppSettings.CreateLogFile, webAppSettings.MaxPreviousLogFiles);
            StaticLogger.SetUiContext(SynchronizationContext.Current ?? new SynchronizationContext());

            // Optional singleton GpuMonitor
            if (webAppSettings.EnableMonitoring)
            {
                builder.Services.AddSingleton<GpuMonitor>();
            }

            // ApiClient + WebAppSettings
            builder.Services.AddSingleton(webAppSettings);
            builder.Services.AddSingleton<LlamaCppClient>(provider =>
                new LlamaCppClient(webAppSettings, provider.GetService<GpuMonitor>()));

            // HTTPS-Umleitung und HSTS aktivieren
            builder.Services.AddHttpsRedirection(options =>
            {
                options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
            });

            builder.Services.AddHsts(options =>
            {
                options.Preload = true;
                options.IncludeSubDomains = true;
                options.MaxAge = TimeSpan.FromDays(365);
            });

            // Antiforgery-Cookie für die Sicherstellung von SameSite-Attributen
            builder.Services.AddAntiforgery(options =>
            {
                options.Cookie.SameSite = SameSiteMode.None;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.HeaderName = "X-CSRF-TOKEN";
            });

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddRadzenComponents();
            builder.Services.AddScoped<HomeViewModel>();
            // builder.Services.AddScoped<ContextViewModel>();

            var app = builder.Build();

            // HTTP-Pipeline konfigurieren
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseHsts();
            app.UseHttpsRedirection();

            app.UseStaticFiles();

            app.UseRouting();

            app.UseCors(CorsPolicy);

            app.UseAntiforgery();

            // WebSockets für Blazor verwenden
            app.UseWebSockets();
            app.UseAuthorization();

            // Blazor Server-Endpunkte
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            // Kill llama-server processes on app shutdown (including debug stop)
            if (webAppSettings.KillExistingServerInstances)
            {
                void KillLlamaServersOnShutdown()
                {
                    try
                    {
                        var client = app.Services.GetService<LlamaCppClient>();
                        int? killed = client?.KillAllLlamaServerExeInstances();
                        if (killed is > 0)
                        {
                            StaticLogger.Log($"[Shutdown] Killed {killed.Value} llama-server instance(s) during app shutdown.");
                        }
                    }
                    catch { }
                }

                var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
                lifetime.ApplicationStopping.Register(KillLlamaServersOnShutdown);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => KillLlamaServersOnShutdown();
            }

            app.Run();
        }
    }
}
