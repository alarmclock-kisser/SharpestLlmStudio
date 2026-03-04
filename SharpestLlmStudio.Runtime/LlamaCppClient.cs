using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using SharpestLlmStudio.Monitoring;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        public readonly GpuMonitor? GPUMonitor = null;
        private readonly WebAppSettings _settings;
        private readonly Lock _conversationLock = new();
        private readonly Lock _knowledgeLock = new();
        private readonly Lock _generationStatsLock = new();

        public string AppDataDirectory { get; }
        public string ContextDirectory { get; }
        public string EmbeddingStoreDirectory { get; }
        public List<LlamaChatMessage> ConversationMessages { get; } = [];
        public List<LlamaKnowledgeEntry> KnowledgeEntries { get; } = [];
        public GenerationStats LastGenerationStats { get; private set; } = new();

        public List<string> ModelDirectories { get; set; } = [];
        public List<LlamaModelInfo> Models { get; set; } = [];



        public LlamaCppClient(WebAppSettings settings, GpuMonitor? gpuMonitor = null)
        {
            this._settings = settings;
            this.GPUMonitor = gpuMonitor;

            this.AppDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SharpestLlmStudio");
            this.ContextDirectory = Path.Combine(this.AppDataDirectory, "contexts");
            this.EmbeddingStoreDirectory = Path.Combine(this.AppDataDirectory, "embeddings");

            Directory.CreateDirectory(this.AppDataDirectory);
            Directory.CreateDirectory(this.ContextDirectory);
            Directory.CreateDirectory(this.EmbeddingStoreDirectory);

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

        [SupportedOSPlatform("windows")]
        public async Task<HardwareStatistics?> GetCurrentHardwareStatisticsAsync()
        {
            if (this.GPUMonitor == null)
            {
                return null;
            }

            try
            {
                return await this.GPUMonitor.GetCurrentHardwareStatisticsAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }


        public int? GetLlamaServerExeInstancesCount()
        {
            try
            {
                var processes = System.Diagnostics.Process
                    .GetProcesses()
                    .Where(p => string.Equals(p.ProcessName, "llama-server", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.ProcessName, "llama-server.exe", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.ProcessName, "LlamaServer", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return processes.Count;
            }
            catch (Exception ex)
            {
                StaticLogger.Log(ex, "Error while counting llama-server process instances");
                return null;
            }
        }


        public int? KillAllLlamaServerExeInstances()
        {
            try
            {
                var processes = System.Diagnostics.Process
                    .GetProcesses()
                    .Where(p => string.Equals(p.ProcessName, "llama-server", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.ProcessName, "llama-server.exe", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(p.ProcessName, "LlamaServer", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var process in processes)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit(2000);
                    }
                }

                return processes.Count;
            }
            catch (Exception ex)
            {
                StaticLogger.Log(ex, "Error while killing llama-server process instances");
                return null;
            }
        }

        public GenerationStats GetLastGenerationStatsSnapshot()
        {
            lock (this._generationStatsLock)
            {
                return new GenerationStats
                {
                    GenerationStarted = this.LastGenerationStats.GenerationStarted,
                    GenerationFinished = this.LastGenerationStats.GenerationFinished,
                    TotalTokensGenerated = this.LastGenerationStats.TotalTokensGenerated,
                    UsedWattsApprox = this.LastGenerationStats.UsedWattsApprox,
                    TotalContextTokens = this.LastGenerationStats.TotalContextTokens,
                    ContextSize = this.LastGenerationStats.ContextSize
                };
            }
        }

    }
}
