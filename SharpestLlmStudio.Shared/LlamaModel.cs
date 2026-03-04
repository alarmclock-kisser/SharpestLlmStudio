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

        public DateTime LastModified
        {
            get
            {
                var modelTime = File.GetLastWriteTime(this.ModelFilePath);
                var mmprojTime = string.IsNullOrEmpty(this.MmprojFilePath) ? modelTime : File.GetLastWriteTime(this.MmprojFilePath);
                return modelTime > mmprojTime ? modelTime : mmprojTime;
            }
        }


        public string DisplayName => $"{(File.Exists(this.MmprojFilePath) ? "[VL] " : "[ ≡ ] ")}{this.Name} <{(this.ParametersB.HasValue ? $"{(this.ParametersB < 1 ? $"{(int) (this.ParametersB * 1000)}M" : $"{(int) (this.ParametersB)}B")}" : "?")}> ({(this.SizeInMb / 1024.0):F3} GB)";


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
            else if (modelFiles.Length > 2)
            {
                throw new Exception($"Too many (>2) .gguf model file found in directory: {modelRootDirectory}");
            }

            if (modelFiles.Length == 1)
            {
                this.ModelFilePath = modelFiles[0];
            }
            else
            {
                this.MmprojFilePath = modelFiles.FirstOrDefault(f => f.Contains("mmproj", StringComparison.OrdinalIgnoreCase));
                if (this.MmprojFilePath == null)
                {
                    throw new Exception($"Multiple .gguf model files found but no mmproj file found in directory: {modelRootDirectory}");
                }
                this.ModelFilePath = modelFiles.FirstOrDefault(f => !f.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                if (string.IsNullOrEmpty(this.ModelFilePath))
                {
                    throw new Exception($"Multiple .gguf model files found but no non-mmproj file found in directory: {modelRootDirectory}");
                }
            }
            // Get size of model file and mmproj file (if exists)
            long modelFileSize = new FileInfo(this.ModelFilePath).Length;
            if (File.Exists(this.MmprojFilePath))
            {
                modelFileSize += new FileInfo(this.MmprojFilePath).Length;
            }
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
    }
}
