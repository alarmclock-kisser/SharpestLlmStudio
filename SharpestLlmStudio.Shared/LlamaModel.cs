using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace SharpestLlmStudio.Shared
{
    public class LlamaModelInfo
    {
        public string Name { get; set; }
        public string ModelRootDirectory { get; set; }

        public string ModelFilePath { get; set; }
        public string? MmprojFilePath { get; set; }

        public double? ParametersB { get; set; } = null;

        public double SizeInMb { get; set; } = 0;

        public bool IsOmni { get; set; } = false;

        /// <summary>
        /// True if the model uses ternary quantization (TQ1_0, TQ2_0), which is incompatible with Flash Attention in llama.cpp/CUDA.
        /// </summary>
        public bool IsTernaryQuantized
        {
            get
            {
                string[] candidates = [this.Name, Path.GetFileNameWithoutExtension(this.ModelFilePath)];
                foreach (var candidate in candidates)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(candidate, @"(?:^|[-_.\s])TQ[12]_0(?:[-_.\s]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public DateTime LastModified
        {
            get
            {
                var modelTime = File.GetLastWriteTime(this.ModelFilePath);
                var mmprojTime = string.IsNullOrEmpty(this.MmprojFilePath) ? modelTime : File.GetLastWriteTime(this.MmprojFilePath);
                return modelTime > mmprojTime ? modelTime : mmprojTime;
            }
        }

        public string DisplayName
        {
            get
            {
                string tag = "[ ≡ ] ";
                if (this.IsOmni)
                {
                    tag = "[ ☆ ] ";
                }
                else if (File.Exists(this.MmprojFilePath))
                {
                    tag = "[ ◎ ]";
                }

                return $"{tag}{this.Name} <{(this.ParametersB.HasValue ? $"{(this.ParametersB < 1 ? $"{(int)(this.ParametersB * 1000)}M" : $"{(int)(this.ParametersB)}B")}" : "?")}> ({(this.SizeInMb / 1024.0):F3} GB)";
            }
        }


        public LlamaModelInfo(string modelRootDirectory)
        {
            if (!Directory.Exists(modelRootDirectory))
            {
                throw new Exception($"Model root directory does not exist: {modelRootDirectory}");
            }

            this.ModelRootDirectory = Path.GetFullPath(modelRootDirectory);
            this.Name = Path.GetFileName(modelRootDirectory);

            var modelFiles = Directory.GetFiles(modelRootDirectory, "*.gguf", SearchOption.AllDirectories);
            if (modelFiles.Length <= 0)
            {
                throw new Exception($"No .gguf model file found in directory: {modelRootDirectory}");
            }

            // Special case for MiniCPM-o: It often has many .gguf files (shards/adapters)
            bool isMiniCPM = this.Name.Contains("MiniCPM", StringComparison.OrdinalIgnoreCase);

            if (modelFiles.Length > 2 && !isMiniCPM)
            {
                throw new Exception($"Too many (>2) .gguf model file found in directory: {modelRootDirectory}");
            }

            // Detect Omni status based on file count
            this.IsOmni = modelFiles.Length > 2 || isMiniCPM;

            // Detect mmproj / vision / encoder gguf files
            // For Omni models (e.g. MiniCPM-o), the vision component may not be named "mmproj"
            // but instead "vision", "encoder", "projector", etc.
            string[] visionKeywords = ["mmproj", "vision", "encoder", "projector"];

            string? FindVisionFile(string[] files)
            {
                foreach (var keyword in visionKeywords)
                {
                    var match = files.FirstOrDefault(f => Path.GetFileName(f).Contains(keyword, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        return match;
                    }
                }
                return null;
            }

            bool IsVisionFile(string f)
            {
                var name = Path.GetFileName(f);
                return visionKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
            }

            if (modelFiles.Length == 1 || (isMiniCPM && modelFiles.Length > 0))
            {
                // For MiniCPM, we pick the main model file (the one without vision keywords).
                this.ModelFilePath = modelFiles.FirstOrDefault(f => !IsVisionFile(f)) ?? modelFiles[0];
                this.MmprojFilePath = FindVisionFile(modelFiles);
            }
            else
            {
                this.MmprojFilePath = FindVisionFile(modelFiles);
                this.ModelFilePath = modelFiles.FirstOrDefault(f => !IsVisionFile(f)) ?? string.Empty;
            }

            // Get size of model file and mmproj file (if exists)
            long modelFileSize = Directory.GetFiles(modelRootDirectory, "*.gguf", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            this.SizeInMb = modelFileSize / (1024.0 * 1024.0);

            // Try to parse parameters from directory name first, then file name
            // Examples: "model-7b", "model-0.8b", "model-258m", "Qwen2.5-0.5B-Instruct"
            string[] candidates = [this.Name, Path.GetFileNameWithoutExtension(this.ModelFilePath)];
            foreach (var candidate in candidates)
            {
                // Require a separator or start-of-string before the number, and a separator/end after the unit
                // to avoid false matches on quantization tokens like "Q8_0", "IQ4_NL", etc.
                var match = System.Text.RegularExpressions.Regex.Match(candidate, @"(?:^|[-_\s])(\d+(?:\.\d+)?)([bBmM])(?:[-_\s.]|$)");
                if (match.Success)
                {
                    var numberPart = match.Groups[1].Value;
                    var unitPart = match.Groups[2].Value.ToLower();
                    if (double.TryParse(numberPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number))
                    {
                        this.ParametersB = unitPart == "b" ? number : number / 1000.0;
                        break;
                    }
                }
            }
        }

    }

    public class LlamaModelLoadRequest
    {
        public required LlamaModelInfo ModelInfo { get; set; }

        // Pfad zur llama-server.exe (z.B. "D:\llama-cpp\llama-server.exe")
        public required string ServerExecutablePath { get; set; }

        // Port, auf dem der Server lauschen soll
        public int Port { get; set; } = 8080;

        // Host (Standard: localhost)
        public string Host { get; set; } = "127.0.0.1";

        // Number of GPU Layers (-ngl). 99 für vollständigen VRAM-Offload.
        public int GpuLayers { get; set; } = 99;

        // Context Size (-c). Wie viele Token das Modell sich merken kann.
        public int ContextSize { get; set; } = 4096;

        // Multimodal Projection mitladen?
        public bool IncludeMmproj { get; set; } = true;

        // Weitere optionale Server-Parameter für später (z.B. Flash Attention)
        public bool UseFlashAttention { get; set; } = true;
    }

    public class LlamaModelLoadResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string BaseApiUrl { get; set; } = string.Empty;
        public TimeSpan LoadTime { get; set; }
        public bool ReusedExistingInstance { get; set; }
        public string? ActiveModelId { get; set; }
    }
}
