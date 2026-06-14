using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Threading.Channels;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        // Holds last per-image token estimates for the most recent NormalizeImageInputsAsync call
        private static readonly Dictionary<string,int> _lastImageTokenEstimates = new(StringComparer.OrdinalIgnoreCase);
        
        [SupportedOSPlatform("windows")]
        public IAsyncEnumerable<string> GenerateAsync(string prompt, bool isolated = false, CancellationToken cancellationToken = default)
        {
            return this.GenerateAsync(new LlamaGenerationRequest
            {
                Prompt = prompt,
                Isolated = isolated,
                MaxTokens = this._settings.DefaultMaxTokens,
                Temperature = this._settings.DefaultTemperature,
                TopP = 0.9,
                Stream = true
            }, cancellationToken);
        }

        [SupportedOSPlatform("windows")]
        public IAsyncEnumerable<string> GenerateAsync(string prompt, string[]? images, bool isolated = false, CancellationToken cancellationToken = default)
        {
            return this.GenerateAsync(new LlamaGenerationRequest
            {
                Prompt = prompt,
                Images = images,
                Isolated = isolated,
                MaxTokens = this._settings.DefaultMaxTokens,
                Temperature = this._settings.DefaultTemperature,
                TopP = 0.9,
                Stream = true
            }, cancellationToken);
        }

        [SupportedOSPlatform("windows")]
        public async IAsyncEnumerable<string> GenerateAsync(LlamaGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!this.IsServerRunning || string.IsNullOrWhiteSpace(this.CurrentBaseUrl))
            {
                throw new InvalidOperationException("llama.cpp server is not running. Load a model first.");
            }

            using var activityScope = this.BeginServerActivityScope();

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                yield break;
            }

            var normalizedImages = await this.NormalizeImageInputsAsync(request, cancellationToken);
            try
            {
                int sum = 0;
                foreach (var kv in _lastImageTokenEstimates)
                {
                    sum += kv.Value;
                }
                if (sum > 0)
                {
                    var perImage = _lastImageTokenEstimates.Values.Select((tokens, index) => $"img{index + 1}:{tokens}");
                    await StaticLogger.LogAsync($"[LlamaCpp] Image inputs estimated total ~{sum} tokens across {_lastImageTokenEstimates.Count} item(s) — estimates per image: {string.Join(", ", perImage)}");
                }
            }
            catch { }
            string assistantText = string.Empty;
            int maxRetries = 5;
            int retryCount = 0;
            bool powerProfilingStarted = false;
            bool isRemoteMode = this.IsRemoteMode;

            try
            {
                this.GPUMonitor?.RestartPowerProfiling();
                powerProfilingStarted = this.GPUMonitor != null;
            }
            catch
            {
            }

            lock (this._generationStatsLock)
            {
                this.LastGenerationStats = new GenerationStats
                {
                    GenerationStarted = DateTime.UtcNow,
                    GenerationFinished = null,
                    TimeTilFirstToken = 0.0,
                    TotalTokensGenerated = 0,
                    TotalContextTokens = 0,
                    ContextSize = this.CurrentContextSize
                };
            }

            var streamChannel = Channel.CreateUnbounded<string>();

            _ = Task.Run(async () =>
            {
                bool completed = false;
                bool emittedChunks = false;

                try
                {
                    while (retryCount <= maxRetries)
                    {
                        // For local multimodal requests, use the /completion endpoint with image_data
                        // to bypass the Jinja template parser that chokes on base64 data URLs.
                        bool useLocalCompletion = !isRemoteMode && normalizedImages.Count > 0;
                        if (useLocalCompletion && retryCount == 0)
                        {
                            await StaticLogger.LogAsync($"[LlamaCpp] Using local /completion endpoint for multimodal request ({normalizedImages.Count} image(s)).");
                        }
                        var payload = useLocalCompletion
                            ? this.BuildLocalCompletionPayload(request, normalizedImages)
                            : this.BuildChatCompletionPayload(request, normalizedImages);
                        string promptEstimate = useLocalCompletion
                            ? (payload["prompt"]?.GetValue<string>() ?? "")
                            : (payload["messages"]?.ToJsonString() ?? "");
                        completed = false;

                        try
                        {
                            if (request.Stream)
                            {
                                IAsyncEnumerable<string> chunks = useLocalCompletion
                                    ? this.StreamLocalCompletionChunksAsync(payload, cancellationToken)
                                    : this.StreamChatCompletionChunksAsync(payload, cancellationToken);

                                await foreach (var chunk in chunks)
                                {
                                    this.TouchServerActivity();
                                    assistantText += chunk;
                                    emittedChunks = true;
                                    await streamChannel.Writer.WriteAsync(chunk, cancellationToken);

                                    lock (this._generationStatsLock)
                                    {
                                        if (this.LastGenerationStats.TimeTilFirstToken <= 0.0 && this.LastGenerationStats.GenerationStarted.HasValue)
                                        {
                                            this.LastGenerationStats.TimeTilFirstToken = Math.Max(0.0, (DateTime.UtcNow - this.LastGenerationStats.GenerationStarted.Value).TotalSeconds);
                                        }

                                        this.LastGenerationStats.TotalTokensGenerated = CountRoughTokens(assistantText);
                                        this.LastGenerationStats.TotalContextTokens = CountRoughTokens(promptEstimate) + CountRoughTokens(assistantText);
                                        this.LastGenerationStats.GenerationFinished = null;
                                    }
                                }
                            }
                            else
                            {
                                assistantText = useLocalCompletion
                                    ? await this.GenerateLocalCompletionAsync(payload, cancellationToken)
                                    : await this.GenerateSingleChatCompletionAsync(payload, cancellationToken);
                                this.TouchServerActivity();

                                lock (this._generationStatsLock)
                                {
                                    if (this.LastGenerationStats.TimeTilFirstToken <= 0.0)
                                    {
                                        this.LastGenerationStats.TimeTilFirstToken = 0.0;
                                    }

                                    this.LastGenerationStats.TotalTokensGenerated = CountRoughTokens(assistantText);
                                    this.LastGenerationStats.TotalContextTokens = CountRoughTokens(promptEstimate) + CountRoughTokens(assistantText);
                                }

                                if (!string.IsNullOrEmpty(assistantText))
                                {
                                    emittedChunks = true;
                                    await streamChannel.Writer.WriteAsync(assistantText, cancellationToken);
                                }
                            }

                            completed = true;
                        }
                        catch (HttpRequestException ex) when (!isRemoteMode && ex.Message.Contains("400") && ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase) && retryCount < maxRetries && !emittedChunks)
                        {
                            retryCount++;
                            await StaticLogger.LogAsync($"[LlamaCpp] Context overflow on attempt {retryCount}, trimming oldest messages and retrying...");

                            lock (this._conversationLock)
                            {
                                int removed = 0;
                                while (removed < 2 && this.ConversationMessages.Count > 0)
                                {
                                    this.ConversationMessages.RemoveAt(0);
                                    removed++;
                                }
                            }

                            if (this.ConversationMessages.Count == 0)
                            {
                                throw;
                            }

                            assistantText = string.Empty;
                            continue;
                        }
                        catch (System.IO.IOException ioEx)
                        {
                            bool serverDead = this._serverProcess != null && this._serverProcess.HasExited;
                            string detail = serverDead
                                ? $"llama-server process has exited (exit code {this._serverProcess!.ExitCode}). The model may have run out of memory."
                                : "llama-server closed the connection unexpectedly.";
                            await StaticLogger.LogAsync($"[LlamaCpp] IOException during generation: {ioEx.Message} — {detail}");
                            throw new InvalidOperationException($"{detail} ({ioEx.Message})", ioEx);
                        }
                        catch (HttpRequestException httpEx) when (httpEx.InnerException is System.IO.IOException)
                        {
                            bool serverDead = this._serverProcess != null && this._serverProcess.HasExited;
                            string detail = serverDead
                                ? $"llama-server process has exited (exit code {this._serverProcess!.ExitCode}). The model may have run out of memory."
                                : "llama-server closed the connection unexpectedly.";
                            await StaticLogger.LogAsync($"[LlamaCpp] Connection lost during generation: {httpEx.Message} — {detail}");
                            throw new InvalidOperationException($"{detail} ({httpEx.Message})", httpEx);
                        }

                        if (completed)
                        {
                            break;
                        }
                    }

                    if (!request.Isolated && request.PersistConversation)
                    {
                        lock (this._conversationLock)
                        {
                            this.ConversationMessages.Add(new LlamaChatMessage { Role = "user", Content = request.Prompt });
                            this.ConversationMessages.Add(new LlamaChatMessage { Role = "assistant", Content = assistantText });
                        }
                    }

                    streamChannel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    try { await this.ClearServerContextAsync(cancellationToken: cancellationToken); } catch { }
                    streamChannel.Writer.TryComplete(ex);
                }
                finally
                {
                    double? usedWattsApprox = null;

                    if (powerProfilingStarted)
                    {
                        try
                        {
                            usedWattsApprox = this.GPUMonitor?.EndPowerProfiling();
                        }
                        catch
                        {
                        }
                    }

                    lock (this._generationStatsLock)
                    {
                        this.LastGenerationStats.GenerationFinished = DateTime.UtcNow;
                        this.LastGenerationStats.UsedWattsApprox = usedWattsApprox;
                        GenerationStats.AddCompletedGeneration(
                            usedWattsApprox,
                            this.LastGenerationStats.TotalGenerationTime,
                            this._settings.PricePerKiloWattHour);
                    }
                }
            }, cancellationToken);

            await foreach (var chunk in streamChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return chunk;
            }
        }

        private JsonObject BuildChatCompletionPayload(LlamaGenerationRequest request, List<string> normalizedImages)
        {
            var messages = new JsonArray();

            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = request.SystemPrompt
                });
            }

            if (!request.Isolated && request.IncludeConversationHistory)
            {
                lock (this._conversationLock)
                {
                    // Trim old messages to avoid exceeding context window
                    // Rough estimate: ~3 chars per token (conservative). Reserve space for system prompt, current prompt, generation tokens, and overhead.
                    int systemPromptTokens = string.IsNullOrWhiteSpace(request.SystemPrompt) ? 0 : (request.SystemPrompt.Length / 3) + 16;
                    int currentPromptTokens = (request.Prompt.Length / 3) + 16;
                    int reservedTokens = request.MaxTokens + systemPromptTokens + currentPromptTokens + 256;
                    int availableContextTokens = Math.Max(128, this.CurrentContextSize - reservedTokens);
                    int maxHistoryChars = availableContextTokens * 3;
                    int totalChars = 0;
                    var historyMessages = this.ConversationMessages
                        .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                        .ToList();

                    // Take messages from newest to oldest, then reverse
                    var trimmed = new List<LlamaChatMessage>();
                    for (int i = historyMessages.Count - 1; i >= 0; i--)
                    {
                        int msgLen = historyMessages[i].Content?.Length ?? 0;
                        if (totalChars + msgLen > maxHistoryChars && trimmed.Count > 0)
                        {
                            break;
                        }

                        totalChars += msgLen;
                        trimmed.Add(historyMessages[i]);
                    }

                    trimmed.Reverse();

                    // Ensure strict user/assistant alternation required by chat templates (e.g. Gemma).
                    // After trimming, the first message may be "assistant" — drop leading non-user messages,
                    // then merge any consecutive same-role messages so the sequence always alternates.
                    while (trimmed.Count > 0 && !string.Equals(trimmed[0].Role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        trimmed.RemoveAt(0);
                    }

                    var sanitized = new List<LlamaChatMessage>();
                    foreach (var msg in trimmed)
                    {
                        if (sanitized.Count > 0 && string.Equals(sanitized[^1].Role, msg.Role, StringComparison.OrdinalIgnoreCase))
                        {
                            // Merge consecutive same-role messages
                            sanitized[^1] = new LlamaChatMessage
                            {
                                Role = sanitized[^1].Role,
                                Content = sanitized[^1].Content + "\n" + msg.Content,
                                CreatedAtUtc = sanitized[^1].CreatedAtUtc
                            };
                        }
                        else
                        {
                            sanitized.Add(msg);
                        }
                    }

                    // If the last history message is "user", drop it — we'll add the current user prompt next
                    if (sanitized.Count > 0 && string.Equals(sanitized[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        sanitized.RemoveAt(sanitized.Count - 1);
                    }

                    foreach (var message in sanitized)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = message.Role,
                            ["content"] = message.Content
                        });
                    }
                }
            }

            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = this.BuildUserContent(request.Prompt, normalizedImages)
            });

            var payload = new JsonObject
            {
                ["messages"] = messages,
                ["stream"] = request.Stream,
                ["temperature"] = request.Temperature,
                ["top_p"] = request.TopP,
                ["cache_prompt"] = !request.Isolated
            };

            // Filter payload fields according to remote provider capabilities when in remote mode
            if (this.IsRemoteMode)
            {
                var caps = RemoteProviderCapabilities.Get(this._remoteProvider ?? RemoteLlmProvider.CustomOpenAiCompatible, isLocal: false);
                // If provider does not allow top_k, ensure it's not present
                if (!caps.AllowTopK && payload.ContainsKey("top_k")) payload.Remove("top_k");
                // repetition_penalty handled below
            }
            else
            {
                // local case: include top_k
                payload["top_k"] = request.TopK;
            }

            // Only include provider-specific inference params when talking to local llama.cpp
            // Remote providers (e.g. Gemini/OpenAI-compatible endpoints) often reject fields like top_k or repetition_penalty.
            if (!this.IsRemoteMode)
            {
                payload["top_k"] = request.TopK;
            }

            if (this.IsRemoteMode && !string.IsNullOrWhiteSpace(this._remoteModelId))
            {
                payload["model"] = this._remoteModelId;
                payload.Remove("cache_prompt");
            }

            // 0 = unlimited (omit max_tokens)
            if (request.MaxTokens > 0)
            {
                payload["max_tokens"] = Math.Max(1, request.MaxTokens);
            }

            // Repetition penalty (optional) - only include if allowed by provider
            if (request.RepetitionPenalty > 0.0 && Math.Abs(request.RepetitionPenalty - 1.0) > 1e-9)
            {
                if (!this.IsRemoteMode)
                {
                    payload["repetition_penalty"] = request.RepetitionPenalty;
                }
                else
                {
                    var caps = RemoteProviderCapabilities.Get(this._remoteProvider ?? RemoteLlmProvider.CustomOpenAiCompatible, isLocal: false);
                    if (caps.AllowRepetitionPenalty)
                    {
                        payload["repetition_penalty"] = request.RepetitionPenalty;
                    }
                }
            }

            if (request.StopSequences is { Length: > 0 })
            {
                var stop = new JsonArray();
                foreach (var sequence in request.StopSequences.Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    stop.Add(sequence);
                }

                if (stop.Count > 0)
                {
                    payload["stop"] = stop;
                }
            }

            return payload;
        }

        private JsonNode BuildUserContent(string prompt, List<string> normalizedImages)
        {
            if (normalizedImages.Count == 0)
            {
                return JsonValue.Create(prompt)!;
            }

            var contentArray = new JsonArray();

            // Images FIRST — VL model templates (Qwen3VL, LLaVA, etc.) expect image
            // content parts before the text part in the content array.
            foreach (var image in normalizedImages)
            {
                contentArray.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = image
                    }
                });
            }

            contentArray.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = prompt
            });

            return contentArray;
        }

        /// <summary>
        /// Builds a payload for the llama-server <c>/completion</c> endpoint with <c>image_data</c>.
        /// This bypasses the Jinja template rendering in <c>/v1/chat/completions</c> which fails
        /// with "Failed to parse input" when base64 data URLs are embedded in multimodal content arrays.
        /// </summary>

        private JsonObject BuildLocalCompletionPayload(LlamaGenerationRequest request, List<string> normalizedImages)
        {
            // Build a ChatML-style prompt manually
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                sb.Append("<|im_start|>system\n");
                sb.Append(request.SystemPrompt);
                sb.Append("<|im_end|>\n");
            }

            if (!request.Isolated && request.IncludeConversationHistory)
            {
                lock (this._conversationLock)
                {
                    foreach (var msg in this.ConversationMessages.Where(m => !string.IsNullOrWhiteSpace(m.Content)))
                    {
                        sb.Append($"<|im_start|>{msg.Role}\n");
                        sb.Append(msg.Content);
                        sb.Append("<|im_end|>\n");
                    }
                }
            }

            sb.Append("<|im_start|>user\n");

            // 1. Image Placeholders injizieren
            for (int i = 0; i < normalizedImages.Count; i++)
            {
                sb.Append($"[img-{i + 1}]");
            }

            // 2. WICHTIG: Audio Placeholders injizieren (fehlte vorher!)
            if (request.Audios != null)
            {
                for (int i = 0; i < request.Audios.Length; i++)
                {
                    sb.Append($"[aud-{i + 1}]");
                }
            }

            sb.Append(request.Prompt);
            sb.Append("<|im_end|>\n");
            sb.Append("<|im_start|>assistant\n");

            // Payload zusammenbauen und saubere Hilfsmethode für Base64 nutzen
            var payload = new JsonObject
            {
                ["prompt"] = sb.ToString(),
                ["image_data"] = ExtractMediaData(normalizedImages),
                ["audio_data"] = ExtractMediaData(request.Audios),
                ["stream"] = request.Stream,
                ["temperature"] = request.Temperature,
                ["top_p"] = request.TopP,
                ["top_k"] = request.TopK,
                ["cache_prompt"] = !request.Isolated
            };

            if (request.MaxTokens > 0)
            {
                payload["n_predict"] = Math.Max(1, request.MaxTokens);
            }

            if (request.RepetitionPenalty > 0.0 && Math.Abs(request.RepetitionPenalty - 1.0) > 1e-9)
            {
                payload["repeat_penalty"] = request.RepetitionPenalty;
            }

            // Stop Sequences sauber verarbeiten
            if (request.StopSequences is { Length: > 0 })
            {
                var stop = new JsonArray();
                foreach (var seq in request.StopSequences.Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    stop.Add(seq);
                }
                if (stop.Count > 0)
                {
                    payload["stop"] = stop;
                }
            }

            // Always stop at end-of-turn for ChatML
            if (!payload.ContainsKey("stop"))
            {
                payload["stop"] = new JsonArray { "<|im_end|>" };
            }
            else if (payload["stop"] is JsonArray existingStop)
            {
                bool hasImEnd = existingStop.Any(item => item?.GetValue<string>() == "<|im_end|>");
                if (!hasImEnd) existingStop.Add("<|im_end|>");
            }

            return payload;
        }

        /// <summary>
        /// Hilfsmethode nach Clean Code Prinzipien, um den Data-URL-Header abzuschneiden 
        /// und das llama.cpp kompatible ID/Data-Format zu erzeugen.
        /// </summary>
        private JsonArray ExtractMediaData(IEnumerable<string>? mediaItems)
        {
            var dataArray = new JsonArray();
            if (mediaItems == null) return dataArray;

            int currentId = 1;
            foreach (var rawItem in mediaItems)
            {
                if (string.IsNullOrWhiteSpace(rawItem)) continue;

                int commaIdx = rawItem.IndexOf(',');
                string base64 = (commaIdx >= 0) ? rawItem[(commaIdx + 1)..] : rawItem;

                dataArray.Add(new JsonObject
                {
                    ["data"] = base64,
                    ["id"] = currentId++
                });
            }

            return dataArray;
        }
        private async Task<string> GenerateLocalCompletionAsync(JsonObject payload, CancellationToken cancellationToken)
        {
            string url = $"{this.CurrentBaseUrl}/completion";
            using var response = await this._httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = string.Empty;
                try { errorBody = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
                throw new HttpRequestException($"llama.cpp returned {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            var content = json?["content"]?.GetValue<string>();
            if (content != null && content.Contains("<|im_end|>"))
            {
                content = content.Replace("<|im_end|>", "");
            }
            return content?.TrimEnd() ?? string.Empty;
        }

        private async IAsyncEnumerable<string> StreamLocalCompletionChunksAsync(JsonObject payload, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string url = $"{this.CurrentBaseUrl}/completion";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload)
            };

            using var response = await this._httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = string.Empty;
                try { errorBody = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
                throw new HttpRequestException($"llama.cpp returned {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) yield break;

                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var data = line[5..].Trim();
                if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    yield break;

                JsonObject? json;
                try { json = JsonNode.Parse(data)?.AsObject(); }
                catch { continue; }

                var delta = json?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(delta))
                {
                    // Stop if the model emits the end-of-turn token in the streamed text
                    if (delta.Contains("<|im_end|>"))
                    {
                        var trimmed = delta.Replace("<|im_end|>", "").TrimEnd();
                        if (!string.IsNullOrEmpty(trimmed)) yield return trimmed;
                        yield break;
                    }

                    yield return delta;
                }

                bool stop = json?["stop"]?.GetValue<bool>() == true;
                if (stop) yield break;
            }
        }

        private async Task<string> GenerateSingleChatCompletionAsync(JsonObject payload, CancellationToken cancellationToken)
        {
            string url = this.IsRemoteMode ? this.GetRemoteChatCompletionsUrl() : $"{this.CurrentBaseUrl}/v1/chat/completions";
            int maxAttempts = this.IsRemoteMode ? 3 : 1;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var response = await this._httpClient.PostAsJsonAsync(url, payload, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = string.Empty;
                    try { errorBody = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }

                    if (this.IsRemoteMode
                        && attempt < maxAttempts
                        && IsTransientRemoteStatus(response.StatusCode))
                    {
                        await StaticLogger.LogAsync($"[LlamaCpp] Remote transient error {(int)response.StatusCode} ({response.ReasonPhrase}) on attempt {attempt}/{maxAttempts}. Retrying.");
                        await Task.Delay(250 * attempt, cancellationToken);
                        continue;
                    }

                    string upstreamLabel = this.IsRemoteMode ? "Remote provider" : "llama.cpp";

                    // Try to parse remote error and extract field violations for better UI hints
                    try
                    {
                        var node = JsonNode.Parse(errorBody);
                        if (node != null && node["error"] is JsonObject err)
                        {
                            string msg = err["message"]?.GetValue<string>() ?? errorBody;
                            var violations = new List<string>();
                            if (err["details"] is JsonArray detailsArr)
                            {
                                foreach (var d in detailsArr)
                                {
                                    try
                                    {
                                        var fv = d?[@"@type"]?.ToString();
                                        if (d is JsonObject dobj && dobj["fieldViolations"] is JsonArray fvArr)
                                        {
                                            foreach (var fvObj in fvArr)
                                            {
                                                var desc = fvObj?["description"]?.GetValue<string>();
                                                if (!string.IsNullOrWhiteSpace(desc))
                                                {
                                                    // try to extract field name from description text
                                                    var m1 = System.Text.RegularExpressions.Regex.Match(desc, "\\\"([^\\\"]+)\\\"");
                                                    if (m1.Success)
                                                    {
                                                        violations.Add(m1.Groups[1].Value);
                                                    }
                                                    else
                                                    {
                                                        violations.Add(desc);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }

                            string combined = !string.IsNullOrWhiteSpace(msg) ? msg : errorBody;
                            if (violations.Count > 0)
                            {
                                combined += " Field violations: " + string.Join(", ", violations.Distinct());
                            }

                            throw new HttpRequestException($"{upstreamLabel} returned {(int)response.StatusCode} ({response.ReasonPhrase}). {combined}");
                        }
                    }
                    catch
                    {
                        // fall back to full body
                    }

                    throw new HttpRequestException($"{upstreamLabel} returned {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}");
                }

                var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
                var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                return content ?? string.Empty;
            }

            throw new HttpRequestException("Remote provider returned no successful response after retries.");
        }

        private async IAsyncEnumerable<string> StreamChatCompletionChunksAsync(JsonObject payload, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string url = this.IsRemoteMode ? this.GetRemoteChatCompletionsUrl() : $"{this.CurrentBaseUrl}/v1/chat/completions";
            int maxAttempts = this.IsRemoteMode ? 3 : 1;
            HttpResponseMessage? response = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(payload)
                };

                response = await this._httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }

                string errorBody = string.Empty;
                try { errorBody = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }

                if (this.IsRemoteMode
                    && attempt < maxAttempts
                    && IsTransientRemoteStatus(response.StatusCode))
                {
                    await StaticLogger.LogAsync($"[LlamaCpp] Remote transient stream error {(int)response.StatusCode} ({response.ReasonPhrase}) on attempt {attempt}/{maxAttempts}. Retrying.");
                    response.Dispose();
                    response = null;
                    await Task.Delay(250 * attempt, cancellationToken);
                    continue;
                }

                string upstreamLabel = this.IsRemoteMode ? "Remote provider" : "llama.cpp";
                throw new HttpRequestException($"{upstreamLabel} returned {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}");
            }

            if (response == null)
            {
                throw new HttpRequestException("Remote provider returned no successful response after retries.");
            }

            using (response)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null)
                    {
                        yield break;
                    }

                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var data = line[5..].Trim();
                    if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    {
                        yield break;
                    }

                    JsonObject? json;
                    try
                    {
                        json = JsonNode.Parse(data)?.AsObject();
                    }
                    catch
                    {
                        continue;
                    }

                    var delta = json?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        yield return delta;
                    }
                }
            }
        }

        private static bool IsTransientRemoteStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.TooManyRequests
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout;
        }

        [SupportedOSPlatform("windows")]
        private async Task<List<string>> NormalizeImageInputsAsync(LlamaGenerationRequest request, CancellationToken cancellationToken)
        {
            var result = new List<string>();
            _lastImageTokenEstimates.Clear();

            var images = request.Images;
            if (images == null || images.Length == 0)
            {
                return result;
            }

            string targetFormat = string.IsNullOrWhiteSpace(request.ImageFormat)
                ? "jpg"
                : request.ImageFormat.Trim().ToLowerInvariant();
            string outputMime = GetMimeTypeByImageFormat(targetFormat);

            foreach (var item in images)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                var trimmed = item.Trim();
                if (File.Exists(trimmed))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var maxDim = request.MaxWidthAndHeight;
                        // If maxDim is 0, treat as disabled (no downsizing)
                        int? maxDimForCall = (maxDim <= 0) ? null : maxDim;
                        var serialized = await this.LoadAndSerializeImagesAsync(
                            [trimmed],
                            maxDimForCall,
                            maxDimForCall,
                            targetFormat);

                        foreach (var base64 in serialized)
                        {
                            string dataUrl = $"data:{outputMime};base64,{base64}";
                            result.Add(dataUrl);
                            TryEstimateTokensForBase64(base64, dataUrl);
                        }
                    }
                    else
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(trimmed, cancellationToken);
                        string mime = GetMimeTypeByFileExtension(trimmed);
                        string dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                        result.Add(dataUrl);
                    }

                    continue;
                }

                if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    if (OperatingSystem.IsWindows() && TryParseImageDataUrl(trimmed, out var mimeType, out var imageBytes))
                    {
                        string extension = GetExtensionForMimeType(mimeType);
                        string tempFile = Path.Combine(Path.GetTempPath(), $"llm_img_{Guid.NewGuid():N}{extension}");

                        try
                        {
                            await File.WriteAllBytesAsync(tempFile, imageBytes, cancellationToken);
                            var maxDim2 = request.MaxWidthAndHeight;
                            int? maxDimForCall2 = (maxDim2 <= 0) ? null : maxDim2;
                            var serialized = await this.LoadAndSerializeImagesAsync(
                                [tempFile],
                                maxDimForCall2,
                                maxDimForCall2,
                                targetFormat);

                            foreach (var base64 in serialized)
                            {
                                string dataUrl = $"data:{outputMime};base64,{base64}";
                                result.Add(dataUrl);
                                TryEstimateTokensForBase64(base64, dataUrl);
                            }
                        }
                        finally
                        {
                            try
                            {
                                if (File.Exists(tempFile))
                                {
                                    File.Delete(tempFile);
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        result.Add(trimmed);
                    }

                    continue;
                }

                if (LooksLikeBase64(trimmed))
                {
                    result.Add($"data:image/jpeg;base64,{trimmed}");
                    continue;
                }

                result.Add(trimmed);
            }

            return result;
        }

        [SupportedOSPlatform("windows")]
        private static void TryEstimateTokensForBase64(string base64, string key)
        {
            try
            {
                using var ms = new MemoryStream(Convert.FromBase64String(base64));
                using var img = Image.FromStream(ms);
                _lastImageTokenEstimates[key] = EstimateTokensForImageDimensions(img.Width, img.Height);
            }
            catch
            {
            }
        }

        private static bool LooksLikeBase64(string value)
        {
            if (value.Length < 16 || value.Contains(" "))
            {
                return false;
            }

            try
            {
                _ = Convert.FromBase64String(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetMimeTypeByFileExtension(string filePath)
        {
            return Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".tif" => "image/tiff",
                ".tiff" => "image/tiff",
                _ => "image/jpeg"
            };
        }

        private static string GetMimeTypeByImageFormat(string format)
        {
            return format.ToLowerInvariant() switch
            {
                "png" => "image/png",
                "bmp" => "image/bmp",
                "gif" => "image/gif",
                _ => "image/jpeg"
            };
        }

        private static string GetExtensionForMimeType(string mimeType)
        {
            return mimeType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/bmp" => ".bmp",
                "image/gif" => ".gif",
                "image/tif" => ".tif",
                "image/tiff" => ".tiff",
                _ => ".jpg"
            };
        }

        private static bool TryParseImageDataUrl(string dataUrl, out string mimeType, out byte[] imageBytes)
        {
            mimeType = string.Empty;
            imageBytes = [];

            try
            {
                int commaIdx = dataUrl.IndexOf(',');
                if (commaIdx <= 0)
                {
                    return false;
                }

                string meta = dataUrl[..commaIdx];
                if (!meta.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string header = meta[5..];
                int semicolonIdx = header.IndexOf(';');
                mimeType = semicolonIdx >= 0 ? header[..semicolonIdx] : header;
                imageBytes = Convert.FromBase64String(dataUrl[(commaIdx + 1)..]);
                return !string.IsNullOrWhiteSpace(mimeType) && imageBytes.Length > 0;
            }
            catch
            {
                mimeType = string.Empty;
                imageBytes = [];
                return false;
            }
        }

        private static int EstimateTokensForImageDimensions(int width, int height)
        {
            int patch = 14;
            int baseTokens = Math.Max(1,
                (int)Math.Ceiling(width / (double)patch) *
                (int)Math.Ceiling(height / (double)patch));
            return Math.Max(1, baseTokens);
        }

        private static int CountRoughTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            return text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }
}
