using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        public async Task<float[]> CreateEmbeddingAsync(string content, CancellationToken cancellationToken = default)
        {
            if (!this.IsServerRunning || string.IsNullOrWhiteSpace(this.CurrentBaseUrl))
            {
                throw new InvalidOperationException("llama.cpp server is not running. Load a model first.");
            }

            using var activityScope = this.BeginServerActivityScope();

            // Chunk large content to avoid exceeding the model's context window
            const int MaxChunkChars = 2000;
            if (content.Length <= MaxChunkChars)
            {
                return await this.CreateSingleEmbeddingAsync(content, cancellationToken, suppressLogging: false);
            }

            var chunks = ChunkText(content, MaxChunkChars);
            float[]? accumulated = null;
            int chunkCount = 0;

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vec = await this.CreateSingleEmbeddingAsync(chunk, cancellationToken, suppressLogging: true);
                if (vec.Length == 0)
                {
                    continue;
                }

                if (accumulated == null)
                {
                    accumulated = new float[vec.Length];
                }

                for (int i = 0; i < Math.Min(accumulated.Length, vec.Length); i++)
                {
                    accumulated[i] += vec[i];
                }

                chunkCount++;
            }

            if (accumulated == null || chunkCount == 0)
            {
                return [];
            }

            // Average the vectors
            for (int i = 0; i < accumulated.Length; i++)
            {
                accumulated[i] /= chunkCount;
            }

            return accumulated;
        }

        private async Task<float[]> CreateSingleEmbeddingAsync(string content, CancellationToken cancellationToken, bool suppressLogging = false, int retrySplitLevel = 0)
        {
            // Try server-side embedding first (requires --embedding flag)
            try
            {
                var payload = new JsonObject
                {
                    ["input"] = content,
                    // ask server to use a compatible pooling for OAI-style embeddings
                    ["pooling"] = "mean"
                };

                using var response = await this._httpClient.PostAsJsonAsync($"{this.CurrentBaseUrl}/v1/embeddings", payload, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
                    var embeddingNode = json?["data"]?[0]?["embedding"]?.AsArray();
                    if (embeddingNode != null && embeddingNode.Count > 0)
                    {
                        var vector = new float[embeddingNode.Count];
                        for (int i = 0; i < embeddingNode.Count; i++)
                        {
                            vector[i] = embeddingNode[i]?.GetValue<float>() ?? 0f;
                        }

                        this.TouchServerActivity();
                        return vector;
                    }
                }

                if (!suppressLogging)
                {
                    string errorBody = string.Empty;
                    try
                    {
                        errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    }
                    catch
                    {
                    }

                    // If the server complains about processing size (batch size), try client-side splitting and retrying
                    if (!string.IsNullOrWhiteSpace(errorBody) && retrySplitLevel < 4 && (errorBody.Contains("too large to process", StringComparison.OrdinalIgnoreCase) || errorBody.Contains("increase the physical batch size", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            // Try to parse the reported current batch size and derive a safe target
                            int reportedBatch = 0;
                            var m = Regex.Match(errorBody, "current batch size:\\s*(\\d+)", RegexOptions.IgnoreCase);
                            if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed))
                            {
                                reportedBatch = parsed;
                            }

                            int targetTokens = reportedBatch > 0 ? Math.Max(64, reportedBatch - 16) : 256;
                            const int charsPerToken = 3;
                            int chunkChars = Math.Max(256, targetTokens * charsPerToken);

                            var parts = ChunkText(content, chunkChars);
                            float[]? accumulated = null;
                            int partCount = 0;
                            foreach (var part in parts)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var vec = await this.CreateSingleEmbeddingAsync(part, cancellationToken, suppressLogging: true, retrySplitLevel: retrySplitLevel + 1);
                                if (vec.Length == 0)
                                {
                                    continue;
                                }

                                if (accumulated == null)
                                {
                                    accumulated = new float[vec.Length];
                                }

                                for (int i = 0; i < Math.Min(accumulated.Length, vec.Length); i++)
                                {
                                    accumulated[i] += vec[i];
                                }

                                partCount++;
                            }

                            if (accumulated != null && partCount > 0)
                            {
                                for (int i = 0; i < accumulated.Length; i++)
                                {
                                    accumulated[i] /= partCount;
                                }

                                await StaticLogger.LogAsync($"[LlamaCpp] Embedding retry: split into {partCount} parts due to server batch size limit. (retryLevel={retrySplitLevel})");
                                this.TouchServerActivity();
                                return accumulated;
                            }
                        }
                        catch { }
                    }

                    // If server complains about pooling 'none', retry with alternative pooling key names
                    if (errorBody != null && errorBody.Contains("Pooling type 'none'", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var altPayload = new JsonObject
                            {
                                ["input"] = content,
                                ["pooling"] = "mean",
                                ["pooling_type"] = "mean",
                                ["pooling_mode"] = "mean"
                            };

                            using var altResp = await this._httpClient.PostAsJsonAsync($"{this.CurrentBaseUrl}/v1/embeddings", altPayload, cancellationToken);
                            if (altResp.IsSuccessStatusCode)
                            {
                                var altJson = await altResp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
                                var altEmbeddingNode = altJson?["data"]?[0]?["embedding"]?.AsArray();
                                if (altEmbeddingNode != null && altEmbeddingNode.Count > 0)
                                {
                                    var vector = new float[altEmbeddingNode.Count];
                                    for (int i = 0; i < altEmbeddingNode.Count; i++)
                                    {
                                        vector[i] = altEmbeddingNode[i]?.GetValue<float>() ?? 0f;
                                    }

                                    this.TouchServerActivity();
                                    return vector;
                                }
                            }
                        }
                        catch { }
                    }

                    // Any non-success from embedding endpoint falls back to local embedding.
                    // This covers 501 (not enabled), 400 (model/server variant specific request mismatch), etc.
                    await StaticLogger.LogAsync($"[LlamaCpp] /v1/embeddings returned {(int)response.StatusCode} ({response.ReasonPhrase}). Falling back to local embedding. Body: {errorBody}");
                }
            }
            catch (HttpRequestException ex)
            {
                if (!suppressLogging)
                {
                    await StaticLogger.LogAsync($"[LlamaCpp] Embedding request failed ({ex.Message}). Falling back to local embedding.");
                }
            }

            // Local fallback: hash-based bag-of-words embedding
            // Works without server support, sufficient for keyword-based similarity
            return CreateLocalEmbedding(content);
        }

        /// <summary>
        /// Creates a simple local embedding vector using character n-gram hashing.
        /// This is a fallback for when the server doesn't support /v1/embeddings
        /// (e.g. multimodal or Omni models loaded without --embedding).
        /// </summary>
        private static float[] CreateLocalEmbedding(string content, int dimensions = 384)
        {
            var vector = new float[dimensions];
            if (string.IsNullOrWhiteSpace(content))
            {
                return vector;
            }

            var normalized = content.ToLowerInvariant();

            // Token-level hashing (words)
            var tokens = normalized.Split([' ', '\n', '\r', '\t', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                int hash = token.GetHashCode(StringComparison.Ordinal);
                int idx = ((hash % dimensions) + dimensions) % dimensions;
                vector[idx] += 1.0f;

                // Also add bigram hashes for better discrimination
                for (int i = 0; i < token.Length - 1; i++)
                {
                    int bigramHash = HashCode.Combine(token[i], token[i + 1]);
                    int bigramIdx = ((bigramHash % dimensions) + dimensions) % dimensions;
                    vector[bigramIdx] += 0.5f;
                }
            }

            // L2-normalize the vector
            double norm = 0;
            for (int i = 0; i < vector.Length; i++)
            {
                norm += vector[i] * vector[i];
            }

            if (norm > 0)
            {
                float invNorm = (float)(1.0 / Math.Sqrt(norm));
                for (int i = 0; i < vector.Length; i++)
                {
                    vector[i] *= invNorm;
                }
            }

            return vector;
        }

        private static List<string> ChunkText(string text, int maxChars)
        {
            var chunks = new List<string>();
            int start = 0;

            while (start < text.Length)
            {
                int end = Math.Min(start + maxChars, text.Length);

                // Try to break at a sentence/paragraph boundary
                if (end < text.Length)
                {
                    int breakPoint = text.LastIndexOfAny(['\n', '.', '!', '?'], end - 1, Math.Min(end - start, 200));
                    if (breakPoint > start)
                    {
                        end = breakPoint + 1;
                    }
                }

                chunks.Add(text[start..end]);
                start = end;
            }

            return chunks;
        }

        public Task<float[]> CreateEmbeddingAsync(JsonNode json, CancellationToken cancellationToken = default)
        {
            return this.CreateEmbeddingAsync(json.ToJsonString(), cancellationToken);
        }

        public Task<float[]> CreateEmbeddingAsync(XDocument xml, CancellationToken cancellationToken = default)
        {
            return this.CreateEmbeddingAsync(xml.ToString(SaveOptions.DisableFormatting), cancellationToken);
        }

        public async Task<LlamaKnowledgeEntry> UpsertKnowledgeAsync(string key, string content, string? sourcePath = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Key is required.", nameof(key));
            }

            string baseKey = key.Trim();
            var chunks = SplitKnowledgeContent(content, 1400);
            var now = DateTime.UtcNow;
            var createdEntries = new List<LlamaKnowledgeEntry>(chunks.Count);

            for (int i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string chunkContent = chunks[i];
                var embedding = await this.CreateEmbeddingAsync(chunkContent, cancellationToken);
                string chunkKey = chunks.Count == 1
                    ? baseKey
                    : $"{baseKey} [chunk {i + 1}/{chunks.Count}]";

                createdEntries.Add(new LlamaKnowledgeEntry
                {
                    Key = chunkKey,
                    Content = chunkContent,
                    Vector = embedding,
                    SourcePath = sourcePath,
                    CreatedAtUtc = now
                });
            }

            lock (this._knowledgeLock)
            {
                this.KnowledgeEntries.RemoveAll(k => IsSameKnowledgeBaseKey(k.Key, baseKey));
                this.KnowledgeEntries.AddRange(createdEntries);
            }

            return createdEntries[0];
        }

        public async Task<LlamaKnowledgeEntry> UpsertKnowledgeFromFileAsync(string filePath, string? key = null, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Knowledge source file not found.", filePath);
            }

            string content = await LoadKnowledgeContentFromFileAsync(filePath, cancellationToken);
            string resolvedKey = string.IsNullOrWhiteSpace(key) ? Path.GetFileNameWithoutExtension(filePath) : key;
            return await this.UpsertKnowledgeAsync(resolvedKey, content, filePath, cancellationToken);
        }

        public async Task<IReadOnlyList<LlamaKnowledgeSearchResult>> SearchKnowledgeAsync(string query, int topK = 5, double minSimilarity = 0.1, CancellationToken cancellationToken = default)
        {
            var queryVector = await this.CreateEmbeddingAsync(query, cancellationToken);

            List<LlamaKnowledgeEntry> snapshot;
            lock (this._knowledgeLock)
            {
                snapshot = this.KnowledgeEntries
                    .Where(k => k.Vector.Length > 0)
                    .Select(k => new LlamaKnowledgeEntry
                    {
                        Id = k.Id,
                        Key = k.Key,
                        Content = k.Content,
                        SourcePath = k.SourcePath,
                        Vector = k.Vector,
                        CreatedAtUtc = k.CreatedAtUtc
                    })
                    .ToList();
            }

            var results = snapshot
                .Select(entry => new LlamaKnowledgeSearchResult
                {
                    Entry = entry,
                    Similarity = CosineSimilarity(queryVector, entry.Vector)
                })
                .Where(r => r.Similarity >= minSimilarity)
                .OrderByDescending(r => r.Similarity)
                .Take(Math.Max(1, topK))
                .ToList();

            return results;
        }

        [SupportedOSPlatform("windows")]
        public async Task<string> BuildKnowledgeAugmentedPromptAsync(string userPrompt, int topK = 3, int contextSize = 0, int maxGenerationTokens = 0, CancellationToken cancellationToken = default)
        {
            bool isBroadQuery = IsBroadKnowledgeQuery(userPrompt);

            List<LlamaKnowledgeEntry> knowledgeToInclude;
            if (isBroadQuery)
            {
                lock (this._knowledgeLock)
                {
                    knowledgeToInclude = this.KnowledgeEntries
                        .Where(k => k.Vector.Length > 0)
                        .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(k => new LlamaKnowledgeEntry
                        {
                            Id = k.Id,
                            Key = k.Key,
                            Content = k.Content,
                            SourcePath = k.SourcePath,
                            Vector = k.Vector,
                            CreatedAtUtc = k.CreatedAtUtc
                        })
                        .ToList();
                }
            }
            else
            {
                var matches = await this.SearchKnowledgeAsync(userPrompt, topK, cancellationToken: cancellationToken);
                knowledgeToInclude = matches.Select(m => m.Entry).ToList();
            }

            if (knowledgeToInclude.Count == 0)
            {
                return userPrompt;
            }

            int ctxTokens = contextSize > 0 ? contextSize : (this.CurrentContextSize > 0 ? this.CurrentContextSize : this._settings.DefaultContextSize);
            ctxTokens = Math.Max(512, ctxTokens);
            int genTokens = maxGenerationTokens > 0 ? maxGenerationTokens : this._settings.DefaultMaxTokens;
            int reservedForGeneration = Math.Clamp(genTokens, 256, Math.Max(256, ctxTokens - 256));
            int availablePromptTokens = Math.Max(256, ctxTokens - reservedForGeneration - 96);
            int availablePromptChars = availablePromptTokens * 3;

            const string introLine = "Nutze die folgenden Wissenskontexte für die Antwort, falls relevant:";
            const string userPromptHeader = "User Prompt:";

            int baseCharsNeeded = introLine.Length + userPromptHeader.Length + userPrompt.Length + 16;

            int totalKnowledgeChars = 0;
            var knowledgeBlocks = new List<(string Header, string Content)>();
            string? lastBaseKey = null;
            foreach (var entry in knowledgeToInclude)
            {
                string baseKey = StaticLogics.GetBaseKnowledgeKey(entry.Key);
                string header = baseKey != lastBaseKey ? $"--- {baseKey} ---" : string.Empty;
                lastBaseKey = baseKey;
                string content = entry.Content ?? string.Empty;
                int blockLen = header.Length + content.Length + 4;
                totalKnowledgeChars += blockLen;
                knowledgeBlocks.Add((header, content));
            }

            int requiredChars = baseCharsNeeded + totalKnowledgeChars;
            if (isBroadQuery && requiredChars > availablePromptChars)
            {
                int neededTokens = (requiredChars / 3) + reservedForGeneration + 128;
                int maxAllowed = ctxTokens - 128;
                availablePromptTokens = Math.Max(availablePromptTokens, Math.Min(neededTokens - reservedForGeneration, maxAllowed - reservedForGeneration));
                availablePromptChars = availablePromptTokens * 3;
            }

            int remainingKnowledgeChars = availablePromptChars - baseCharsNeeded;
            if (remainingKnowledgeChars <= 0)
            {
                return userPrompt;
            }

            var sb = new StringBuilder();
            sb.AppendLine(introLine);
            sb.AppendLine();

            bool addedAnyContext = false;
            foreach (var (header, content) in knowledgeBlocks)
            {
                int blockChars = header.Length + content.Length + 4;
                if (blockChars > remainingKnowledgeChars && remainingKnowledgeChars < 64)
                {
                    break;
                }

                if (blockChars > remainingKnowledgeChars)
                {
                    if (isBroadQuery)
                    {
                        int canFit = remainingKnowledgeChars - header.Length - 4;
                        if (canFit > 100)
                        {
                            if (!string.IsNullOrEmpty(header))
                            {
                                sb.AppendLine(header);
                            }

                            sb.AppendLine(content.Substring(0, canFit));
                            sb.AppendLine();
                            addedAnyContext = true;
                        }
                        break;
                    }
                    continue;
                }

                if (!string.IsNullOrEmpty(header))
                {
                    sb.AppendLine(header);
                }

                sb.AppendLine(content);
                sb.AppendLine();

                remainingKnowledgeChars -= blockChars;
                addedAnyContext = true;
            }

            if (!addedAnyContext)
            {
                return userPrompt;
            }

            sb.AppendLine(userPromptHeader);
            sb.AppendLine(userPrompt);
            return sb.ToString();
        }

        private static bool IsBroadKnowledgeQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            string[] broadPatterns =
            [
                "alle ", "alles ", "jede ", "jedes ", "jeden ", "jeder ",
                "every ", "each ", "all ",
                "list all", "list every", "document all", "document every", "describe all", "describe every",
                "nenne alle", "nenne jede", "beschreibe alle", "beschreibe jede",
                "dokumentiere alle", "dokumentiere jede", "erkläre alle", "erkläre jede",
                "zeige alle", "zeige jede", "zusammenfassung", "overview", "summarize all",
                "gesamte ", "komplett ", "vollständig", "sämtliche "
            ];

            string lower = query.ToLowerInvariant();
            return broadPatterns.Any(p => lower.Contains(p));
        }

        private static bool IsSameKnowledgeBaseKey(string existingKey, string baseKey)
        {
            if (string.Equals(existingKey, baseKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return existingKey.StartsWith(baseKey + " [chunk ", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> SplitKnowledgeContent(string content, int maxChunkChars)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return [string.Empty];
            }

            var chunks = new List<string>();
            int start = 0;
            string text = content.Trim();

            while (start < text.Length)
            {
                int end = Math.Min(start + Math.Max(200, maxChunkChars), text.Length);

                if (end < text.Length)
                {
                    int paragraph = text.LastIndexOf("\n\n", end, Math.Max(0, end - start));
                    if (paragraph > start + 200)
                    {
                        end = paragraph;
                    }
                    else
                    {
                        int sentence = text.LastIndexOfAny(['\n', '.', ';', ':', '!', '?'], end - 1, Math.Max(1, end - start));
                        if (sentence > start + 120)
                        {
                            end = sentence + 1;
                        }
                    }
                }

                string chunk = text[start..end].Trim();
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    chunks.Add(chunk);
                }

                start = end;
                while (start < text.Length && char.IsWhiteSpace(text[start]))
                {
                    start++;
                }
            }

            return chunks.Count == 0 ? [text] : chunks;
        }

        [SupportedOSPlatform("windows")]
        public async IAsyncEnumerable<string> GenerateWithKnowledgeAsync(
            string prompt,
            int topK = 3,
            bool isolated = false,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string augmentedPrompt = await this.BuildKnowledgeAugmentedPromptAsync(prompt, topK, cancellationToken: cancellationToken);
            await foreach (var chunk in this.GenerateAsync(augmentedPrompt, isolated, cancellationToken))
            {
                yield return chunk;
            }
        }

        public async Task<string> SaveKnowledgeStoreAsync(string? fileName = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(this.EmbeddingStoreDirectory);

            string name = string.IsNullOrWhiteSpace(fileName)
                ? $"knowledge_{DateTime.Now:yyyyMMdd_HHmmss}.json"
                : fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? fileName : $"{fileName}.json";

            string path = Path.Combine(this.EmbeddingStoreDirectory, name);

            List<LlamaKnowledgeEntry> snapshot;
            lock (this._knowledgeLock)
            {
                snapshot = this.KnowledgeEntries.ToList();
            }

            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, cancellationToken);
            return path;
        }

        public async Task<int> LoadKnowledgeStoreAsync(string path, bool clearExisting = false, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var entries = JsonSerializer.Deserialize<List<LlamaKnowledgeEntry>>(json) ?? [];

            lock (this._knowledgeLock)
            {
                if (clearExisting)
                {
                    this.KnowledgeEntries.Clear();
                }

                foreach (var entry in entries)
                {
                    this.KnowledgeEntries.RemoveAll(k => string.Equals(k.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
                    this.KnowledgeEntries.Add(entry);
                }
            }

            return entries.Count;
        }

        public void ClearKnowledgeStore()
        {
            lock (this._knowledgeLock)
            {
                this.KnowledgeEntries.Clear();
            }
        }

        // Returns a snapshot of current knowledge entries for UI consumption
        public IReadOnlyList<LlamaKnowledgeEntry> GetKnowledgeEntriesSnapshot()
        {
            lock (this._knowledgeLock)
            {
                return this.KnowledgeEntries.Select(k => new LlamaKnowledgeEntry
                {
                    Id = k.Id,
                    Key = k.Key,
                    Content = k.Content,
                    SourcePath = k.SourcePath,
                    Vector = k.Vector,
                    CreatedAtUtc = k.CreatedAtUtc
                }).ToList();
            }
        }

        private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            {
                return 0;
            }

            double dot = 0;
            double normA = 0;
            double normB = 0;

            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            if (normA <= 0 || normB <= 0)
            {
                return 0;
            }

            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }

        private static async Task<string> LoadKnowledgeContentFromFileAsync(string filePath, CancellationToken cancellationToken)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            string text = await File.ReadAllTextAsync(filePath, cancellationToken);

            return extension switch
            {
                ".json" => JsonNode.Parse(text)?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? text,
                ".xml" => XDocument.Parse(text).ToString(SaveOptions.DisableFormatting),
                _ => text
            };
        }
    }
}
