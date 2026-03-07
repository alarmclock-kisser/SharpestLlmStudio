using System;
using System.Collections.Generic;
using System.Text;

namespace SharpestLlmStudio.Shared
{
    public class WebAppSettings
    {
        public bool CreateLogFile { get; set; }
        public string LogDirectory { get; set; } = string.Empty;
        public int MaxPreviousLogFiles { get; set; } = -1; // unlimited
        public bool EnableMonitoring { get; set; } = true;
        public string DarkMode { get; set; } = "auto"; // "auto", "on", "off"

        public string ServerExecutablePath { get; set; } = "llama-server.exe";
        public string[] ModelDirectories { get; set; } = [];

        public bool KillExistingServerInstances { get; set; } = false;
        public int IdleShutdownMinutes { get; set; } = 15;
        public int IdleCheckIntervalSeconds { get; set; } = 15;
        public string DefaultModel { get; set; } = string.Empty;
        public int DefaultContextSize { get; set; } = 4096;
        public int DefaultBatchSize { get; set; } = 512;
        public int DefaultMaxTokens { get; set; } = 2048;
        public double DefaultTemperature { get; set; } = 0.7;
        public double DefaultRepetitionPenalty { get; set; } = 1.1;
        public List<string> SystemPrompts { get; set; } = [];
        // Image handling defaults for generation
        // Use 0 to disable downsizing (send full-size images). Default is 720.
        public int DefaultImageMaxDimension { get; set; } = 720;
        public string DefaultImageFormat { get; set; } = "jpg";
        public bool AgentCommandReadOnlyMode { get; set; } = true;
        public bool AgentAllowElevatedCommands { get; set; } = true;
        public bool AgentShowCommandWindow { get; set; } = false;
        public bool AgentAutoContinue { get; set; } = true;
        public bool AllowAllNonAdminCommands { get; set; } = false;
        public bool AutoAllowWebSearch { get; set; } = true;



    }
}
