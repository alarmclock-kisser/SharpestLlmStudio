using System;
using System.Collections.Generic;

namespace SharpestLlmStudio.Shared
{
    public sealed class LlamaChatMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class LlamaGenerationRequest
    {
        public string Prompt { get; set; } = string.Empty;
        public string? SystemPrompt { get; set; }
        public string[]? Images { get; set; }
        public int MaxWidthAndHeight { get; set; } = 720; // Set to 0 to disable downsizing and send full-size images
        public string ImageFormat { get; set; } = "jpg"; // Format to use when downsizing and serializing images. Supported: "jpg", "png", "bmp"

        public bool Isolated { get; set; } = false;
        public bool PersistConversation { get; set; } = true;
        public bool IncludeConversationHistory { get; set; } = true;

        public int MaxTokens { get; set; } = 1024;
        public double Temperature { get; set; } = 0.7;
        public double TopP { get; set; } = 0.9;
        public double RepetitionPenalty { get; set; } = 1.1;
        public string[]? StopSequences { get; set; }
        public bool Stream { get; set; } = true;
    }

    public sealed class LlamaContextSaveResult
    {
        public bool Success { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
    }

    public sealed class LlamaKnowledgeEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Key { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? SourcePath { get; set; }
        public float[] Vector { get; set; } = [];
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class LlamaKnowledgeSearchResult
    {
        public required LlamaKnowledgeEntry Entry { get; set; }
        public double Similarity { get; set; }
    }

    public sealed class LoadedImageMetadata
    {
        public string FileName { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public long FileSizeBytes { get; init; }
    }
}
