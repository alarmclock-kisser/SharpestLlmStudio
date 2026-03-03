using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

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

            // Chunk large content to avoid exceeding the model's context window
            const int MaxChunkChars = 2000;
            if (content.Length <= MaxChunkChars)
            {
                return await this.CreateSingleEmbeddingAsync(content, cancellationToken);
            }

            var chunks = ChunkText(content, MaxChunkChars);
            float[]? accumulated = null;
            int chunkCount = 0;

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vec = await this.CreateSingleEmbeddingAsync(chunk, cancellationToken);
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

        private async Task<float[]> CreateSingleEmbeddingAsync(string content, CancellationToken cancellationToken)
        {
            var payload = new JsonObject
            {
                ["input"] = content
            };

            using var response = await this._httpClient.PostAsJsonAsync($"{this.CurrentBaseUrl}/v1/embeddings", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            var embeddingNode = json?["data"]?[0]?["embedding"]?.AsArray();
            if (embeddingNode == null || embeddingNode.Count == 0)
            {
                return [];
            }

            var vector = new float[embeddingNode.Count];
            for (int i = 0; i < embeddingNode.Count; i++)
            {
                vector[i] = embeddingNode[i]?.GetValue<float>() ?? 0f;
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

            var embedding = await this.CreateEmbeddingAsync(content, cancellationToken);
            var now = DateTime.UtcNow;

            lock (this._knowledgeLock)
            {
                var existing = this.KnowledgeEntries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Content = content;
                    existing.Vector = embedding;
                    existing.SourcePath = sourcePath;
                    existing.CreatedAtUtc = now;
                    return existing;
                }

                var created = new LlamaKnowledgeEntry
                {
                    Key = key,
                    Content = content,
                    Vector = embedding,
                    SourcePath = sourcePath,
                    CreatedAtUtc = now
                };

                this.KnowledgeEntries.Add(created);
                return created;
            }
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

        public async Task<string> BuildKnowledgeAugmentedPromptAsync(string userPrompt, int topK = 3, CancellationToken cancellationToken = default)
        {
            var matches = await this.SearchKnowledgeAsync(userPrompt, topK, cancellationToken: cancellationToken);
            if (matches.Count == 0)
            {
                return userPrompt;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Nutze die folgenden Wissenskontexte für die Antwort, falls relevant:");
            sb.AppendLine();

            int i = 1;
            foreach (var match in matches)
            {
                sb.AppendLine($"[{i}] {match.Entry.Key} (score: {match.Similarity:0.000})");
                sb.AppendLine(match.Entry.Content);
                sb.AppendLine();
                i++;
            }

            sb.AppendLine("User Prompt:");
            sb.AppendLine(userPrompt);
            return sb.ToString();
        }

        public async IAsyncEnumerable<string> GenerateWithKnowledgeAsync(
            string prompt,
            int topK = 3,
            bool isolated = false,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string augmentedPrompt = await this.BuildKnowledgeAugmentedPromptAsync(prompt, topK, cancellationToken);
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
