using SharpestLlmStudio.Monitoring;
using SharpestLlmStudio.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

public sealed class GpuMonitor : IDisposable
{
    // ------------------------ Public API ------------------------

    public static Dictionary<DateTime, HardwareStatistics> HardwareStatsHistory { get; private set; } = [];
    public static readonly List<double> PowerUsageHistory = [];
    private static readonly Lock PowerUsageHistoryLock = new();
    private static readonly TimeSpan DefaultPowerSampleInterval = TimeSpan.FromMilliseconds(100);
    public static double TotalKiloWattsUsed
    {
        get
        {
            lock (PowerUsageHistoryLock)
            {
                return PowerUsageHistory.Sum(watts => Math.Max(0.0, watts)) * (DefaultPowerSampleInterval.TotalHours / 1000.0);
            }
        }
    }


    public sealed record Sample(DateTimeOffset Timestamp, double GpuUtil01, double? PowerWatts);

    public sealed class ProcessingSession
    {
        public int DeviceIndex { get; internal set; }
        public DateTimeOffset StartedAt { get; internal set; }
        public DateTimeOffset EndedAt { get; internal set; }
        public TimeSpan Duration => this.EndedAt - this.StartedAt;

        public double AvgUtil01 { get; internal set; }
        public double PeakUtil01 { get; internal set; }

        public double? AvgPowerWatts { get; internal set; }
        public double? PeakPowerWatts { get; internal set; }

        public double? EnergyJoules { get; internal set; }
        public double? EnergyWh => this.EnergyJoules is null ? null : this.EnergyJoules.Value / 3600.0;

        public IReadOnlyList<Sample> Samples => this._samples;
        private readonly List<Sample> _samples = [];

        internal void Add(Sample s, int maxSamples)
        {
            if (maxSamples > 0 && this._samples.Count >= maxSamples)
            {
                return;
            }

            this._samples.Add(s);
        }

        public override string ToString()
        {
            return $"GPU#{this.DeviceIndex} Session {this.StartedAt:u} - {this.EndedAt:u} ({this.Duration}): AvgUtil={this.AvgUtil01:P1}, PeakUtil={this.PeakUtil01:P1}, AvgPower={this.AvgPowerWatts:0.0}W, PeakPower={this.PeakPowerWatts:0.0}W, Energy={this.EnergyWh:0.000}Wh, Samples={this.Samples.Count}";
        }
    }

    /// <summary>
    /// Automatically filled with completed processing sessions.
    /// Thread-safe snapshotting via GetSessionsSnapshot().
    /// </summary>
    public List<ProcessingSession> ProcessingSessions { get; } = [];

    /// <summary> Current GPU load [0..1] sampled by the background worker. </summary>
    public double CurrentLoad01 => Volatile.Read(ref this._currentLoad01);

    /// <summary> Current GPU power in watts (if NVML supports it); null otherwise. </summary>
    public double? CurrentPowerWatts
    {
        get
        {
            var v = Volatile.Read(ref this._currentPowerMilliwatts);
            return v < 0 ? null : v / 1000.0;
        }
    }

    /// <summary> Returns last completed session or null if none. </summary>
    public ProcessingSession? LastSessionOrDefault()
    {
        lock (this._sessionsLock)
        {
            return this.ProcessingSessions.Count == 0 ? null : this.ProcessingSessions[^1];
        }
    }

    // ------------------------ Options ------------------------

    public int DeviceIndex { get; }
    public static List<string> GpuNames = GetGpuNames();
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary> Start session when util >= threshold for StartHold. </summary>
    public double StartThresholdUtil01 { get; set; } = 0.10; // 10%

    /// <summary> End session when util < threshold for EndHold (hysteresis). </summary>
    public double EndThresholdUtil01 { get; set; } = 0.07; // 7%

    public TimeSpan StartHold { get; set; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan EndHold { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Keep at most N samples per session to cap RAM. 0 = unlimited.
    /// </summary>
    public int MaxSamplesPerSession { get; set; } = 0;

    // ------------------------ Lifecycle ------------------------

    public GpuMonitor(int deviceIndex = 0)
    {
        this.DeviceIndex = Math.Max(0, deviceIndex);

        this._cts = new CancellationTokenSource();
        this._worker = Task.Run(() => this.WorkerLoop(this._cts.Token), CancellationToken.None);
    }

    public void Dispose()
    {
        try
        {
            this._cts.Cancel();
        }
        catch { /* ignore */ }

        try
        {
            this._worker.Wait(500);
        }
        catch { /* ignore */ }

        this._cts.Dispose();
    }

    // ------------------------ Internals ------------------------

    private readonly Lock _sessionsLock = new();
    private readonly Lock _stateLock = new();

    private readonly CancellationTokenSource _cts;
    private readonly Task _worker;

    private ProcessingSession? _currentSession;

    private double _currentLoad01;
    private long _currentPowerMilliwatts = -1; // -1 => unknown/unavailable

    private async Task WorkerLoop(CancellationToken ct)
    {
        TimeSpan aboveAccum = TimeSpan.Zero;
        TimeSpan belowAccum = TimeSpan.Zero;

        Sample? prev = null;

        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            // Sample GPU
            double util01 = NvmlGpu.TryGetGpuUtilization(this.DeviceIndex) ?? 0.0;
            uint? mw = NvmlGpu.TryGetGpuPowerMilliwatts(this.DeviceIndex);

            Volatile.Write(ref this._currentLoad01, util01);
            Volatile.Write(ref this._currentPowerMilliwatts, mw.HasValue ? mw.Value : -1);

            var s = new Sample(now, util01, mw.HasValue ? mw.Value / 1000.0 : null);

            lock (PowerUsageHistoryLock)
            {
                PowerUsageHistory.Add(s.PowerWatts ?? 0.0);
            }

            lock (this._stateLock)
            {
                if (this._currentSession == null)
                {
                    // START detection (sustained above threshold)
                    if (util01 >= this.StartThresholdUtil01)
                    {
                        aboveAccum += this.SampleInterval;
                        if (aboveAccum >= this.StartHold)
                        {
                            this._currentSession = new ProcessingSession
                            {
                                DeviceIndex = this.DeviceIndex,
                                StartedAt = now
                            };
                            this._currentSession.Add(s, this.MaxSamplesPerSession);

                            aboveAccum = TimeSpan.Zero;
                            belowAccum = TimeSpan.Zero;
                            prev = null; // reset integration baseline
                        }
                    }
                    else
                    {
                        aboveAccum = TimeSpan.Zero;
                    }
                }
                else
                {
                    // Session ACTIVE: append
                    this._currentSession.Add(s, this.MaxSamplesPerSession);

                    // Energy integrate if power exists
                    if (s.PowerWatts is double pw && prev?.PowerWatts is double pprev)
                    {
                        double dt = (s.Timestamp - prev.Timestamp).TotalSeconds;
                        if (dt > 0 && dt < 5.0) // guard huge gaps
                        {
                            double joules = 0.5 * (pprev + pw) * dt;
                            this._currentSession.EnergyJoules = (this._currentSession.EnergyJoules ?? 0.0) + joules;
                        }
                    }

                    // END detection with hysteresis + micro dips tolerance
                    if (util01 < this.EndThresholdUtil01)
                    {
                        belowAccum += this.SampleInterval;
                        if (belowAccum >= this.EndHold)
                        {
                            this._currentSession.EndedAt = now;
                            FinalizeSession(this._currentSession);

                            lock (this._sessionsLock)
                            {
                                this.ProcessingSessions.Add(this._currentSession);
                            }

                            this._currentSession = null;
                            aboveAccum = TimeSpan.Zero;
                            belowAccum = TimeSpan.Zero;
                            prev = null;
                        }
                    }
                    else
                    {
                        belowAccum = TimeSpan.Zero;
                    }
                }
            }

            prev = s;

            try
            {
                await Task.Delay(this.SampleInterval, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        // Close running session on shutdown
        lock (this._stateLock)
        {
            if (this._currentSession != null)
            {
                this._currentSession.EndedAt = DateTimeOffset.UtcNow;
                FinalizeSession(this._currentSession);

                lock (this._sessionsLock)
                {
                    this.ProcessingSessions.Add(this._currentSession);
                }

                this._currentSession = null;
            }
        }
    }

    private static void FinalizeSession(ProcessingSession sess)
    {
        var samples = sess.Samples;
        if (samples.Count == 0)
        {
            sess.AvgUtil01 = 0;
            sess.PeakUtil01 = 0;
            sess.AvgPowerWatts = null;
            sess.PeakPowerWatts = null;
            return;
        }

        double sumU = 0, peakU = 0;
        double sumP = 0, peakP = 0;
        int cntP = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            var u = samples[i].GpuUtil01;
            sumU += u;
            if (u > peakU)
            {
                peakU = u;
            }

            if (samples[i].PowerWatts is double pw)
            {
                sumP += pw;
                if (pw > peakP)
                {
                    peakP = pw;
                }

                cntP++;
            }
        }

        sess.AvgUtil01 = sumU / samples.Count;
        sess.PeakUtil01 = peakU;

        sess.AvgPowerWatts = cntP > 0 ? sumP / cntP : null;
        sess.PeakPowerWatts = cntP > 0 ? peakP : null;

        if (sess.EndedAt == default)
        {
            sess.EndedAt = DateTimeOffset.UtcNow;
        }
    }

    public static List<string> GetGpuNames()
    {
        var names = new List<string>();
        int index = 0;
        while (true)
        {
            if (!NvmlGpu.TryGetGpuUtilization(index).HasValue)
            {
                break;
            }
            names.Add($"GPU#{index}");
            index++;
        }
        return names;
    }

    public long GetTotalVramBytes()
    {
        if (!NvmlGpu.TryGetGpuUtilization(this.DeviceIndex).HasValue)
        {
            return -1;
        }
        
        return NvmlGpu.TryGetGpuTotalMemoryBytes(this.DeviceIndex) ?? -1;
    }

    public long GetUsedVramBytes()
    {
        if (!NvmlGpu.TryGetGpuUtilization(this.DeviceIndex).HasValue)
        {
            return -1;
        }
        
        return NvmlGpu.TryGetGpuUsedMemoryBytes(this.DeviceIndex) ?? -1;
    }


    public void RestartPowerProfiling()
    {
        lock (this._stateLock)
        {
            if (this._currentSession != null)
            {
                this._currentSession.EndedAt = DateTimeOffset.UtcNow;
                FinalizeSession(this._currentSession);
                lock (this._sessionsLock)
                {
                    this.ProcessingSessions.Add(this._currentSession);
                }
            }
            this._currentSession = new ProcessingSession
            {
                DeviceIndex = this.DeviceIndex,
                StartedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public double? EndPowerProfiling()
    {
        if (this._currentSession == null)
        {
            return null;
        }

        lock (this._stateLock)
        {
            if (this._currentSession != null)
            {
                this._currentSession.EndedAt = DateTimeOffset.UtcNow;
                FinalizeSession(this._currentSession);
                lock (this._sessionsLock)
                {
                    this.ProcessingSessions.Add(this._currentSession);
                }

                double totalWattsUsedApprox = this._currentSession.EnergyWh.HasValue ? this._currentSession.EnergyWh.Value * (this._currentSession.Duration.TotalHours > 0 ? this._currentSession.EnergyWh.Value / this._currentSession.Duration.TotalHours : 0) : 0.0;
                this._currentSession = null;
                return totalWattsUsedApprox;
            }
            else
            {
                return null;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public string GetCurrentGpuName()
    {
        // With managementobjectsearcher we could get the actual GPU name, but it causes long hangs on some machines with "WMI: Timeout 20s" and doesn't work well in containerized environments, so we just return a simple name based on the index if NVML is available.
        var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController");
        if (searcher.Get().Count > this.DeviceIndex)
        {
            try
            {
                return searcher.Get().Cast<ManagementObject>().ElementAt(this.DeviceIndex).GetPropertyValue("Name")?.ToString() ?? $"GPU#{this.DeviceIndex}";
            }
            catch
            {
                return $"GPU#{this.DeviceIndex}";
            }
        }
        else
        {
            return $"GPU#{this.DeviceIndex}";
        }
    }


    [SupportedOSPlatform("windows")]
    public async Task<HardwareStatistics> GetCurrentHardwareStatisticsAsync()
    {
        var fetching = DateTime.Now;

        HardwareStatistics hwStats = new(
            await CpuMonitor.GetThreadUsagesAsync(), CpuMonitor.GetTotalMemoryBytes(), CpuMonitor.GetUsedMemoryBytes(),
            this.CurrentLoad01, this.CurrentPowerWatts ?? -1, this.GetTotalVramBytes(), this.GetUsedVramBytes());

        hwStats.FetchingDurationMs = (int) (DateTime.Now - fetching).TotalMilliseconds;

        hwStats.CpuStats.Name = CpuMonitor.GetCpuName();
        hwStats.RamStats.Name = "RAM";
        hwStats.GpuStats.Name = this.GetCurrentGpuName();
        hwStats.GpuStats.VramStats.Name = "VRAM";
        hwStats.GpuStats.TotalKiloWattsUsed = TotalKiloWattsUsed;

        HardwareStatsHistory.Add(hwStats.CreatedAt, hwStats);

        return hwStats;
    }

    // ------------------------ NVML (GPU util + power) ------------------------

    private static class NvmlGpu
    {
        private static int _initialized; // 0/1
        private static readonly Lock InitLock = new();

        public static double? TryGetGpuUtilization(int deviceIndex)
        {
            if (!EnsureInitialized())
            {
                return null;
            }

            var rc = nvmlDeviceGetHandleByIndex_v2((uint) deviceIndex, out var device);
            if (rc != NvmlReturn.Success)
            {
                return null;
            }

            rc = nvmlDeviceGetUtilizationRates(device, out var util);
            if (rc != NvmlReturn.Success)
            {
                return null;
            }

            return util.gpu / 100.0;
        }

        public static uint? TryGetGpuPowerMilliwatts(int deviceIndex)
        {
            if (!EnsureInitialized())
            {
                return null;
            }

            var rc = nvmlDeviceGetHandleByIndex_v2((uint) deviceIndex, out var device);
            if (rc != NvmlReturn.Success)
            {
                return null;
            }

            rc = nvmlDeviceGetPowerUsage(device, out var milliwatts);
            if (rc != NvmlReturn.Success)
            {
                return null;
            }

            return milliwatts;
        }

        public static long? TryGetGpuTotalMemoryBytes(int deviceIndex)
        {
            if (!EnsureInitialized())
            {
                return null;
            }
            var rc = nvmlDeviceGetHandleByIndex_v2((uint)deviceIndex, out var device);
            if (rc != NvmlReturn.Success)
            {
                return null;
            }
            rc = nvmlDeviceGetMemoryInfo(device, out var memInfo);
            if (rc != NvmlReturn.Success)
            {
                return null;
            }
            return (long)memInfo.total;
        }

        public static long? TryGetGpuUsedMemoryBytes(int deviceIndex)
        {
            if (!EnsureInitialized())
            {
                return null;
            }
            var rc = nvmlDeviceGetHandleByIndex_v2((uint)deviceIndex, out var device);
            if (rc != NvmlReturn.Success)
            {
                return null;
            }
            rc = nvmlDeviceGetMemoryInfo(device, out var memInfo);
            if (rc != NvmlReturn.Success)
            {
                return null;
            }
            return (long)memInfo.used;
        }

        private static bool EnsureInitialized()
        {
            if (Volatile.Read(ref _initialized) == 1)
            {
                return true;
            }

            lock (InitLock)
            {
                if (_initialized == 1)
                {
                    return true;
                }

                try
                {
                    var rc = nvmlInit_v2();
                    if (rc != NvmlReturn.Success)
                    {
                        return false;
                    }

                    _initialized = 1;

                    AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                    {
                        try { nvmlShutdown(); } catch { /* ignore */ }
                    };

                    return true;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
            }
        }

        private const string NvmlDll = "nvml.dll";

        private enum NvmlReturn : int
        {
            Success = 0,
            ErrorUninitialized = 1,
            ErrorInvalidArgument = 2,
            ErrorNotSupported = 3,
            ErrorNoPermission = 4,
            ErrorAlreadyInitialized = 5,
            ErrorNotFound = 6,
            ErrorInsufficientSize = 7,
            ErrorUnknown = 999
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct nvmlUtilization_t
        {
            public uint gpu;
            public uint memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct nvmlDevice_t
        {
            public IntPtr Handle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct nvmlMemory_t
        {
            public ulong total;
            public ulong free;
            public ulong used;
        }

        [DllImport(NvmlDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern NvmlReturn nvmlInit_v2();

        [DllImport(NvmlDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern NvmlReturn nvmlShutdown();

        [DllImport(NvmlDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out nvmlDevice_t device);

        [DllImport(NvmlDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern NvmlReturn nvmlDeviceGetUtilizationRates(nvmlDevice_t device, out nvmlUtilization_t utilization);

        [DllImport(NvmlDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern NvmlReturn nvmlDeviceGetPowerUsage(nvmlDevice_t device, out uint powerMilliwatts);

        [DllImport(NvmlDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern NvmlReturn nvmlDeviceGetMemoryInfo(nvmlDevice_t device, out nvmlMemory_t memory);
    }
}
