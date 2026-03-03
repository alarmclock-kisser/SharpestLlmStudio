using System;
using System.Collections.Generic;
using System.Text;

namespace SharpestLlmStudio.Shared
{
    public class WebAppSettings
    {
        public bool CreateLogFile { get; set; }
        public string? LogDirectory { get; set; }
        public int MaxPreviousLogFiles { get; set; } = -1; // unlimited
        public bool EnableMonitoring { get; set; } = true;

        public string ServerExecutablePath { get; set; } = "llama-server.exe";
        public string[]? ModelDirectories { get; set; }

        public int DefaultContextSize { get; set; } = 4096;
        public int DefaultMaxTokens { get; set; } = 2048;
        public double DefaultTemperature { get; set; } = 0.7;



    }
}
