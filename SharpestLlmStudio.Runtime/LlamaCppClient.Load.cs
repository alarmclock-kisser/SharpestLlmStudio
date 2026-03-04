using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
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
        public int CurrentContextSize { get; private set; }

        // Async-captured server output (populated via BeginErrorReadLine / BeginOutputReadLine)
        private readonly StringBuilder _serverStderr = new();
        private readonly StringBuilder _serverStdout = new();

        public bool IsServerRunning => (this._serverProcess != null && !this._serverProcess.HasExited) || !string.IsNullOrWhiteSpace(this._currentBaseUrl);
        public string CurrentBaseUrl => this._currentBaseUrl;

        public async Task<LlamaModelLoadResult> LoadModelAsync(LlamaModelLoadRequest request, CancellationToken cancellationToken = default)
        {
            string targetBaseUrl = $"http://{request.Host}:{request.Port}";

            if (this._settings.KillExistingServerInstances)
            {
                int killed = this.KillAllLlamaServerExeInstances() ?? 0;
                if (killed > 0)
                {
                    await StaticLogger.LogAsync($"[LlamaCpp] Killed {killed} existing llama-server.exe instance(s) before loading new model.");
                }
            }
            else
            {
                var reused = await TryReuseExistingInstanceAsync(targetBaseUrl, request.ContextSize, cancellationToken);
                if (reused != null)
                {
                    return reused;
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

                // For Omni models, always include vision/encoder gguf as --mmproj if available
                bool shouldIncludeMmproj = request.IncludeMmproj || request.ModelInfo.IsOmni;
                if (shouldIncludeMmproj && !string.IsNullOrEmpty(request.ModelInfo.MmprojFilePath))
                {
                    args += $" --mmproj \"{request.ModelInfo.MmprojFilePath}\"";
                }

                if (request.UseFlashAttention)
                {
                    args += " --flash-attn on";
                }

                // Enable embedding endpoint for knowledge base vectorization
                // NOTE: --embedding conflicts with multimodal (VL/mmproj) models — only enable when no mmproj is loaded
                bool hasMmproj = shouldIncludeMmproj && !string.IsNullOrEmpty(request.ModelInfo.MmprojFilePath);
                if (!hasMmproj)
                {
                    args += " --embedding --pooling mean";
                }

                // Enable slot save/restore for context management
                args += $" --slot-save-path \"{this.ContextDirectory}\"";

                // Prüfen ob Executable existiert
                var exePath = ResolveExecutablePath(request.ServerExecutablePath);

                // Special case for Omni/MiniCPM-o or multi-file models: Use omni server
                var modelDir = request.ModelInfo.ModelRootDirectory;
                int ggufCount = Directory.Exists(modelDir) ? Directory.GetFiles(modelDir, "*.gguf", SearchOption.AllDirectories).Length : 0;

                if (request.ModelInfo.IsOmni ||
                    request.ModelInfo.Name.Contains("MiniCPM", StringComparison.OrdinalIgnoreCase) ||
                    request.ModelInfo.ModelFilePath.Contains("MiniCPM", StringComparison.OrdinalIgnoreCase) ||
                    ggufCount > 2)
                {
                    var omniPath = Path.Combine(Path.GetDirectoryName(request.ServerExecutablePath) ?? string.Empty, "llama.cpp-omni", "build", "bin", "llama-server.exe");
                    if (File.Exists(omniPath))
                    {
                        exePath = omniPath;
                        await StaticLogger.LogAsync($"[LlamaCpp] Special model detected (MiniCPM or {ggufCount} GGUFs): Using Omni-Server at {exePath}");
                    }
                    else
                    {
                        await StaticLogger.LogAsync($"[LlamaCpp] WARNING: MiniCPM/Multi-file model detected, but Omni-Server not found at '{omniPath}'. Falling back to default.");
                    }
                }

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
                this._currentBaseUrl = targetBaseUrl;

                // 4. Warten, bis der Server hochgefahren und das Modell im VRAM ist
                bool isReady = await this.WaitForServerReadyAsync(this._currentBaseUrl, TimeSpan.FromMinutes(2), cancellationToken);

                stopwatch.Stop();

                if (isReady)
                {
                    this.CurrentContextSize = request.ContextSize;
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

        public Task<LlamaModelLoadResult?> TryAttachToRunningServerAsync(string host = "127.0.0.1", int port = 8080, int contextSize = 4096, CancellationToken cancellationToken = default)
        {
            string baseUrl = $"http://{host}:{port}";
            return this.TryReuseExistingInstanceAsync(baseUrl, contextSize, cancellationToken);
        }

        private async Task<LlamaModelLoadResult?> TryReuseExistingInstanceAsync(string baseUrl, int contextSize, CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3));

                using var healthResponse = await this._httpClient.GetAsync($"{baseUrl}/health", cts.Token);
                if (!healthResponse.IsSuccessStatusCode)
                {
                    return null;
                }

                this._currentBaseUrl = baseUrl;
                this.CurrentContextSize = contextSize;
                string? activeModelId = await this.TryGetActiveModelIdAsync(baseUrl, cts.Token);

                await StaticLogger.LogAsync($"[LlamaCpp] Existing llama-server instance detected at {baseUrl}. Reusing it instead of starting a new process.");

                return new LlamaModelLoadResult
                {
                    Success = true,
                    BaseApiUrl = baseUrl,
                    LoadTime = TimeSpan.Zero,
                    ReusedExistingInstance = true,
                    ActiveModelId = activeModelId
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<string?> TryGetActiveModelIdAsync(string baseUrl, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await this._httpClient.GetAsync($"{baseUrl}/v1/models", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
                var data = json?["data"]?.AsArray();
                if (data == null || data.Count == 0)
                {
                    return null;
                }

                return data[0]?["id"]?.GetValue<string>();
            }
            catch
            {
                return null;
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

            if (this._settings.KillExistingServerInstances)
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
