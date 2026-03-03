using System.Collections.Generic;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
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
            var payload = this.BuildChatCompletionPayload(request, normalizedImages);
            string assistantText = string.Empty;

            lock (this._generationStatsLock)
            {
                this.LastGenerationStats = new GenerationStats
                {
                    GenerationStarted = DateTime.UtcNow,
                    GenerationFinished = null,
                    TotalTokensGenerated = 0,
                    TotalContextTokens = request.MaxTokens
                };
            }

            if (request.Stream)
            {
                await foreach (var chunk in this.StreamChatCompletionChunksAsync(payload, cancellationToken))
                {
                    assistantText += chunk;

                    lock (this._generationStatsLock)
                    {
                        this.LastGenerationStats.TotalTokensGenerated = CountRoughTokens(assistantText);
                        this.LastGenerationStats.GenerationFinished = null;
                    }

                    yield return chunk;
                }
            }
            else
            {
                assistantText = await this.GenerateSingleChatCompletionAsync(payload, cancellationToken);

                lock (this._generationStatsLock)
                {
                    this.LastGenerationStats.TotalTokensGenerated = CountRoughTokens(assistantText);
                }

                if (!string.IsNullOrEmpty(assistantText))
                {
                    yield return assistantText;
                }
            }

            lock (this._generationStatsLock)
            {
                this.LastGenerationStats.GenerationFinished = DateTime.UtcNow;
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
                    // Rough estimate: ~4 chars per token, reserve 80% of context for history
                    int maxHistoryChars = (int)(request.MaxTokens * 4 * 0.8);
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
            response.EnsureSuccessStatusCode();

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
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
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
                    byte[] bytes = await File.ReadAllBytesAsync(trimmed, cancellationToken);
                    string mime = GetMimeTypeByFileExtension(trimmed);
                    result.Add($"data:{mime};base64,{Convert.ToBase64String(bytes)}");
                    continue;
                }

                if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
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
                _ => "image/jpeg"
            };
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
