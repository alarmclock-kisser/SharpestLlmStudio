using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SharpestLlmStudio.Monitoring;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        public readonly GpuMonitor? GPUMonitor = null;
        private readonly WebAppSettings _settings;
        private readonly Lock _modelsLock = new();
        private readonly Lock _conversationLock = new();
        private readonly Lock _knowledgeLock = new();
        private readonly Lock _generationStatsLock = new();
        private readonly Lock _idleMonitorLock = new();
        private System.Threading.Timer? _idleMonitorTimer;
        private DateTime _lastServerActivityUtc = DateTime.UtcNow;
        private int _activeServerRequests;
        private string? _remoteApiKey;
        private RemoteLlmProvider? _remoteProvider;
        private string? _remoteModelId;
        private string? _remoteEmbeddingModelId;
        private string? _remoteBaseUrl;

        public string AppDataDirectory { get; }
        public string ContextDirectory { get; }
        public string EmbeddingStoreDirectory { get; }
        public List<LlamaChatMessage> ConversationMessages { get; } = [];
        public List<LlamaKnowledgeEntry> KnowledgeEntries { get; } = [];
        public GenerationStats LastGenerationStats { get; private set; } = new();
        public bool IsRemoteMode => this._remoteProvider.HasValue && !string.IsNullOrWhiteSpace(this._remoteApiKey) && !string.IsNullOrWhiteSpace(this._remoteBaseUrl);
        public string RemoteProviderLabel => this._remoteProvider?.ToString() ?? string.Empty;
        public string RemoteModelId => this._remoteModelId ?? string.Empty;

        public List<string> ModelDirectories { get; set; } = [];
        public List<LlamaModelInfo> Models { get; set; } = [];



        public LlamaCppClient(WebAppSettings settings, GpuMonitor? gpuMonitor = null)
        {
            this._settings = settings;
            this.GPUMonitor = gpuMonitor;

            this.AppDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SharpestLlmStudio");
            this.ContextDirectory = Path.Combine(this.AppDataDirectory, "contexts");
            this.EmbeddingStoreDirectory = Path.Combine(this.AppDataDirectory, "embeddings");

            Directory.CreateDirectory(this.AppDataDirectory);
            Directory.CreateDirectory(this.ContextDirectory);
            Directory.CreateDirectory(this.EmbeddingStoreDirectory);


        }

        public IReadOnlyList<LlamaModelInfo> EnsureModelsLoaded()
        {
            lock (this._modelsLock)
            {
                if (this.Models.Count == 0)
                {
                    this.GetModels(this._settings.ModelDirectories?.ToArray());
                }

                return this.Models.ToList();
            }
        }

        /// <summary>
        /// Public helper: Try to fetch models from a remote provider base URL using an optional API key.
        /// Does not modify client remote-mode state permanently.
        /// </summary>
        public async Task<string[]?> FetchRemoteModelsAsync(RemoteLlmProvider provider, string? baseUrl, string? apiKey, CancellationToken cancellationToken = default)
        {
            string originalBase = this._remoteBaseUrl ?? string.Empty;
            AuthenticationHeaderValue? originalAuth = this._httpClient.DefaultRequestHeaders.Authorization;

            try
            {
                this._remoteBaseUrl = ResolveRemoteBaseUrl(provider, baseUrl);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    this._httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }

                return await TryFetchRemoteModelsAsync(cancellationToken);
            }
            finally
            {
                // restore
                this._remoteBaseUrl = originalBase;
                this._httpClient.DefaultRequestHeaders.Authorization = originalAuth;
            }
        }

        public async Task<RemoteLlmConnectionResult> ConnectRemoteAsync(RemoteLlmConnectionRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return new RemoteLlmConnectionResult { Success = false, ErrorMessage = "API key is required." };
            }

            if (string.IsNullOrWhiteSpace(request.ModelId))
            {
                return new RemoteLlmConnectionResult { Success = false, ErrorMessage = "Remote model id is required." };
            }

            string baseUrl = ResolveRemoteBaseUrl(request.Provider, request.BaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return new RemoteLlmConnectionResult { Success = false, ErrorMessage = "A valid remote base URL is required." };
            }

            this.UnloadModel();
            this.StopIdleMonitor();
            this._remoteApiKey = request.ApiKey.Trim();
            this._remoteProvider = request.Provider;
            this._remoteModelId = request.ModelId.Trim();
            this._remoteEmbeddingModelId = string.IsNullOrWhiteSpace(request.EmbeddingModelId) ? this._remoteModelId : request.EmbeddingModelId.Trim();
            this._remoteBaseUrl = baseUrl.TrimEnd('/');
            this._currentBaseUrl = this._remoteBaseUrl;
            this.CurrentContextSize = Math.Max(1024, request.ContextSizeHint);
            this.CurrentBatchSize = Math.Max(1, this._settings.DefaultBatchSize);
            this.ConfigureHttpClientForRemote();

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, this.GetRemoteChatCompletionsUrl());
                message.Content = JsonContent.Create(this.BuildRemoteTestPayload());
                using var response = await this._httpClient.SendAsync(message, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = string.Empty;
                    try { errorBody = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
                    // Try to fetch available models to give user guidance
                    string[]? available = null;
                    try
                    {
                        available = await TryFetchRemoteModelsAsync(cancellationToken);
                    }
                    catch { }

                    this.DisconnectRemote();
                    return new RemoteLlmConnectionResult
                    {
                        Success = false,
                        ErrorMessage = $"Remote test failed: {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}",
                        BaseApiUrl = baseUrl,
                        ProviderLabel = request.Provider.ToString(),
                        AvailableModels = available
                    };
                }

                this.TouchServerActivity();
                return new RemoteLlmConnectionResult
                {
                    Success = true,
                    BaseApiUrl = baseUrl,
                    ProviderLabel = request.Provider.ToString()
                };
            }
            catch (Exception ex)
            {
                this.DisconnectRemote();
                return new RemoteLlmConnectionResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    BaseApiUrl = baseUrl,
                    ProviderLabel = request.Provider.ToString()
                };
            }
        }

        public void DisconnectRemote()
        {
            this._remoteApiKey = null;
            this._remoteProvider = null;
            this._remoteModelId = null;
            this._remoteEmbeddingModelId = null;
            this._remoteBaseUrl = null;
            this._currentBaseUrl = string.Empty;
            this.CurrentContextSize = 0;
            this.CurrentBatchSize = 0;
            this._httpClient.DefaultRequestHeaders.Authorization = null;
            this._httpClient.DefaultRequestHeaders.Remove("x-goog-api-key");
        }

        private void ConfigureHttpClientForRemote()
        {
            this._httpClient.DefaultRequestHeaders.Authorization = null;
            this._httpClient.DefaultRequestHeaders.Remove("x-goog-api-key");

            if (!this.IsRemoteMode)
            {
                return;
            }

            this._httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this._remoteApiKey);
        }

        private static string ResolveRemoteBaseUrl(RemoteLlmProvider provider, string? baseUrl)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                return baseUrl.Trim();
            }

            return provider switch
            {
                RemoteLlmProvider.OpenAI => "https://api.openai.com/v1",
                RemoteLlmProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
                RemoteLlmProvider.OpenRouter => "https://openrouter.ai/api/v1",
                RemoteLlmProvider.XAI => "https://api.x.ai/v1",
                RemoteLlmProvider.CustomOpenAiCompatible => string.Empty,
                _ => string.Empty
            };
        }

        private string GetRemoteChatCompletionsUrl()
        {
            return $"{this._remoteBaseUrl}/chat/completions";
        }

        private string GetRemoteEmbeddingsUrl()
        {
            return $"{this._remoteBaseUrl}/embeddings";
        }

        private JsonObject BuildRemoteTestPayload()
        {
            return new JsonObject
            {
                ["model"] = this._remoteModelId,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "Reply with OK"
                    }
                },
                ["stream"] = false,
                ["max_tokens"] = 8,
                ["temperature"] = 0.0
            };
        }

        private async Task<string[]?> TryFetchRemoteModelsAsync(CancellationToken cancellationToken = default)
        {
            string baseUrl = (this._remoteBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return null;
            }

            var endpointSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                $"{baseUrl}/models",
                $"{baseUrl}/v1/models",
                $"{baseUrl}/v1/engines"
            };

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
            {
                string root = $"{baseUri.Scheme}://{baseUri.Host}";
                if (!baseUri.IsDefaultPort)
                {
                    root += $":{baseUri.Port}";
                }

                if (baseUri.Host.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
                {
                    endpointSet.Add($"{root}/v1beta/models");
                }
            }

            foreach (string url in endpointSet)
            {
                try
                {
                    using var resp = await this._httpClient.GetAsync(url, cancellationToken);
                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    string body = await resp.Content.ReadAsStringAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        continue;
                    }

                    try
                    {
                        var node = JsonNode.Parse(body);
                        if (node == null)
                        {
                            continue;
                        }

                        var ids = new List<string>();

                        static void AddModelCandidate(List<string> target, string? raw)
                        {
                            string candidate = (raw ?? string.Empty).Trim();
                            if (string.IsNullOrWhiteSpace(candidate))
                            {
                                return;
                            }

                            if (candidate.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                            {
                                candidate = candidate["models/".Length..];
                            }

                            if (!string.IsNullOrWhiteSpace(candidate))
                            {
                                target.Add(candidate);
                            }
                        }

                        static void ExtractModels(JsonArray source, List<string> target)
                        {
                            foreach (JsonNode? el in source)
                            {
                                if (el is JsonObject obj)
                                {
                                    AddModelCandidate(target, obj["id"]?.ToString());
                                    AddModelCandidate(target, obj["name"]?.ToString());
                                    AddModelCandidate(target, obj["model"]?.ToString());
                                    AddModelCandidate(target, obj["display_name"]?.ToString());
                                }
                                else
                                {
                                    AddModelCandidate(target, el?.ToString());
                                }
                            }
                        }

                        var data = node["data"] as JsonArray;
                        if (data != null)
                        {
                            ExtractModels(data, ids);
                        }

                        if (node is JsonArray arr)
                        {
                            ExtractModels(arr, ids);
                        }

                        var models = node["models"] as JsonArray;
                        if (models != null)
                        {
                            ExtractModels(models, ids);
                        }

                        if (ids.Count > 0)
                        {
                            return ids
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                        }
                    }
                    catch { }
                }
                catch { }
            }

            return null;
        }



        public string[] GetModels(string[]? modelDirectories)
        {
            if (modelDirectories != null)
            {
                this.ModelDirectories.AddRange(modelDirectories);
            }

            this.ModelDirectories = this.ModelDirectories.Distinct().Where(d => Directory.Exists(d)).ToList();
            string[] candidateRootDirs = this.ModelDirectories.SelectMany(d => Directory.GetDirectories(d)).Where(d => Directory.GetFiles(d, "*.gguf").Length > 0).ToArray();

            foreach (string candidateRootDir in candidateRootDirs)
            {
                try
                {
                    LlamaModelInfo modelInfo = new(candidateRootDir);
                    if (!this.Models.Any(m => m.ModelRootDirectory == modelInfo.ModelRootDirectory))
                    {
                        this.Models.Add(modelInfo);
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Error loading model from directory {candidateRootDir}: {ex.Message}");
                }
            }

            return this.Models.Select(m => m.ModelRootDirectory).ToArray();
        }

        [SupportedOSPlatform("windows")]
        public async Task<HardwareStatistics?> GetCurrentHardwareStatisticsAsync()
        {
            if (this.GPUMonitor == null)
            {
                return null;
            }

            try
            {
                return await this.GPUMonitor.GetCurrentHardwareStatisticsAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }


        public int? GetLlamaServerExeInstancesCount()
        {
            try
            {
                var processes = System.Diagnostics.Process
                    .GetProcesses()
                    .Where(p => string.Equals(p.ProcessName, "llama-server", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.ProcessName, "llama-server.exe", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.ProcessName, "LlamaServer", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return processes.Count;
            }
            catch (Exception ex)
            {
                StaticLogger.Log(ex, "Error while counting llama-server process instances");
                return null;
            }
        }


        public int? KillAllLlamaServerExeInstances()
        {
            try
            {
                var processes = System.Diagnostics.Process
                    .GetProcesses()
                    .Where(p => string.Equals(p.ProcessName, "llama-server", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.ProcessName, "llama-server.exe", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.ProcessName, "LlamaServer", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var process in processes)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit(2000);
                    }
                }

                this.ResetConversation();

                return processes.Count;
            }
            catch (Exception ex)
            {
                StaticLogger.Log(ex, "Error while killing llama-server process instances");
                return null;
            }
        }

        public GenerationStats GetLastGenerationStatsSnapshot()
        {
            lock (this._generationStatsLock)
            {
                return new GenerationStats
                {
                    GenerationStarted = this.LastGenerationStats.GenerationStarted,
                    GenerationFinished = this.LastGenerationStats.GenerationFinished,
                    TimeTilFirstToken = this.LastGenerationStats.TimeTilFirstToken,
                    TotalTokensGenerated = this.LastGenerationStats.TotalTokensGenerated,
                    UsedWattsApprox = this.LastGenerationStats.UsedWattsApprox,
                    TotalContextTokens = this.LastGenerationStats.TotalContextTokens,
                    ContextSize = this.LastGenerationStats.ContextSize
                };
            }
        }

        private void TouchServerActivity()
        {
            this._lastServerActivityUtc = DateTime.UtcNow;
        }

        private IDisposable BeginServerActivityScope()
        {
            System.Threading.Interlocked.Increment(ref this._activeServerRequests);
            this.TouchServerActivity();
            return new ServerActivityScope(this);
        }

        private void EndServerActivityScope()
        {
            System.Threading.Interlocked.Decrement(ref this._activeServerRequests);
            this.TouchServerActivity();
        }

        private void StartIdleMonitorIfEnabled()
        {
            int idleMinutes = Math.Max(0, this._settings.IdleShutdownMinutes);
            if (idleMinutes <= 0)
            {
                this.StopIdleMonitor();
                return;
            }

            int checkSeconds = Math.Max(5, this._settings.IdleCheckIntervalSeconds);

            lock (this._idleMonitorLock)
            {
                this._idleMonitorTimer?.Dispose();
                this._lastServerActivityUtc = DateTime.UtcNow;
                this._idleMonitorTimer = new System.Threading.Timer(
                    static state => ((LlamaCppClient)state!).OnIdleMonitorTick(),
                    this,
                    TimeSpan.FromSeconds(checkSeconds),
                    TimeSpan.FromSeconds(checkSeconds));
            }

            _ = StaticLogger.LogAsync($"[LlamaCpp] Idle auto-shutdown enabled: {idleMinutes} min (check every {checkSeconds}s).");
        }

        private void StopIdleMonitor()
        {
            lock (this._idleMonitorLock)
            {
                this._idleMonitorTimer?.Dispose();
                this._idleMonitorTimer = null;
            }
        }

        private void OnIdleMonitorTick()
        {
            try
            {
                int idleMinutes = Math.Max(0, this._settings.IdleShutdownMinutes);
                if (idleMinutes <= 0)
                {
                    return;
                }

                if (System.Threading.Volatile.Read(ref this._activeServerRequests) > 0)
                {
                    return;
                }

                var process = this._serverProcess;
                if (process == null || process.HasExited)
                {
                    return;
                }

                var idleFor = DateTime.UtcNow - this._lastServerActivityUtc;
                if (idleFor < TimeSpan.FromMinutes(idleMinutes))
                {
                    return;
                }

                StaticLogger.Log($"[LlamaCpp] Idle timeout reached after {idleFor.TotalMinutes:F1} min. Shutting down llama-server.");
                this.UnloadModel();
            }
            catch (Exception ex)
            {
                StaticLogger.Log(ex, "Error in llama-server idle monitor callback");
            }
        }

        private sealed class ServerActivityScope(LlamaCppClient owner) : IDisposable
        {
            private LlamaCppClient? _owner = owner;

            public void Dispose()
            {
                var o = System.Threading.Interlocked.Exchange(ref this._owner, null);
                o?.EndServerActivityScope();
            }
        }

    }
}
