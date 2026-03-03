using System;
using System.Collections.Generic;
using System.Text;

namespace SharpestLlmStudio.Shared
{
    public class HardwareStatistics
    {
        public CpuStatistics CpuStats { get; set; }
        public MemoryStatistics RamStats { get; set; }
        public GpuStatistics GpuStats {  get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int FetchingDurationMs { get; set; } = 0;


        public string? ErrorMessage { get; set; } = null;


        public HardwareStatistics(IEnumerable<double>? cpuCoreLoads = null, long totalRamBytes = 0, long usedRamBytes = 0, double gpuCoreLoad = 0, double wattsUsage = 0, double totalVramBytes = 0, double usedVramBytes = 0)
        {
            this.CpuStats = new CpuStatistics(cpuCoreLoads);
            this.RamStats = new MemoryStatistics(totalRamBytes, usedRamBytes);
            this.GpuStats = new GpuStatistics(gpuCoreLoad, wattsUsage, totalVramBytes, usedVramBytes);
        }
    }



    public class CpuStatistics
    {
        public string Name { get; set; } = "CPU";
        public string Manufacturer => this.Name switch
        {
            string n when n.Contains("Intel", StringComparison.OrdinalIgnoreCase) => "Intel",
            string n when n.Contains("AMD", StringComparison.OrdinalIgnoreCase) => "AMD",
            string n when n.Contains("Apple", StringComparison.OrdinalIgnoreCase) => "Apple",
            _ => "Unknown"
        };

        public List<double> CpuCoreLoads { get; set; } = [];
        public int CpuCoreCount => this.CpuCoreLoads.Count;
        public double AverageLoadPercentage => this.CpuCoreLoads.Count > 0 ? this.CpuCoreLoads.Average() : 0;

        public CpuStatistics(IEnumerable<double>? cpuCoreLoads = null)
        {
            this.CpuCoreLoads = cpuCoreLoads?.ToList() ?? [];
        }
    }

    public class MemoryStatistics
    {
        public string Name { get; set; } = "RAM";

        public double TotalMemoryMb { get; set; }
        public double UsedMemoryMb { get; set; }
        public double FreeMemoryMb => this.TotalMemoryMb - this.UsedMemoryMb;
        public double MemoryUsagePercentage => this.TotalMemoryMb > 0 ? (this.UsedMemoryMb / this.TotalMemoryMb) * 100 : 0;

        public MemoryStatistics(double totalMemoryBytes = 0, double usedMemoryBytes = 0)
        {
            this.TotalMemoryMb = totalMemoryBytes / (1024 * 1024);
            this.UsedMemoryMb = usedMemoryBytes / (1024 * 1024);
        }
    }

    public class GpuStatistics
    {
        public string Name { get; set; } = "GPU";
        public string Manufacturer => this.Name switch
        {
            string n when n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => "NVIDIA",
            string n when n.Contains("AMD", StringComparison.OrdinalIgnoreCase) => "AMD",
            string n when n.Contains("Intel", StringComparison.OrdinalIgnoreCase) => "Intel",
            string n when n.Contains("Apple", StringComparison.OrdinalIgnoreCase) => "Apple",
            _ => "Unknown"
        };

        public double CoreLoadPercentage { get; set; }
        public double WattsUsage { get; set; }
        public double TotalKiloWattsUsed { get; set; }

        public MemoryStatistics VramStats { get; set; }


        public GpuStatistics(double gpuCoreLoad = 0, double wattsUsage = 0, double totalVramBytes = 0, double usedVramBytes = 0)
        {
            this.CoreLoadPercentage = gpuCoreLoad * 100;
            this.WattsUsage = wattsUsage;
            this.VramStats = new MemoryStatistics(totalVramBytes, usedVramBytes);
        }
    }

}
