using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
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

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                yield break;
            }

            var normalizedImages = await NormalizeImageInputsAsync(request.Images, cancellationToken);
            try
            {
                int sum = 0;
                foreach (var kv in _lastImageTokenEstimates)
                {
                    sum += kv.Value;
                }
                if (sum > 0)
                {
                    await StaticLogger.LogAsync($"[LlamaCpp] Image inputs estimated total ~{sum} tokens across {_lastImageTokenEstimates.Count} item(s) — estimates per image: {string.Join(", ", _lastImageTokenEstimates.Select(kv => kv.Key + ":" + kv.Value))}");
                }
            }
            catch { }
            string assistantText = string.Empty;
            int maxRetries = 5;
            int retryCount = 0;

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

            var outputChunks = new List<string>();
            bool completed = false;

            try
            {
                while (retryCount <= maxRetries)
                {
                    var payload = this.BuildChatCompletionPayload(request, normalizedImages);
                    outputChunks.Clear();
                    completed = false;

                    try
                    {
                        if (request.Stream)
                        {
                            await foreach (var chunk in this.StreamChatCompletionChunksAsync(payload, cancellationToken))
                            {
                                assistantText += chunk;
                                outputChunks.Add(chunk);

                                lock (this._generationStatsLock)
                                {
                                    if (this.LastGenerationStats.TimeTilFirstToken <= 0.0 && this.LastGenerationStats.GenerationStarted.HasValue)
                                    {
                                        this.LastGenerationStats.TimeTilFirstToken = Math.Max(0.0, (DateTime.UtcNow - this.LastGenerationStats.GenerationStarted.Value).TotalSeconds);
                                    }

                                    this.LastGenerationStats.TotalTokensGenerated = CountRoughTokens(assistantText);
                                    this.LastGenerationStats.TotalContextTokens = CountRoughTokens(payload["messages"]?.ToJsonString() ?? "") + CountRoughTokens(assistantText);
                                    this.LastGenerationStats.GenerationFinished = null;
                                }
                            }
                        }
                        else
                        {
                            assistantText = await this.GenerateSingleChatCompletionAsync(payload, cancellationToken);

                            lock (this._generationStatsLock)
                            {
                                if (this.LastGenerationStats.TimeTilFirstToken <= 0.0)
                                {
                                    this.LastGenerationStats.TimeTilFirstToken = 0.0;
                                }

                                this.LastGenerationStats.TotalTokensGenerated = CountRoughTokens(assistantText);
                                this.LastGenerationStats.TotalContextTokens = CountRoughTokens(payload["messages"]?.ToJsonString() ?? "") + CountRoughTokens(assistantText);
                            }

                            if (!string.IsNullOrEmpty(assistantText))
                            {
                                outputChunks.Add(assistantText);
                            }
                        }

                        completed = true;
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("400") && ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase) && retryCount < maxRetries)
                    {
                        // Context overflow — trim oldest conversation messages (ring buffer) and retry
                        retryCount++;
                        await StaticLogger.LogAsync($"[LlamaCpp] Context overflow on attempt {retryCount}, trimming oldest messages and retrying...");

                        lock (this._conversationLock)
                        {
                            // Remove the 2 oldest non-system messages (1 user + 1 assistant pair)
                            int removed = 0;
                            while (removed < 2 && this.ConversationMessages.Count > 0)
                            {
                                this.ConversationMessages.RemoveAt(0);
                                removed++;
                            }
                        }

                        if (this.ConversationMessages.Count == 0)
                        {
                            // No more history to trim — re-throw
                            throw;
                        }

                        assistantText = string.Empty;
                        continue;
                    }
                    catch (System.IO.IOException ioEx)
                    {
                        // Server closed the connection (likely crashed, e.g. OOM or internal error)
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
            }
            finally
            {
                // Always finalize generation stats so the timer stops, even on exceptions
                lock (this._generationStatsLock)
                {
                    this.LastGenerationStats.GenerationFinished = DateTime.UtcNow;
                }
            }

            foreach (var chunk in outputChunks)
            {
                yield return chunk;
            }

            if (!request.Isolated && request.PersistConversation)
            {
                lock (this._conversationLock)
                {
                    this.ConversationMessages.Add(new LlamaChatMessage { Role = "user", Content = request.Prompt });
                    this.ConversationMessages.Add(new LlamaChatMessage { Role = "assistant", Content = assistantText });
                }
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

                    foreach (var message in trimmed)
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
                ["content"] = BuildUserContent(request.Prompt, normalizedImages)
            });

            var payload = new JsonObject
            {
                ["messages"] = messages,
                ["stream"] = request.Stream,
                ["temperature"] = request.Temperature,
                ["top_p"] = request.TopP,
                ["max_tokens"] = Math.Max(1, request.MaxTokens),
                ["cache_prompt"] = !request.Isolated
            };

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

        private static JsonNode BuildUserContent(string prompt, List<string> normalizedImages)
        {
            if (normalizedImages.Count == 0)
            {
                return JsonValue.Create(prompt)!;
            }

            var contentArray = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = prompt
                }
            };

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

            return contentArray;
        }

        private async Task<string> GenerateSingleChatCompletionAsync(JsonObject payload, CancellationToken cancellationToken)
        {
            using var response = await this._httpClient.PostAsJsonAsync($"{this.CurrentBaseUrl}/v1/chat/completions", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = string.Empty;
                try { errorBody = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
                throw new HttpRequestException($"llama.cpp returned {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            return content ?? string.Empty;
        }

        private async IAsyncEnumerable<string> StreamChatCompletionChunksAsync(JsonObject payload, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{this.CurrentBaseUrl}/v1/chat/completions")
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

        [SupportedOSPlatform("windows")]
        private static async Task<List<string>> NormalizeImageInputsAsync(string[]? images, CancellationToken cancellationToken)
        {
            var result = new List<string>();
            if (images == null || images.Length == 0)
            {
                return result;
            }

            foreach (var item in images)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                var trimmed = item.Trim();
                if (File.Exists(trimmed))
                {
                    string ext = Path.GetExtension(trimmed).ToLowerInvariant();
                    if (ext is ".tif" or ".tiff")
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            var frames = ExtractTiffFramesAsBase64(trimmed);
                            result.AddRange(frames);

                            // Estimate tokens for each extracted frame and record
                            try
                            {
                                using var img = Image.FromFile(trimmed);
                                int frameCount = GetTiffFrameCount(img);
                                for (int i = 0; i < frameCount; i++)
                                {
                                    // use same estimation logic as HomeViewModel (patch size 14)
                                    int w = img.Width;
                                    int h = img.Height;
                                    int estimated = EstimateTokensForImageDimensions(w, h);
                                    _lastImageTokenEstimates[$"{trimmed}#frame{i}"] = estimated;
                                }
                            }
                            catch { }
                        }
                        else
                        {
                            byte[] tiffBytes = await File.ReadAllBytesAsync(trimmed, cancellationToken);
                            result.Add($"data:image/tiff;base64,{Convert.ToBase64String(tiffBytes)}");
                        }

                        continue;
                    }

                    byte[] bytes = await File.ReadAllBytesAsync(trimmed, cancellationToken);
                    string mime = GetMimeTypeByFileExtension(trimmed);
                    result.Add($"data:{mime};base64,{Convert.ToBase64String(bytes)}");
                    continue;
                }

                if (trimmed.StartsWith("data:image/tiff", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("data:image/tif", StringComparison.OrdinalIgnoreCase))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var frames = ExtractTiffFramesFromDataUrl(trimmed);
                        foreach (var f in frames)
                        {
                            result.Add(f);
                            try
                            {
                                using var ms = new MemoryStream(Convert.FromBase64String(f.Substring(f.IndexOf(',') + 1)));
                                using var img = Image.FromStream(ms);
                                int estimated = EstimateTokensForImageDimensions(img.Width, img.Height);
                                _lastImageTokenEstimates[f] = estimated;
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

                if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    // try to estimate tokens for data urls (png/jpg)
                    try
                    {
                        // attempt to get dimensions for png/jpeg by loading
                        int commaIdx = trimmed.IndexOf(',');
                        var meta = trimmed.Substring(5, commaIdx - 5); // e.g. image/png;base64
                        var comma = trimmed.IndexOf(',');
                        byte[] bytes = Convert.FromBase64String(trimmed[(comma + 1)..]);
                        using var ms = new MemoryStream(bytes);
                        using var img = Image.FromStream(ms);
                        int est = EstimateTokensForImageDimensions(img.Width, img.Height);
                        _lastImageTokenEstimates[$"dataurl#{result.Count}"] = est;
                    }
                    catch { }

                    result.Add(trimmed);
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

        /// <summary>
        /// Extracts each page/frame of a multi-page TIFF file and converts to PNG base64 data URLs.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static List<string> ExtractTiffFramesAsBase64(string filePath)
        {
            var frames = new List<string>();
            try
            {
                using var image = Image.FromFile(filePath);
                int frameCount = GetTiffFrameCount(image);

                for (int i = 0; i < frameCount; i++)
                {
                    image.SelectActiveFrame(FrameDimension.Page, i);
                    using var frameBitmap = new Bitmap(image.Width, image.Height);
                    using (var g = Graphics.FromImage(frameBitmap))
                    {
                        g.DrawImage(image, 0, 0, image.Width, image.Height);
                    }

                    var dataUrl = BitmapToDataUrl(frameBitmap);
                    frames.Add(dataUrl);
                    try
                    {
                        int estimated = EstimateTokensForImageDimensions(frameBitmap.Width, frameBitmap.Height);
                        _lastImageTokenEstimates[dataUrl] = estimated;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[LlamaCpp] Failed to extract TIFF frames from '{filePath}': {ex.Message}");
            }

            return frames;
        }

        /// <summary>
        /// Extracts each page/frame of a multi-page TIFF from a data:image/tiff;base64,... URL.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static List<string> ExtractTiffFramesFromDataUrl(string dataUrl)
        {
            var frames = new List<string>();
            try
            {
                int commaIdx = dataUrl.IndexOf(',');
                if (commaIdx < 0)
                {
                    return frames;
                }

                byte[] tiffBytes = Convert.FromBase64String(dataUrl[(commaIdx + 1)..]);
                using var ms = new MemoryStream(tiffBytes);
                using var image = Image.FromStream(ms);
                int frameCount = GetTiffFrameCount(image);

                for (int i = 0; i < frameCount; i++)
                {
                    image.SelectActiveFrame(FrameDimension.Page, i);
                    using var frameBitmap = new Bitmap(image.Width, image.Height);
                    using (var g = Graphics.FromImage(frameBitmap))
                    {
                        g.DrawImage(image, 0, 0, image.Width, image.Height);
                    }

                    frames.Add(BitmapToDataUrl(frameBitmap));
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[LlamaCpp] Failed to extract TIFF frames from data URL: {ex.Message}");
            }

            return frames;
        }

        [SupportedOSPlatform("windows")]
        private static int GetTiffFrameCount(Image image)
        {
            try
            {
                var dimension = new FrameDimension(image.FrameDimensionsList[0]);
                return image.GetFrameCount(dimension);
            }
            catch
            {
                return 1;
            }
        }

        [SupportedOSPlatform("windows")]
        private static string BitmapToDataUrl(Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return $"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}";
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
