using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    // Wichtig: 'partial' und 'IDisposable' hinzufügen
    public partial class LlamaCppClient : IDisposable
    {
        private Process? _serverProcess;
        private readonly HttpClient _httpClient = new();
        private string _currentBaseUrl = string.Empty;

        public bool IsServerRunning => this._serverProcess != null && !this._serverProcess.HasExited;
        public string CurrentBaseUrl => this._currentBaseUrl;

        public async Task<LlamaModelLoadResult> LoadModelAsync(LlamaModelLoadRequest request, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // 1. Alten Server beenden, falls noch einer läuft
                this.UnloadModel();

                // 2. Argumente zusammenbauen
                var args = $"-m \"{request.ModelInfo.ModelFilePath}\" -ngl {request.GpuLayers} -c {request.ContextSize} --host {request.Host} --port {request.Port}";

                if (!string.IsNullOrEmpty(request.ModelInfo.MmprojFilePath))
                {
                    args += $" --mmproj \"{request.ModelInfo.MmprojFilePath}\"";
                }

                if (request.UseFlashAttention)
                {
                    args += " -fa"; // Aktiviert Flash Attention (super für Performance bei großem Context)
                }

                // 3. Prozess konfigurieren und starten
                var startInfo = new ProcessStartInfo
                {
                    FileName = request.ServerExecutablePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true, // Wir verstecken das Konsolenfenster der llama-server.exe
                    // Optional: Output umleiten, falls du Server-Logs in Blazor anzeigen willst
                    // RedirectStandardOutput = true, 
                    // RedirectStandardError = true 
                };

                this._serverProcess = Process.Start(startInfo);

                if (this._serverProcess == null)
                {
                    return new LlamaModelLoadResult { Success = false, ErrorMessage = "Konnte llama-server.exe nicht starten." };
                }

                this._currentBaseUrl = $"http://{request.Host}:{request.Port}";

                // 4. Warten, bis der Server hochgefahren und das Modell im VRAM ist
                bool isReady = await this.WaitForServerReadyAsync(this._currentBaseUrl, TimeSpan.FromMinutes(2), cancellationToken);

                stopwatch.Stop();

                if (isReady)
                {
                    return new LlamaModelLoadResult
                    {
                        Success = true,
                        BaseApiUrl = this._currentBaseUrl,
                        LoadTime = stopwatch.Elapsed
                    };
                }
                else
                {
                    this.UnloadModel(); // Aufräumen, falls Timeout
                    return new LlamaModelLoadResult { Success = false, ErrorMessage = "Timeout: Server hat nicht in der vorgegebenen Zeit geantwortet." };
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                this.UnloadModel(); // Sicherheitshalber abschießen
                return new LlamaModelLoadResult { Success = false, ErrorMessage = ex.Message, LoadTime = stopwatch.Elapsed };
            }
        }

        public void UnloadModel()
        {
            if (this._serverProcess != null && !this._serverProcess.HasExited)
            {
                try
                {
                    this._serverProcess.Kill(true); // Killt den Prozessbaum (falls child-prozesse da sind)
                    this._serverProcess.WaitForExit(2000);
                }
                catch (Exception)
                {
                    // Ignorieren, falls der Prozess ohnehin schon weg ist
                }
                finally
                {
                    this._serverProcess.Dispose();
                    this._serverProcess = null;
                    this._currentBaseUrl = string.Empty;
                }
            }
        }

        private async Task<bool> WaitForServerReadyAsync(string baseUrl, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            // Der llama-server hat standardmäßig einen /health endpoint
            var healthUrl = $"{baseUrl}/health";

            while (stopwatch.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
            {
                if (this._serverProcess != null && this._serverProcess.HasExited)
                {
                    return false; // Server ist gecrasht während des Ladens
                }

                try
                {
                    var response = await this._httpClient.GetAsync(healthUrl, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        // Server ist da und antwortet!
                        return true;
                    }
                }
                catch (HttpRequestException)
                {
                    // Server ist noch nicht bereit (Port noch nicht offen)
                }

                // Kurz warten und nochmal probieren
                await Task.Delay(500, cancellationToken);
            }

            return false;
        }

        public void Dispose()
        {
            // Wenn der Garbage Collector kommt oder die App beendet wird: Aufräumen!
            this.UnloadModel();
            this._httpClient.Dispose();
        }
    }
}