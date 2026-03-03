using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        public IReadOnlyList<LlamaChatMessage> GetConversationSnapshot()
        {
            lock (this._conversationLock)
            {
                return this.ConversationMessages
                    .Select(m => new LlamaChatMessage { Role = m.Role, Content = m.Content, CreatedAtUtc = m.CreatedAtUtc })
                    .ToList();
            }
        }

        public void ResetConversation()
        {
            lock (this._conversationLock)
            {
                this.ConversationMessages.Clear();
            }
        }

        public void AddSystemMessage(string content) => this.AddConversationMessage("system", content);
        public void AddUserMessage(string content) => this.AddConversationMessage("user", content);
        public void AddAssistantMessage(string content) => this.AddConversationMessage("assistant", content);

        public void AddConversationMessage(string role, string content)
        {
            if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            lock (this._conversationLock)
            {
                this.ConversationMessages.Add(new LlamaChatMessage
                {
                    Role = role.Trim().ToLowerInvariant(),
                    Content = content,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
        }

        public Task<IReadOnlyList<string>> GetSavedContextFilesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(this.ContextDirectory);

            IReadOnlyList<string> files = Directory
                .GetFiles(this.ContextDirectory, "*.chat.json", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(this.ContextDirectory, "*.bin", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            return Task.FromResult(files);
        }

        public async Task<LlamaContextSaveResult> SaveContextAsync(string? contextName = null, int slotId = 0, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(this.ContextDirectory);

            string safeName = string.IsNullOrWhiteSpace(contextName) ? $"context_{DateTime.Now:yyyyMMdd_HHmmss}" : SanitizeFileName(contextName);

            // Save conversation as JSON (reliable, portable)
            string jsonPath = Path.Combine(this.ContextDirectory, $"{safeName}.chat.json");

            try
            {
                List<LlamaChatMessage> snapshot;
                lock (this._conversationLock)
                {
                    snapshot = this.ConversationMessages
                        .Select(m => new LlamaChatMessage { Role = m.Role, Content = m.Content, CreatedAtUtc = m.CreatedAtUtc })
                        .ToList();
                }

                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(jsonPath, json, cancellationToken);
                await StaticLogger.LogAsync($"[LlamaCpp][Context] Saved {snapshot.Count} messages to {Path.GetFileName(jsonPath)}");

                return new LlamaContextSaveResult
                {
                    Success = true,
                    FilePath = jsonPath
                };
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"[LlamaCpp][Context] Save failed: {ex.Message}");
                return new LlamaContextSaveResult
                {
                    Success = false,
                    FilePath = jsonPath,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<bool> LoadContextAsync(string contextPathOrName, int slotId = 0, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(contextPathOrName))
            {
                return false;
            }

            string filePath = this.ResolveContextPath(contextPathOrName);
            if (!File.Exists(filePath))
            {
                return false;
            }

            try
            {
                // JSON-based conversation restore
                if (filePath.EndsWith(".chat.json", StringComparison.OrdinalIgnoreCase))
                {
                    string json = await File.ReadAllTextAsync(filePath, cancellationToken);
                    var messages = JsonSerializer.Deserialize<List<LlamaChatMessage>>(json) ?? [];

                    lock (this._conversationLock)
                    {
                        this.ConversationMessages.Clear();
                        this.ConversationMessages.AddRange(messages);
                    }

                    await StaticLogger.LogAsync($"[LlamaCpp][Context] Loaded {messages.Count} messages from {Path.GetFileName(filePath)}");
                    return true;
                }

                // Legacy: slot-based restore for .bin files
                await this.ExecuteSlotActionAsync(slotId, "restore", filePath, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"[LlamaCpp][Context] Load failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteContextAsync(string contextPathOrName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string filePath = this.ResolveContextPath(contextPathOrName);
            if (!File.Exists(filePath))
            {
                return false;
            }

            try
            {
                File.Delete(filePath);
                return true;
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"[LlamaCpp][Context] Delete failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ClearServerContextAsync(int slotId = 0, CancellationToken cancellationToken = default)
        {
            try
            {
                await this.ExecuteSlotActionAsync(slotId, "erase", null, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"[LlamaCpp][Context] Erase failed: {ex.Message}");
                return false;
            }
        }

        private async Task ExecuteSlotActionAsync(int slotId, string action, string? filePath, CancellationToken cancellationToken)
        {
            if (!this.IsServerRunning || string.IsNullOrWhiteSpace(this.CurrentBaseUrl))
            {
                throw new InvalidOperationException("llama.cpp server is not running. Load a model first.");
            }

            var url = $"{this.CurrentBaseUrl}/slots/{slotId}?action={Uri.EscapeDataString(action)}";
            string? slotFilename = !string.IsNullOrWhiteSpace(filePath) ? Path.GetFileName(filePath) : null;
            HttpResponseMessage response;

            if (string.IsNullOrWhiteSpace(slotFilename))
            {
                response = await this._httpClient.PostAsync(url, content: null, cancellationToken);
            }
            else
            {
                response = await this._httpClient.PostAsJsonAsync(url, new JsonObject { ["filename"] = slotFilename }, cancellationToken);
            }

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(slotFilename))
            {
                response.Dispose();
                var fallbackUrl = $"{url}&filename={Uri.EscapeDataString(slotFilename)}";
                response = await this._httpClient.PostAsync(fallbackUrl, content: null, cancellationToken);
            }

            response.EnsureSuccessStatusCode();
        }

        private string ResolveContextPath(string contextPathOrName)
        {
            if (Path.IsPathRooted(contextPathOrName))
            {
                return contextPathOrName;
            }

            // Try .chat.json first, then .bin
            string jsonCandidate = contextPathOrName.EndsWith(".chat.json", StringComparison.OrdinalIgnoreCase)
                ? contextPathOrName
                : $"{contextPathOrName}.chat.json";

            string jsonPath = Path.Combine(this.ContextDirectory, jsonCandidate);
            if (File.Exists(jsonPath))
            {
                return jsonPath;
            }

            string binCandidate = contextPathOrName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                ? contextPathOrName
                : $"{contextPathOrName}.bin";

            return Path.Combine(this.ContextDirectory, binCandidate);
        }

        private static string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "context" : sanitized;
        }
    }
}
