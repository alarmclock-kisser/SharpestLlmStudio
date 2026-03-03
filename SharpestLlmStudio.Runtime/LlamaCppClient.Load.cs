using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient : IDisposable
    {
        private Process? _serverProcess;
        private readonly HttpClient _httpClient = new();
        private string _currentBaseUrl = string.Empty;

        // Async-captured server output (populated via BeginErrorReadLine / BeginOutputReadLine)
        private readonly StringBuilder _serverStderr = new();
        private readonly StringBuilder _serverStdout = new();

        public bool IsServerRunning => this._serverProcess != null && !this._serverProcess.HasExited;
        public string CurrentBaseUrl => this._currentBaseUrl;

        public async Task<LlamaModelLoadResult> LoadModelAsync(LlamaModelLoadRequest request, CancellationToken cancellationToken = default)
        {
            if (this._settings.KillExistingServerInstancesBeforeLoad)
            {
                int killed = this.KillAllLlamaServerExeInstances() ?? 0;
                if (killed > 0)
                {
                    await StaticLogger.LogAsync($"[LlamaCpp] Killed {killed} existing llama-server.exe instance(s) before loading new model.");
                }
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // 1. Alten Server beenden, falls noch einer läuft
                this.UnloadModel();

                // Output-Puffer leeren
                lock (this._serverStderr) { this._serverStderr.Clear(); }
                lock (this._serverStdout) { this._serverStdout.Clear(); }

                // 2. Argumente zusammenbauen
                var args = $"-m \"{request.ModelInfo.ModelFilePath}\" -ngl {request.GpuLayers} -c {request.ContextSize} --host {request.Host} --port {request.Port}";

                if (request.IncludeMmproj && !string.IsNullOrEmpty(request.ModelInfo.MmprojFilePath))
                {
                    args += $" --mmproj \"{request.ModelInfo.MmprojFilePath}\"";
                }

                if (request.UseFlashAttention)
                {
                    args += " --flash-attn on";
                }

                // Enable embedding endpoint for knowledge base vectorization
                // NOTE: --embedding conflicts with multimodal (VL/mmproj) models — only enable when no mmproj is loaded
                bool hasMmproj = request.IncludeMmproj && !string.IsNullOrEmpty(request.ModelInfo.MmprojFilePath);
                if (!hasMmproj)
                {
                    args += " --embedding";
                }

                // Enable slot save/restore for context management
                args += $" --slot-save-path \"{this.ContextDirectory}\"";

                // Prüfen ob Executable existiert
                var exePath = ResolveExecutablePath(request.ServerExecutablePath);
                await StaticLogger.LogAsync($"[LlamaCpp] Resolved executable: {exePath}");

                if (!File.Exists(exePath))
                {
                    var msg = $"Executable not found: '{request.ServerExecutablePath}' (resolved to '{exePath}'). "
                            + "Ensure llama-server.exe path is absolute in appsettings.json or the file exists at the given location.";
                    await StaticLogger.LogAsync($"[LlamaCpp] {msg}");
                    return new LlamaModelLoadResult { Success = false, ErrorMessage = msg };
                }

                await StaticLogger.LogAsync($"[LlamaCpp] Starting: {exePath} {args}");

                // 3. Prozess konfigurieren und starten
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                this._serverProcess = Process.Start(startInfo);

                if (this._serverProcess == null)
                {
                    var msg = $"Process.Start returned null for '{exePath}'.";
                    await StaticLogger.LogAsync($"[LlamaCpp] {msg}");
                    return new LlamaModelLoadResult { Success = false, ErrorMessage = msg };
                }

                // Sofort asynchron stdout/stderr lesen — verhindert Pipe-Deadlocks
                // und fängt die Crash-Meldung auf, bevor der Prozess stirbt.
                this._serverProcess.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (this._serverStderr) { this._serverStderr.AppendLine(e.Data); }
                    }
                };
                this._serverProcess.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (this._serverStdout) { this._serverStdout.AppendLine(e.Data); }
                    }
                };
                this._serverProcess.BeginErrorReadLine();
                this._serverProcess.BeginOutputReadLine();

                await StaticLogger.LogAsync($"[LlamaCpp] Process started (PID {this._serverProcess.Id}). Waiting for health endpoint...");
                this._currentBaseUrl = $"http://{request.Host}:{request.Port}";

                // 4. Warten, bis der Server hochgefahren und das Modell im VRAM ist
                bool isReady = await this.WaitForServerReadyAsync(this._currentBaseUrl, TimeSpan.FromMinutes(2), cancellationToken);

                stopwatch.Stop();

                if (isReady)
                {
                    await StaticLogger.LogAsync($"[LlamaCpp] Server ready at {this._currentBaseUrl} after {stopwatch.Elapsed.TotalSeconds:F1}s");
                    return new LlamaModelLoadResult
                    {
                        Success = true,
                        BaseApiUrl = this._currentBaseUrl,
                        LoadTime = stopwatch.Elapsed
                    };
                }
                else
                {
                    var serverOutput = this.GetCapturedServerOutput();
                    this.UnloadModel();
                    var msg = "Server failed to start.";
                    if (!string.IsNullOrWhiteSpace(serverOutput))
                    {
                        msg += $"\n{serverOutput}";
                    }
                    await StaticLogger.LogAsync($"[LlamaCpp] {msg}");
                    return new LlamaModelLoadResult { Success = false, ErrorMessage = msg, LoadTime = stopwatch.Elapsed };
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var serverOutput = this.GetCapturedServerOutput();
                await StaticLogger.LogAsync($"[LlamaCpp] Exception in LoadModelAsync: {ex}");
                if (!string.IsNullOrWhiteSpace(serverOutput))
                {
                    await StaticLogger.LogAsync($"[LlamaCpp] Server output:\n{serverOutput}");
                }

                this.UnloadModel();
                var msg = ex.Message;
                if (!string.IsNullOrWhiteSpace(serverOutput))
                {
                    msg += $"\n{serverOutput}";
                }
                return new LlamaModelLoadResult { Success = false, ErrorMessage = msg, LoadTime = stopwatch.Elapsed };
            }
        }

        /// <summary>
        /// Returns captured stderr + stdout from the server process (max 1000 chars each).
        /// </summary>
        private string GetCapturedServerOutput()
        {
            var sb = new StringBuilder();
            string stderr, stdout;
            lock (this._serverStderr) { stderr = this._serverStderr.ToString().Trim(); }
            lock (this._serverStdout) { stdout = this._serverStdout.ToString().Trim(); }

            if (!string.IsNullOrEmpty(stderr))
            {
                sb.AppendLine("stderr: " + (stderr.Length > 1000 ? stderr[^1000..] : stderr));
            }
            if (!string.IsNullOrEmpty(stdout))
            {
                sb.AppendLine("stdout: " + (stdout.Length > 1000 ? stdout[^1000..] : stdout));
            }
            return sb.ToString().Trim();
        }

        public void UnloadModel()
        {
            if (this._serverProcess != null)
            {
                try
                {
                    if (!this._serverProcess.HasExited)
                    {
                        this._serverProcess.Kill(true);
                        this._serverProcess.WaitForExit(2000);
                    }
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
            var healthUrl = $"{baseUrl}/health";
            int attempt = 0;

            while (stopwatch.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
            {
                if (this._serverProcess != null && this._serverProcess.HasExited)
                {
                    // Kurz warten damit stderr-Events noch ankommen
                    await Task.Delay(250, CancellationToken.None);
                    var exitCode = this._serverProcess.ExitCode;
                    var serverOutput = this.GetCapturedServerOutput();
                    await StaticLogger.LogAsync($"[LlamaCpp] Server process exited with code {exitCode} during health polling (attempt {attempt}).");
                    if (!string.IsNullOrWhiteSpace(serverOutput))
                    {
                        await StaticLogger.LogAsync($"[LlamaCpp] Server output:\n{serverOutput}");
                    }
                    return false;
                }

                attempt++;
                try
                {
                    var response = await this._httpClient.GetAsync(healthUrl, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        await StaticLogger.LogAsync($"[LlamaCpp] Health check OK after {attempt} attempts ({stopwatch.Elapsed.TotalSeconds:F1}s).");
                        return true;
                    }
                    else
                    {
                        if (attempt <= 3 || attempt % 10 == 0)
                        {
                            await StaticLogger.LogAsync($"[LlamaCpp] Health check attempt {attempt}: HTTP {(int)response.StatusCode} ({stopwatch.Elapsed.TotalSeconds:F1}s)");
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    if (attempt <= 3 || attempt % 10 == 0)
                    {
                        await StaticLogger.LogAsync($"[LlamaCpp] Health check attempt {attempt}: Connection refused ({stopwatch.Elapsed.TotalSeconds:F1}s)");
                    }
                }

                // Kurz warten und nochmal probieren
                await Task.Delay(500, cancellationToken);
            }

            return false;
        }

        public void Dispose()
        {
            this.UnloadModel();

            if (this._settings.KillExistingServerInstancesBeforeLoad)
            {
                try
                {
                    _ = this.KillAllLlamaServerExeInstances();
                }
                catch
                {
                }
            }

            this._httpClient.Dispose();
        }

        /// <summary>
        /// Resolves an executable name/path to an absolute path.
        /// 1. If the path is already absolute and exists → return as-is.
        /// 2. Try relative to the current working directory.
        /// 3. Search the system PATH environment variable.
        /// 4. Fallback: return the original value unchanged.
        /// </summary>
        private static string ResolveExecutablePath(string executable)
        {
            // Already absolute?
            if (Path.IsPathRooted(executable) && File.Exists(executable))
            {
                return executable;
            }

            // Relative to CWD?
            var fullPath = Path.GetFullPath(executable);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            // Search PATH (same logic the OS shell uses)
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                var fileName = Path.GetFileName(executable);
                foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            // Nothing found — return original so the caller gets a clear error
            return executable;
        }
    }
}
