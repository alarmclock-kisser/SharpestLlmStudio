using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        private readonly Lock _knowledgeV2Lock = new();
        private readonly List<LlamaKnowledgeChunkV2> _knowledgeChunksV2 = [];

        private static readonly HashSet<string> _knowledgeStopWordsV2 =
        [
            "a", "an", "and", "are", "as", "at", "be", "bei", "but", "by", "das", "dass", "dem", "den", "der", "des",
            "die", "ein", "eine", "einem", "einer", "eines", "er", "es", "for", "from", "hat", "have", "how", "ich",
            "in", "into", "is", "ist", "it", "mit", "not", "oder", "of", "on", "or", "that", "the", "their", "there",
            "these", "this", "to", "und", "von", "was", "we", "wer", "wie", "with", "you", "your"
        ];

        private readonly record struct KnowledgeChunkingPlanV2(int ParentChunkSize, int ChildChunkSize, bool IsAutoSelected);

        public int GetKnowledgeChunkCountV2(string content, int? chunkSize = null)
        {
            KnowledgeChunkingPlanV2 plan = ResolveChunkingPlanV2(content, chunkSize);
            return BuildKnowledgeChunksV2("preview", content, null, plan).Count;
        }

        public async Task<LlamaKnowledgeChunkV2> UpsertKnowledgeV2Async(string key, string content, string? sourcePath = null, CancellationToken cancellationToken = default, int? chunkSize = null, Action<string>? progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Key is required.", nameof(key));
            }

            string baseKey = key.Trim();
            KnowledgeChunkingPlanV2 plan = ResolveChunkingPlanV2(content, chunkSize);
            await StaticLogger.LogAsync($"[LlamaCpp][RAGv2] Chunking for '{baseKey}': mode={(plan.IsAutoSelected ? "auto" : "manual")}, child={plan.ChildChunkSize}, parent={plan.ParentChunkSize}, contentLength={(content?.Length ?? 0)}, source='{sourcePath ?? "(inline)"}'");
            var chunks = BuildKnowledgeChunksV2(baseKey, content ?? string.Empty, sourcePath, plan);
            if (chunks.Count == 0)
            {
                throw new InvalidOperationException("Knowledge source did not produce any chunks.");
            }

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                chunk.Vector = await this.CreateEmbeddingAsync(chunk.Content, cancellationToken);
                progressCallback?.Invoke(chunk.CitationId);
            }

            lock (this._knowledgeV2Lock)
            {
                this._knowledgeChunksV2.RemoveAll(c => string.Equals(c.SourceKey, baseKey, StringComparison.OrdinalIgnoreCase));
                this._knowledgeChunksV2.AddRange(chunks);
            }

            return chunks[0];
        }

        public async Task<IReadOnlyList<LlamaKnowledgeSearchResultV2>> SearchKnowledgeV2Async(string query, int topK = 5, double minScore = 0.08, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return [];
            }

            var queryVector = await this.CreateEmbeddingAsync(query, cancellationToken);
            var queryTokens = ExtractKnowledgeKeywordSetV2(query);

            List<LlamaKnowledgeChunkV2> snapshot;
            lock (this._knowledgeV2Lock)
            {
                snapshot = this._knowledgeChunksV2
                    .Where(k => k.Vector.Length > 0)
                    .Select(CloneKnowledgeChunkV2)
                    .ToList();
            }

            if (snapshot.Count == 0)
            {
                return [];
            }

            var scored = snapshot
                .Select(chunk =>
                {
                    double denseScore = NormalizeCosineScore(CosineSimilarity(queryVector, chunk.Vector));
                    double keywordScore = ComputeKeywordScoreV2(queryTokens, chunk);
                    double rerankScore = ComputeHeuristicRerankScoreV2(query, queryTokens, chunk);
                    double finalScore = (denseScore * 0.52) + (keywordScore * 0.18) + (rerankScore * 0.30);

                    return new LlamaKnowledgeSearchResultV2
                    {
                        Chunk = chunk,
                        DenseScore = denseScore,
                        KeywordScore = keywordScore,
                        RerankScore = rerankScore,
                        FinalScore = finalScore
                    };
                })
                .Where(r => r.FinalScore >= minScore || r.KeywordScore > 0.24)
                .OrderByDescending(r => r.FinalScore)
                .ThenByDescending(r => r.RerankScore)
                .Take(Math.Max(6, topK * 6))
                .ToList();

            var deduped = scored
                .GroupBy(r => r.Chunk.ParentChunkId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r => r.FinalScore).First())
                .OrderByDescending(r => r.FinalScore)
                .ThenByDescending(r => r.DenseScore)
                .Take(Math.Max(1, topK))
                .ToList();

            return deduped;
        }

        public async Task<LlamaKnowledgePromptPackageV2> BuildKnowledgePromptPackageV2Async(string userPrompt, int topK = 4, int contextSize = 0, int maxGenerationTokens = 0, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                return new LlamaKnowledgePromptPackageV2 { UserPrompt = string.Empty };
            }

            int retrievalCount = Math.Max(topK, 4);
            IReadOnlyList<LlamaKnowledgeSearchResultV2> matches;
            if (IsBroadKnowledgeQuery(userPrompt))
            {
                List<LlamaKnowledgeChunkV2> broadSnapshot;
                lock (this._knowledgeV2Lock)
                {
                    broadSnapshot = this._knowledgeChunksV2
                        .OrderBy(c => c.SourceKey, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(c => c.ParentIndex)
                        .ThenBy(c => c.ChunkIndex)
                        .Select(CloneKnowledgeChunkV2)
                        .Take(Math.Max(8, topK * 3))
                        .ToList();
                }

                matches = broadSnapshot
                    .Select((chunk, index) => new LlamaKnowledgeSearchResultV2
                    {
                        Chunk = chunk,
                        DenseScore = 0.5,
                        KeywordScore = 0.5,
                        RerankScore = Math.Max(0.1, 1.0 - (index * 0.03)),
                        FinalScore = Math.Max(0.1, 1.0 - (index * 0.03))
                    })
                    .ToList();
            }
            else
            {
                matches = await this.SearchKnowledgeV2Async(userPrompt, retrievalCount, cancellationToken: cancellationToken);
            }

            if (matches.Count == 0)
            {
                return new LlamaKnowledgePromptPackageV2 { UserPrompt = userPrompt };
            }

            int ctxTokens = contextSize > 0 ? contextSize : (this.CurrentContextSize > 0 ? this.CurrentContextSize : this._settings.DefaultContextSize);
            ctxTokens = Math.Max(1024, ctxTokens);
            int genTokens = maxGenerationTokens > 0 ? maxGenerationTokens : this._settings.DefaultMaxTokens;
            int reservedForGeneration = Math.Clamp(genTokens, 256, Math.Max(256, ctxTokens - 256));
            int availablePromptTokens = Math.Max(384, ctxTokens - reservedForGeneration - 160);
            int availablePromptChars = availablePromptTokens * 3;

            const string questionHeader = "User Question:";
            int promptBaseChars = questionHeader.Length + userPrompt.Length + 96;
            int remainingEvidenceChars = Math.Max(256, availablePromptChars - promptBaseChars);

            var selected = new List<LlamaKnowledgeSearchResultV2>();
            foreach (var match in matches.OrderByDescending(m => m.FinalScore))
            {
                string block = BuildEvidenceBlockV2(match);
                if (selected.Count > 0 && block.Length > remainingEvidenceChars)
                {
                    continue;
                }

                if (block.Length > remainingEvidenceChars)
                {
                    break;
                }

                selected.Add(match);
                remainingEvidenceChars -= block.Length;
                if (remainingEvidenceChars < 160)
                {
                    break;
                }
            }

            if (selected.Count == 0)
            {
                selected.Add(matches[0]);
            }

            var sb = new StringBuilder();
            sb.AppendLine("Evidence Pack (retrieved + reranked):");
            sb.AppendLine();
            foreach (var match in selected)
            {
                sb.Append(BuildEvidenceBlockV2(match));
                sb.AppendLine();
            }

            sb.AppendLine(questionHeader);
            sb.AppendLine(userPrompt.Trim());

            string systemPrompt =
                "You are operating in grounded-answer mode. "
                + "Use the evidence pack as the primary source of truth. "
                + "Cite evidence ids inline in square brackets such as [kb:source:1.2]. "
                + "If the evidence is insufficient or conflicting, explicitly say so. "
                + "Do not invent unsupported facts and prefer concise, source-backed answers.";

            return new LlamaKnowledgePromptPackageV2
            {
                UserPrompt = sb.ToString().Trim(),
                SystemPromptInstructions = systemPrompt,
                Results = selected
            };
        }

        public async Task<string> SaveKnowledgeStoreV2Async(string? fileName = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(this.EmbeddingStoreDirectory);

            string name = string.IsNullOrWhiteSpace(fileName)
                ? $"knowledge_v2_{DateTime.Now:yyyyMMdd_HHmmss}.json"
                : fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? fileName : $"{fileName}.json";

            string path = Path.Combine(this.EmbeddingStoreDirectory, name);

            List<LlamaKnowledgeChunkV2> snapshot;
            lock (this._knowledgeV2Lock)
            {
                snapshot = this._knowledgeChunksV2.Select(CloneKnowledgeChunkV2).ToList();
            }

            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, cancellationToken);
            return path;
        }

        public async Task<int> LoadKnowledgeStoreV2Async(string path, bool clearExisting = false, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            string json = await File.ReadAllTextAsync(path, cancellationToken);
            var entries = JsonSerializer.Deserialize<List<LlamaKnowledgeChunkV2>>(json) ?? [];

            lock (this._knowledgeV2Lock)
            {
                if (clearExisting)
                {
                    this._knowledgeChunksV2.Clear();
                }

                foreach (var entry in entries)
                {
                    this._knowledgeChunksV2.RemoveAll(k => string.Equals(k.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                    this._knowledgeChunksV2.Add(entry);
                }
            }

            return entries.Count;
        }

        public void ClearKnowledgeStoreV2()
        {
            lock (this._knowledgeV2Lock)
            {
                this._knowledgeChunksV2.Clear();
            }
        }

        public void DeleteKnowledgeBySourceKeyV2(string sourceKey)
        {
            if (string.IsNullOrWhiteSpace(sourceKey))
            {
                return;
            }

            lock (this._knowledgeV2Lock)
            {
                this._knowledgeChunksV2.RemoveAll(c => string.Equals(c.SourceKey, sourceKey.Trim(), StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyList<LlamaKnowledgeEntry> GetKnowledgeEntriesV2Snapshot()
        {
            lock (this._knowledgeV2Lock)
            {
                return this._knowledgeChunksV2
                    .OrderBy(c => c.SourceKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.ParentIndex)
                    .ThenBy(c => c.ChunkIndex)
                    .Select(c => new LlamaKnowledgeEntry
                    {
                        Id = c.Id,
                        Key = $"{c.SourceKey} [chunk {c.ParentIndex + 1}.{c.ChunkIndex + 1}]",
                        Content = c.Content,
                        SourcePath = c.SourcePath,
                        Vector = c.Vector,
                        CreatedAtUtc = c.CreatedAtUtc
                    })
                    .ToList();
            }
        }

        private static LlamaKnowledgeChunkV2 CloneKnowledgeChunkV2(LlamaKnowledgeChunkV2 chunk)
        {
            return new LlamaKnowledgeChunkV2
            {
                Id = chunk.Id,
                DocumentId = chunk.DocumentId,
                SourceKey = chunk.SourceKey,
                SourcePath = chunk.SourcePath,
                ParentChunkId = chunk.ParentChunkId,
                ParentIndex = chunk.ParentIndex,
                ChunkIndex = chunk.ChunkIndex,
                CitationId = chunk.CitationId,
                Content = chunk.Content,
                Preview = chunk.Preview,
                Keywords = chunk.Keywords.ToArray(),
                Vector = chunk.Vector.ToArray(),
                CreatedAtUtc = chunk.CreatedAtUtc
            };
        }

        private static List<LlamaKnowledgeChunkV2> BuildKnowledgeChunksV2(string key, string content, string? sourcePath, KnowledgeChunkingPlanV2 plan)
        {
            string trimmed = content?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return [];
            }

            string documentId = Guid.NewGuid().ToString("N");
            string normalizedKey = key.Trim();
            string keySlug = SanitizeKeyForCitationV2(normalizedKey);
            var parentChunks = SplitKnowledgeContent(trimmed, plan.ParentChunkSize);
            var results = new List<LlamaKnowledgeChunkV2>();

            for (int parentIndex = 0; parentIndex < parentChunks.Count; parentIndex++)
            {
                string parent = parentChunks[parentIndex];
                var children = SplitKnowledgeContent(parent, plan.ChildChunkSize);
                string parentChunkId = $"{documentId}-p{parentIndex + 1}";

                for (int chunkIndex = 0; chunkIndex < children.Count; chunkIndex++)
                {
                    string child = children[chunkIndex].Trim();
                    if (string.IsNullOrWhiteSpace(child))
                    {
                        continue;
                    }

                    results.Add(new LlamaKnowledgeChunkV2
                    {
                        Id = $"{parentChunkId}-c{chunkIndex + 1}",
                        DocumentId = documentId,
                        SourceKey = normalizedKey,
                        SourcePath = sourcePath,
                        ParentChunkId = parentChunkId,
                        ParentIndex = parentIndex,
                        ChunkIndex = chunkIndex,
                        CitationId = $"kb:{keySlug}:{parentIndex + 1}.{chunkIndex + 1}",
                        Content = child,
                        Preview = BuildPreviewV2(child),
                        Keywords = ExtractKnowledgeKeywordsV2(child),
                        CreatedAtUtc = DateTime.UtcNow
                    });
                }
            }

            return results;
        }

        private KnowledgeChunkingPlanV2 ResolveChunkingPlanV2(string? content, int? requestedChunkSize)
        {
            int contentLength = content?.Length ?? 0;
            int effectiveBatchSize = Math.Max(32, this.CurrentBatchSize > 0 ? this.CurrentBatchSize : this._settings.DefaultBatchSize);
            if (requestedChunkSize.HasValue)
            {
                int child = Math.Clamp(requestedChunkSize.Value, 256, 4096);
                int parent = Math.Clamp((int)Math.Round(child * 2.4), child + 256, 8192);
                return new KnowledgeChunkingPlanV2(parent, child, false);
            }

            int autoChild = contentLength switch
            {
                <= 12_000 => 896,
                <= 60_000 => 1152,
                <= 250_000 => 1408,
                <= 800_000 => 1664,
                _ => 1920
            };
            autoChild = Math.Min(autoChild, effectiveBatchSize);

            int autoParent = contentLength switch
            {
                <= 12_000 => 2048,
                <= 60_000 => 2688,
                <= 250_000 => 3328,
                <= 800_000 => 4096,
                _ => 4864
            };

            return new KnowledgeChunkingPlanV2(autoParent, autoChild, true);
        }

        private static string BuildEvidenceBlockV2(LlamaKnowledgeSearchResultV2 match)
        {
            return $"[{match.Chunk.CitationId}]\n"
                + $"Source: {match.Chunk.SourceKey}\n"
                + $"Scores: final={match.FinalScore:F3}; dense={match.DenseScore:F3}; keyword={match.KeywordScore:F3}; rerank={match.RerankScore:F3}\n"
                + $"Content: {match.Chunk.Content.Trim()}\n";
        }

        private static double NormalizeCosineScore(double score)
        {
            return Math.Clamp((score + 1.0) / 2.0, 0.0, 1.0);
        }

        private static double ComputeKeywordScoreV2(HashSet<string> queryTokens, LlamaKnowledgeChunkV2 chunk)
        {
            if (queryTokens.Count == 0 || chunk.Keywords.Length == 0)
            {
                return 0.0;
            }

            int overlap = chunk.Keywords.Count(queryTokens.Contains);
            if (overlap <= 0)
            {
                return 0.0;
            }

            double coverage = overlap / (double)queryTokens.Count;
            double density = overlap / (double)Math.Max(1, chunk.Keywords.Length);
            double sourceBoost = queryTokens.Any(t => chunk.SourceKey.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 0.12 : 0.0;
            return Math.Clamp((coverage * 0.75) + (density * 0.25) + sourceBoost, 0.0, 1.0);
        }

        private static double ComputeHeuristicRerankScoreV2(string query, HashSet<string> queryTokens, LlamaKnowledgeChunkV2 chunk)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return 0.0;
            }

            string q = NormalizeTextForMatchV2(query);
            string content = NormalizeTextForMatchV2(chunk.Content);
            string source = NormalizeTextForMatchV2(chunk.SourceKey);

            double exactPhrase = content.Contains(q, StringComparison.Ordinal) ? 1.0 : 0.0;
            int coveredTerms = queryTokens.Count(t => content.Contains(t, StringComparison.Ordinal) || source.Contains(t, StringComparison.Ordinal));
            double termCoverage = queryTokens.Count == 0 ? 0.0 : coveredTerms / (double)queryTokens.Count;
            double sourceMatch = queryTokens.Any(t => source.Contains(t, StringComparison.Ordinal)) ? 0.35 : 0.0;
            double previewBoost = !string.IsNullOrWhiteSpace(chunk.Preview) && q.Length > 6 && NormalizeTextForMatchV2(chunk.Preview).Contains(q[..Math.Min(q.Length, 48)], StringComparison.Ordinal)
                ? 0.2
                : 0.0;

            return Math.Clamp((exactPhrase * 0.45) + (termCoverage * 0.4) + sourceMatch + previewBoost, 0.0, 1.0);
        }

        private static string[] ExtractKnowledgeKeywordsV2(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            return Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{Nd}_-]{3,}")
                .Select(m => m.Value)
                .Where(token => !_knowledgeStopWordsV2.Contains(token))
                .Distinct(StringComparer.Ordinal)
                .Take(64)
                .ToArray();
        }

        private static HashSet<string> ExtractKnowledgeKeywordSetV2(string text)
        {
            return ExtractKnowledgeKeywordsV2(text).ToHashSet(StringComparer.Ordinal);
        }

        private static string BuildPreviewV2(string content)
        {
            string normalized = Regex.Replace(content, @"\s+", " ").Trim();
            return normalized.Length <= 180 ? normalized : normalized[..180] + "…";
        }

        private static string SanitizeKeyForCitationV2(string key)
        {
            string normalized = Regex.Replace(key.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(normalized) ? "source" : normalized;
        }

        private static string NormalizeTextForMatchV2(string text)
        {
            return Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();
        }
    }
}
