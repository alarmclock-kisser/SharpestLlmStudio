using System;
using System.Collections.Generic;
using System.Text;

namespace SharpestLlmStudio.Shared
{
    public class OnnxWhisperModel
    {
        public string ModelRootDirectory { get; set; } = string.Empty;
        public string ModelFilePath {  get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;

        public string[]? ConfigurationFiles { get; set; } = null;       // If .bin format, null config files. If .onnx, may have multiple config files (e.g. encoder/tokenizer).


        public OnnxWhisperModel() { }

        public OnnxWhisperModel(string modelRootDirectory)
        {
            if (!Directory.Exists(modelRootDirectory))
            {
                throw new DirectoryNotFoundException($"Model root directory not found: {modelRootDirectory}");
            }

            this.ModelRootDirectory = modelRootDirectory;

            var modelFiles = Directory.GetFiles(modelRootDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (modelFiles.Length == 0)
            {
                throw new FileNotFoundException("No model file (.bin or .onnx) found in directory: " + modelRootDirectory);
            }

            // For simplicity, take the first model file found. Could be enhanced to support multiple models per directory if needed.
            this.ModelFilePath = modelFiles[0];
            this.ModelName = Path.GetFileNameWithoutExtension(this.ModelFilePath);
            if (Path.GetExtension(this.ModelFilePath).Equals(".onnx", StringComparison.OrdinalIgnoreCase))
                {
                    this.ConfigurationFiles = Directory.GetFiles(modelRootDirectory, "*.json", SearchOption.TopDirectoryOnly);
            }
        }


    }
}
