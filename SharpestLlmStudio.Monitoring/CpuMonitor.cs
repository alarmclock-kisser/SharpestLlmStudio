using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Management;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Monitoring
{
    [SupportedOSPlatform("windows")]
    public static class CpuMonitor
    {
        private static readonly PerformanceCounter[] _cpuCounters = CreateCpuCounters();
        private static readonly TimeSpan _samplingInterval = TimeSpan.FromMilliseconds(250);
        private static DateTime _lastSampleUtc = DateTime.MinValue;
        private static double[] _lastUsages = [];
        private static readonly Lock _sampleLock = new();

        private static PerformanceCounter[] CreateCpuCounters()
        {
            int coreCount = Environment.ProcessorCount;
            var counters = new PerformanceCounter[coreCount];

            for (int i = 0; i < coreCount; i++)
            {
                counters[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);
                // erste Probe, damit der nächste Wert „richtig“ ist
                _ = counters[i].NextValue();
            }

            _lastUsages = new double[coreCount];
            return counters;
        }

        /// <summary>
        /// CPU-Auslastung pro logischem Prozessor (0.0f - 1.0f).
        /// Nicht-blockierend: liefert gecachte Werte, wenn Intervall noch nicht abgelaufen.
        /// </summary>
        public static Task<double[]> GetThreadUsagesAsync(CancellationToken cancellationToken = default)
        {
            lock (_sampleLock)
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastSampleUtc;

                if (elapsed > _samplingInterval * 4)
                {
                    for (int i = 0; i < _cpuCounters.Length; i++)
                    {
                        _ = _cpuCounters[i].NextValue();
                    }
                    Thread.Sleep(_samplingInterval);
                }
                else if (elapsed < _samplingInterval && _lastUsages.Length == _cpuCounters.Length)
                {
                    return Task.FromResult((double[]) _lastUsages.Clone());
                }

                int coreCount = _cpuCounters.Length;
                var usages = new double[coreCount];

                for (int i = 0; i < coreCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    float percent = _cpuCounters[i].NextValue();
                    if (percent < 0f)
                    {
                        percent = 0f;
                    }

                    if (percent > 100f)
                    {
                        percent = 100f;
                    }

                    usages[i] = percent / 100f;
                }

                _lastUsages = usages;
                _lastSampleUtc = now;
                return Task.FromResult((double[]) usages.Clone());
            }
        }

        /// <summary>
        /// Sync-Wrapper, falls du irgendwo keine async-Methode aufrufen willst.
        /// </summary>
        public static double[] GetThreadUsages()
            => GetThreadUsagesAsync().GetAwaiter().GetResult();

        // -------- Speicher (physisch) --------
        // Die Speicherabfragen sind sehr schnell und blockieren nicht nennenswert.
        // Async bringt hier praktisch nichts, daher bleiben sie synchron.

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private static MEMORYSTATUSEX GetMemoryStatus()
        {
            var status = new MEMORYSTATUSEX
            {
                dwLength = (uint) Marshal.SizeOf<MEMORYSTATUSEX>()
            };

            if (!GlobalMemoryStatusEx(ref status))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return status;
        }

        /// <summary>
        /// Gesamter physischer Speicher in BYTES.
        /// </summary>
        public static long GetTotalMemoryBytes()
        {
            var status = GetMemoryStatus();
            return (long) status.ullTotalPhys;
        }

        /// <summary>
        /// Verwendeter physischer Speicher in BYTES.
        /// </summary>
        public static long GetUsedMemoryBytes()
        {
            var status = GetMemoryStatus();
            ulong used = status.ullTotalPhys - status.ullAvailPhys;
            return (long) used;
        }

        public static string GetCpuName()
        {
            try
            {
                return new ManagementObjectSearcher("select Name from Win32_Processor")
                    .Get()
                    .Cast<ManagementObject>()
                    .Select(mo => mo["Name"]?.ToString()?.Trim())
                    .FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? "N/A";
            }
            catch (Exception ex)
            {
                StaticLogger.Log(ex);
            }

            return "Unknown CPU";
        }
    }

}

