using Radzen;
using SharpestLlmStudio.Monitoring;
using SharpestLlmStudio.Runtime;
using SharpestLlmStudio.Runtime.ONNX;
using SharpestLlmStudio.Shared;
using SharpestLlmStudio.WebApp.Components;
using SharpestLlmStudio.WebApp.ViewModels;
using Microsoft.AspNetCore.HttpOverrides;

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

            builder.Services.AddSingleton<ScreenClicker>();

            // ApiClient + WebAppSettings
            builder.Services.AddSingleton(webAppSettings);
            builder.Services.AddSingleton<LlamaCppClient>(provider =>
                new LlamaCppClient(webAppSettings, provider.GetService<GpuMonitor>()));
            builder.Services.AddSingleton<OnnxWhisperService>(provider =>
                new OnnxWhisperService(webAppSettings));

            // Decide whether HTTPS features should be enabled based on configured URLs.
            // When the app is run without HTTPS (single-file, self-contained builds may use HTTP)
            // enabling HSTS / HTTPS redirection and requiring Secure cookies will cause runtime
            // errors. Detect configured URLs and only enable HTTPS features when an https:// URL
            // is present.
            var configuredUrls = builder.Configuration["ASPNETCORE_URLS"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? builder.Configuration["urls"];
            var enableHttps = !string.IsNullOrEmpty(configuredUrls) && configuredUrls.Contains("https://", StringComparison.OrdinalIgnoreCase);

            if (enableHttps)
            {
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
            }

            // Antiforgery-Cookie für die Sicherstellung von SameSite-Attributen
            builder.Services.AddAntiforgery(options =>
            {
                // If HTTPS is enabled, we can use SameSite=None (required for some cross-site scenarios)
                // and set Secure. When running over plain HTTP (single-file self-contained scenarios)
                // browsers will warn if SameSite=None is set without Secure, so use Lax in that case.
                options.Cookie.SameSite = enableHttps ? SameSiteMode.None : SameSiteMode.Lax;
                options.Cookie.SecurePolicy = (builder.Environment.IsDevelopment() || !enableHttps)
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
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

            // If the app is behind a reverse proxy (terminating TLS), honor the
            // X-Forwarded-For / X-Forwarded-Proto headers so the framework
            // correctly recognizes the original request scheme.
            var forwardedOptions = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            };
            // In some containerized setups you may need to clear KnownNetworks / KnownProxies:
            // forwardedOptions.KnownNetworks.Clear(); forwardedOptions.KnownProxies.Clear();
            app.UseForwardedHeaders(forwardedOptions);

            if (enableHttps)
            {
                app.UseHsts();
                app.UseHttpsRedirection();
            }

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
