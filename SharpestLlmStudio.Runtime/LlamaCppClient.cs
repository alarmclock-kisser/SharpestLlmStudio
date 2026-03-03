using System;
using System.Collections.Generic;
using System.Text;
using SharpestLlmStudio.Monitoring;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        public readonly GpuMonitor? GPUMonitor = null;

        public List<string> ModelDirectories { get; set; } = [];
        public List<LlamaModelInfo> Models { get; set; } = [];



        public LlamaCppClient(WebAppSettings settings, GpuMonitor? gpuMonitor = null)
        {
            this.GetModels(settings.ModelDirectories?.ToArray());


        }



        public string[] GetModels(string[]? modelDirectories)
        {
            if (modelDirectories != null)
            {
                this.ModelDirectories.AddRange(modelDirectories);
            }

            this.ModelDirectories = this.ModelDirectories.Distinct().Where(d => Directory.Exists(d)).ToList();
            string[] candidateRootDirs = this.ModelDirectories.SelectMany(d => Directory.GetDirectories(d)).Where(d => Directory.GetFiles(d, "*.gguf").Length > 0).ToArray();

            foreach (string candidateRootDir in candidateRootDirs)
            {
                try
                {
                    LlamaModelInfo modelInfo = new(candidateRootDir);
                    if (!this.Models.Any(m => m.ModelRootDirectory == modelInfo.ModelRootDirectory))
                    {
                        this.Models.Add(modelInfo);
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Error loading model from directory {candidateRootDir}: {ex.Message}");
                }
            }

            return this.Models.Select(m => m.ModelRootDirectory).ToArray();
        }

    }
}
