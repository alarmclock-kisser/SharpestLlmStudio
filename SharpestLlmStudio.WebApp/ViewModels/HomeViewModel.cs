using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.Web;
using System.IO;
using System.Text;
using System.Diagnostics;
using SharpestLlmStudio.Shared;
using SharpestLlmStudio.Runtime;

namespace SharpestLlmStudio.WebApp.ViewModels
{
    public class HomeViewModel
    {
        // Component can set this to allow the ViewModel to request UI re-render
        public Action? NotifyStateChanged { get; set; }
        private readonly LlamaCppClient Client;
        private readonly IJSRuntime Js;
        private readonly WebAppSettings Settings;
        private CancellationTokenSource? generationCts;
        private ElementReference messageContainerRef;


        // API Data
        public ICollection<string> DirectMlDevices { get; set; } = [];
        public ICollection<LlamaModelInfo> LlamaModels { get; set; } = [];

        // State Data
        public string? SelectedDirectMlDevice { get; set; }
        public int DirectMlDeviceIndex => this.DirectMlDevices != null && this.SelectedDirectMlDevice != null ? this.DirectMlDevices.ToList().IndexOf(this.SelectedDirectMlDevice) -1 : -1;
        public string? SelectedModelName { get; set; } = null;
        public bool ForceUnload { get; set; } = true;
        public LlamaModelInfo? LoadedModel { get; set; } = null;
        public int ContextSize { get; set; } = 1024;
        public bool UseSystemPrompt { get; set; } = true;
        public bool IsolatedGeneration { get; set; } = false;
        public bool AutoSaveEnabled { get; set; } = true;

        public ICollection<string> ContextFiles { get; private set; } = [];
        public bool IsCurrentContextSaved { get; private set; } = false;


        public string ConversationLabelColor => this.IsCurrentContextSaved ? "green" : "orange";


        public string ModelLoadingTimeString { get; set; } = "No model loaded yet.";
        public bool IsLoaded { get; set; } = false;
        public bool IsBusy { get; set; } = false;
        public bool IsImagePathsExpanded { get; set; } = false;
        public List<string> SelectedImagePaths { get; private set; } = [];
        private readonly Dictionary<string, LoadedImageMetadata> loadedImageMetadata = new(StringComparer.OrdinalIgnoreCase);

        private bool asBytes;
        public bool AsBytes
        {
            get => this.asBytes;
            set
            {
                this.asBytes = value;
                this.RequestUiRefresh();
            }
        }

        private bool resizeEnabled;
        public bool ResizeEnabled
        {
            get => this.resizeEnabled;
            set
            {
                this.resizeEnabled = value;
                this.RequestUiRefresh();
            }
        }

        private int? maxDiagonalImageSize;
        public int? MaxDiagonalImageSize
        {
            get => this.maxDiagonalImageSize;
            set
            {
                this.maxDiagonalImageSize = value;
                this.RequestUiRefresh();
            }
        }

        private bool bitDepthEnabled;
        public bool BitDepthEnabled
        {
            get => this.bitDepthEnabled;
            set
            {
                this.bitDepthEnabled = value;
                this.RequestUiRefresh();
            }
        }

        private int? bitDepth;
        public int? BitDepth
        {
            get => this.bitDepth;
            set
            {
                this.bitDepth = value;
                this.RequestUiRefresh();
            }
        }

        private string imageFormat = "jpg";
        public string ImageFormat
        {
            get => this.imageFormat;
            set
            {
                this.imageFormat = NormalizeImageFormat(value);
                this.RequestUiRefresh();
            }
        }
        public IReadOnlyList<string> AvailableImageFormats { get; } = ["bmp", "png", "jpg"];

        // Generation UI state
        private string userInput = string.Empty;
        public string UserInput
        {
            get => this.userInput;
            set
            {
                this.userInput = value ?? string.Empty;
                try
                {
                    this.NotifyStateChanged?.Invoke();
                }
                catch
                {
                    // ignore notify errors
                }
            }
        }

        public GenerationStats? LastGenerationStats { get; set; } = null;
        public HardwareStatistics? LastHardwareStats { get; set; } = null;
        public string CpuManufacturerName => this.LastHardwareStats?.CpuStats.Manufacturer ?? "N/A";
        public string GpuManufacturerName => this.LastHardwareStats?.GpuStats.Manufacturer ?? "N/A";
        public string TotalGpuEnergyDisplay
        {
            get
            {
                double kwh = this.LastHardwareStats?.GpuStats.TotalKiloWattsUsed ?? 0.0;
                double wh = kwh * 1000.0;
                return $"{kwh:F6} kWh ({wh:F3} Wh)";
            }
        }
        public string CollapsedStatsSummary
        {
            get
            {
                var hw = this.LastHardwareStats;
                if (hw == null)
                {
                    return "CPU: -, RAM: -, GPU: -, VRAM: -, kWh: -";
                }

                return $"CPU: {hw.CpuStats.AverageLoadPercentage:F0}% | RAM: {hw.RamStats.MemoryUsagePercentage:F0}% | GPU: {hw.GpuStats.CoreLoadPercentage:F0}% | VRAM: {hw.GpuStats.VramStats.MemoryUsagePercentage:F0}% | kWh: {hw.GpuStats.TotalKiloWattsUsed:F6}";
            }
        }

        private readonly Queue<double> cpuUsageHistory = [];
        private readonly Queue<double> gpuUsageHistory = [];
        private const int SparklineHistoryMax = 60;

        public string SparklineCpuColor => this.CpuManufacturerName switch
        {
            string s when s.Contains("Intel", StringComparison.OrdinalIgnoreCase) => "#0071C5",
            string s when s.Contains("AMD", StringComparison.OrdinalIgnoreCase) => "#ED1C24",
            string s when s.Contains("Apple", StringComparison.OrdinalIgnoreCase) => "#7D7D7D",
            _ => "#111111"
        };
        public string SparklineGpuColor => this.GpuManufacturerName switch
        {
            string s when s.Contains("Intel", StringComparison.OrdinalIgnoreCase) => "#0071C5",
            string s when s.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => "#76B900",
            string s when s.Contains("AMD", StringComparison.OrdinalIgnoreCase) => "#ED1C24",
            string s when s.Contains("Apple", StringComparison.OrdinalIgnoreCase) => "#7D7D7D",
            _ => "#111111"
        };

        // Auto-refresh
        private System.Threading.Timer? autoRefreshTimer;
        private bool autoRefreshEnabled = true;
        public bool AutoRefreshEnabled
        {
            get => this.autoRefreshEnabled;
            set
            {
                this.autoRefreshEnabled = value;
                try { this.NotifyStateChanged?.Invoke(); } catch { }
                if (value)
                {
                    this.StartAutoRefresh();
                }
                else
                {
                    this.StopAutoRefresh();
                }
            }
        }

        private int autoRefreshIntervalMs = 1000;
        public int AutoRefreshIntervalMs
        {
            get => this.autoRefreshIntervalMs;
            set
            {
                this.autoRefreshIntervalMs = Math.Clamp(value, 100, 5000);
                try { this.NotifyStateChanged?.Invoke(); } catch { }
                if (this.AutoRefreshEnabled)
                {
                    this.StartAutoRefresh();
                }
            }
        }


        // New overload: start generation from provided prompt (used by UI component)
        
          
        public string GeneratedOutput { get; set; } = string.Empty;
        public bool IsGenerating { get; set; } = false;

        public bool CanSend => !this.IsGenerating && !string.IsNullOrWhiteSpace(this.UserInput);

        private bool isModelPanelExpanded = true;
        public bool IsModelPanelExpanded
        {
            get => this.isModelPanelExpanded;
            set
            {
                this.isModelPanelExpanded = value;
                try { this.NotifyStateChanged?.Invoke(); } catch { }
            }
        }

        private bool isStatsPanelExpanded = true;
        public bool IsStatsPanelExpanded
        {
            get => this.isStatsPanelExpanded;
            set
            {
                this.isStatsPanelExpanded = value;
                try { this.NotifyStateChanged?.Invoke(); } catch { }
            }
        }

        public float GenTemperature { get; set; } = 0.7f;
        public int GenMaxTokens { get; set; } = 512;
        public float GenTopP { get; set; } = 0.9f;



        public bool FirstRender { get; private set; } = true;


        public HomeViewModel(LlamaCppClient ApiClient, IJSRuntime js, WebAppSettings webAppSettings)
        {
            this.Client = ApiClient;
            this.Js = js;
            this.Settings = webAppSettings;

            // start auto-refresh with defaults
            if (this.AutoRefreshEnabled)
            {
                this.StartAutoRefresh();
            }
        }

        private void StartAutoRefresh()
        {
            try
            {
                this.autoRefreshTimer?.Dispose();
                this.autoRefreshTimer = new System.Threading.Timer(async _ =>
                {
                    try
                    {
                        await this.UpdateGenerationStatsAsync();
                        await this.UpdateHardwareStatsAsync();
                        try { this.NotifyStateChanged?.Invoke(); } catch { }
                    }
                    catch { }
                }, null, 0, Math.Max(200, this.AutoRefreshIntervalMs));
            }
            catch { }
        }

        private void StopAutoRefresh()
        {
            try
            {
                this.autoRefreshTimer?.Dispose();
                this.autoRefreshTimer = null;
            }
            catch { }
        }

        [JSInvokable]
        public async Task OnEnterPressed()
        {
            // Called from JS when Enter (without Shift) is pressed
            if (this.IsGenerating)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(this.UserInput))
            {
                return;
            }

            // await this.StartGenerationAsync();
        }


        public Task InitializeAsync()
        {
            return this.InitializeInternalAsync();
        }

        private async Task InitializeInternalAsync()
        {
            await this.RefreshAsync();
        }

        public async Task RefreshContextAsync()
        {
            try
            {
                // this.ContextFiles = await this.Api.GetContextFilesAsync();
            }
            catch
            {
                this.ContextFiles = [];
            }
        }

        public async Task ResetConversationAsync()
        {
            // await this.Client.ResetContextAsync(this.ContextSize);
            this.GeneratedOutput = string.Empty;
            await this.RefreshContextAsync();
            try { this.NotifyStateChanged?.Invoke(); } catch { }
        }

        public void SetMessageContainer(ElementReference container)
        {
            this.messageContainerRef = container;
        }

        public async Task ToggleAndBrowseImagePathsAsync()
        {
            this.IsImagePathsExpanded = !this.IsImagePathsExpanded;

            if (!this.IsImagePathsExpanded)
            {
                try { this.NotifyStateChanged?.Invoke(); } catch { }
                return;
            }

            await this.BrowseImagePathsAsync();
        }

        public async Task BrowseImagePathsAsync()
        {
            try
            {
                var picturesPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) ?? string.Empty;
                var selected = await this.Js.InvokeAsync<string[]?>("blazorHelpers.browseImagePaths", picturesPath, this.SelectedImagePaths);
                if (selected == null || selected.Length == 0)
                {
                    try { this.NotifyStateChanged?.Invoke(); } catch { }
                    return;
                }

                int added = 0;
                foreach (var path in selected)
                {
                    if (!string.IsNullOrWhiteSpace(path) && !this.SelectedImagePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        var normalizedPath = path.Trim();
                        this.SelectedImagePaths.Add(normalizedPath);
                        // await this.EnsureImageMetadataAsync(normalizedPath);
                        added++;
                    }
                }
            }
            catch
            {
            }

            try { this.NotifyStateChanged?.Invoke(); } catch { }
        }

        /*public async Task<ICollection<string>> UploadImagesAsync(IEnumerable<FileParameter> fileParameters, CancellationToken ct = default)
        {
            return await this.Client.UploadImagesAsync(fileParameters, ct);
        }*/

        public async Task AddUploadedImagePathsAsync(IEnumerable<string> uploadedPaths)
        {
            int added = 0;

            foreach (var path in uploadedPaths ?? [])
            {
                if (!string.IsNullOrWhiteSpace(path) && !this.SelectedImagePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    var normalizedPath = path.Trim();
                    this.SelectedImagePaths.Add(normalizedPath);
                    // await this.EnsureImageMetadataAsync(normalizedPath);
                    added++;
                }
            }

            if (added > 0)
            {
                this.IsImagePathsExpanded = true;
            }

            try { this.NotifyStateChanged?.Invoke(); } catch { }
        }

        public void RemoveImagePath(string path)
        {
            this.SelectedImagePaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            this.loadedImageMetadata.Remove(path);

            try { this.NotifyStateChanged?.Invoke(); } catch { }
        }

        /*public async Task StartGenerationAsync()
        {
            await this.StartGenerationFromPromptAsync(this.UserInput);
        }*/

        public void CancelGeneration()
        {
            try
            {
                this.generationCts?.Cancel();
            }
            catch { }
        }

        public async Task RefreshAsync()
        {
            this.LlamaModels = this.Client.Models;

            if (this.FirstRender)
            {
                // this.DirectMlDevices = await this.Client.GetDirectMlDevicesAsync();
                this.ContextSize = this.Settings.DefaultContextSize;

                this.SelectedModelName = this.LlamaModels.FirstOrDefault()?.Name;

                this.GenMaxTokens = this.Settings.DefaultMaxTokens;
                this.GenTemperature = (float) this.Settings.DefaultTemperature;


                this.FirstRender = false;
            }
        }

        private async Task<List<byte[]>> LoadSelectedImageBytesAsync(CancellationToken ct)
        {
            List<byte[]> bytes = [];
            foreach (var path in this.SelectedImagePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                try
                {
                    bytes.Add(await File.ReadAllBytesAsync(path, ct));
                }
                catch (Exception ex)
                {
                    await StaticLogger.LogAsync($"[HomeViewModel] Could not read image bytes for '{path}': {ex.Message}");
                }
            }

            return bytes;
        }

        public string GetImageDisplayLabel(string imagePath)
        {
            if (!TryGetImageDisplayInfo(imagePath, out var info))
            {
                return Path.GetFileName(imagePath);
            }

            return info.Label;
        }

        public string GetImageDisplayStyle(string imagePath)
        {
            if (!TryGetImageDisplayInfo(imagePath, out var info))
            {
                return "color:#1B5E20;";
            }

            var maxTokens = Math.Max(1, this.GenMaxTokens);
            double usage = info.EstimatedTokens / (double)maxTokens;
            if (usage > 1.0)
            {
                return "color:#8B0000;font-weight:700;text-decoration:line-through;";
            }

            string color = GetTokenUsageColor(usage);
            return $"color:{color};font-weight:600;";
        }

        private bool TryGetImageDisplayInfo(string imagePath, out ImageDisplayInfo info)
        {
            info = default;

            if (!this.loadedImageMetadata.TryGetValue(imagePath, out var metadata))
            {
                return false;
            }

            int width = metadata.Width;
            int height = metadata.Height;
            if (this.ResizeEnabled && this.MaxDiagonalImageSize is int maxDiagonal and > 0)
            {
                (width, height) = ResizeToMaxDiagonal(width, height, maxDiagonal);
            }

            int estimatedTokens = EstimateImageTokens(width, height, this.AsBytes, this.ImageFormat, this.BitDepthEnabled ? this.BitDepth : null);
            string sizeText = FormatSize(metadata.FileSizeBytes);
            info = new ImageDisplayInfo(
                $"{metadata.FileName} ({width}x{height} px., {sizeText}, ca. {estimatedTokens} tok.)",
                estimatedTokens);
            return true;
        }

        private static string GetTokenUsageColor(double usage)
        {
            usage = Math.Clamp(usage, 0.0, 1.0);

            return usage switch
            {
                <= 0.25 => InterpolateHexColor("#0B3D0B", "#1B5E20", usage / 0.25),
                <= 0.40 => InterpolateHexColor("#1B5E20", "#2E7D32", (usage - 0.25) / 0.15),
                <= 0.60 => InterpolateHexColor("#2E7D32", "#F9A825", (usage - 0.40) / 0.20),
                <= 0.80 => InterpolateHexColor("#F9A825", "#EF6C00", (usage - 0.60) / 0.20),
                _ => InterpolateHexColor("#EF6C00", "#C62828", (usage - 0.80) / 0.20)
            };
        }

        private static string InterpolateHexColor(string startHex, string endHex, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);

            var start = ParseHexColor(startHex);
            var end = ParseHexColor(endHex);

            int r = (int)Math.Round(start.R + (end.R - start.R) * t);
            int g = (int)Math.Round(start.G + (end.G - start.G) * t);
            int b = (int)Math.Round(start.B + (end.B - start.B) * t);

            return $"#{r:X2}{g:X2}{b:X2}";
        }

        private static (int R, int G, int B) ParseHexColor(string hex)
        {
            string c = hex.TrimStart('#');
            return (
                Convert.ToInt32(c.Substring(0, 2), 16),
                Convert.ToInt32(c.Substring(2, 2), 16),
                Convert.ToInt32(c.Substring(4, 2), 16)
            );
        }

        


        private static (int Width, int Height) ResizeToMaxDiagonal(int width, int height, int maxDiagonal)
        {
            int maxCurrent = Math.Max(width, height);
            if (maxCurrent <= 0 || maxCurrent <= maxDiagonal)
            {
                return (width, height);
            }

            double ratio = maxDiagonal / (double)maxCurrent;
            int newWidth = Math.Max(1, (int)Math.Round(width * ratio));
            int newHeight = Math.Max(1, (int)Math.Round(height * ratio));
            return (newWidth, newHeight);
        }

        private static int EstimateImageTokens(int width, int height, bool asBytes, string format, int? bitDepth)
        {
            int patch = 14;
            int baseTokens = Math.Max(1,
                (int)Math.Ceiling(width / (double)patch) *
                (int)Math.Ceiling(height / (double)patch));

            double factor = 1.0;
            if (bitDepth is int bd and > 0 and < 24)
            {
                factor += (24 - bd) / 24.0 * 0.06;
            }

            if (asBytes)
            {
                factor *= NormalizeImageFormat(format) switch
                {
                    "bmp" => 1.02,
                    "png" => 1.00,
                    _ => 0.98
                };
            }

            return Math.Max(1, (int)Math.Round(baseTokens * factor));
        }

        private static string FormatSize(long bytes)
        {
            double kb = bytes / 1024.0;
            if (kb < 1024.0)
            {
                return $"{kb:F1} KB";
            }

            return $"{kb / 1024.0:F1} MB";
        }

        private static string NormalizeImageFormat(string? format)
        {
            return format?.Trim().ToLowerInvariant() switch
            {
                "bmp" => "bmp",
                "png" => "png",
                _ => "jpg"
            };
        }

        private sealed class LoadedImageMetadata
        {
            public string FileName { get; init; } = string.Empty;
            public int Width { get; init; }
            public int Height { get; init; }
            public long FileSizeBytes { get; init; }
        }

        private readonly record struct ImageDisplayInfo(string Label, int EstimatedTokens);

        private void RequestUiRefresh()
        {
            try { this.NotifyStateChanged?.Invoke(); } catch { }
        }



        
        
        public async Task ToggleModelAsync()
        {
            if (this.IsBusy)
            {
                return;
            }

            this.IsBusy = true;
            try
            {
                if (this.IsLoaded)
                {
                    // Unload
                    
                }
                else
                {
                    // Load
                    LlamaModelInfo? modelToLoad = this.LlamaModels.FirstOrDefault(m => m.Name.Equals(this.SelectedModelName, StringComparison.OrdinalIgnoreCase));
                    if (modelToLoad == null)
                    {
                        throw new Exception("Selected model not found");
                    }

                    LlamaModelLoadRequest loadRequest = new()
                    {
                        ModelInfo = modelToLoad,
                        ServerExecutablePath = this.Settings.ServerExecutablePath,
                    };
                    Stopwatch sw = Stopwatch.StartNew();

                    LlamaModelLoadResult response = await this.Client.LoadModelAsync(loadRequest);

                    sw.Stop();
                    this.ModelLoadingTimeString = sw.Elapsed.TotalSeconds.ToString("F3") + " sec. elapsed loading.";
                    this.IsLoaded = response.Success;
                    this.LoadedModel = response.Success ? modelToLoad : null;

                    // Collapse model panel if load succeeded
                    if (this.IsLoaded)
                    {
                        this.IsModelPanelExpanded = false;
                    }
                }
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync("[Blazor] Error during model load/unload: " + ex.Message);
                await StaticLogger.LogAsync(ex);
            }

            this.IsBusy = false;
            await this.RefreshAsync();
        }


        public async Task UpdateGenerationStatsAsync()
        {
            try
            {
                // this.LastGenerationStats = await this.Client.LastGenerationStats;
            }
            catch
            {
                // ignore errors
            }
        }

        public async Task UpdateHardwareStatsAsync()
        {
            try
            {
                // this.LastHardwareStats = await this.Client.GetHardwareStatisticsAsync();

                if (this.LastHardwareStats?.CpuStats != null)
                {
                    this.AppendHistory(this.cpuUsageHistory, this.LastHardwareStats.CpuStats.AverageLoadPercentage);
                }

                if (this.LastHardwareStats?.GpuStats != null)
                {
                    this.AppendHistory(this.gpuUsageHistory, this.LastHardwareStats.GpuStats.CoreLoadPercentage);
                }
            }
            catch
            {
                // ignore errors
            }
        }

        public string GetCpuSparklineSvg(int width = 180, int height = 56)
        {
            return this.GetSparklineSvg(this.cpuUsageHistory, width, height, this.SparklineCpuColor, this.GetLighterColorGradient(this.SparklineCpuColor), this.CpuManufacturerName + " CPU");
        }

        public string GetGpuSparklineSvg(int width = 180, int height = 56)
        {
            return this.GetSparklineSvg(this.gpuUsageHistory, width, height, this.SparklineGpuColor, this.GetLighterColorGradient(this.SparklineGpuColor), this.GpuManufacturerName + " GPU");
        }

        private string GetSparklineSvg(IEnumerable<double> valuesInput, int width, int height, string lineColor, string fillColor, string label)
        {
            try
            {
                var valsRaw = valuesInput.Select(v => Math.Clamp(v, 0.0, 100.0)).ToList();
                if (valsRaw.Count == 0) return string.Empty;

                int n = valsRaw.Count;
                double pad = 6;
                double innerW = Math.Max(10, width - 2 * pad);
                double innerH = Math.Max(10, height - 2 * pad);

                double min = valsRaw.Min();
                double max = valsRaw.Max();
                // if flat line, create a small range around value to make variations visible
                if (Math.Abs(max - min) < 0.0001)
                {
                    min = Math.Max(0, min - 5);
                    max = Math.Min(100, max + 5);
                }

                var points = new StringBuilder();
                var area = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    double x = pad + (n == 1 ? innerW / 2.0 : i * (innerW / Math.Max(1, n - 1)));
                    double norm = (valsRaw[i] - min) / Math.Max(1e-6, (max - min));
                    double y = pad + (1.0 - norm) * innerH;
                    points.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0},{1} ", x, y);
                    area.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0},{1} ", x, y);
                }

                // build area polygon (from left-bottom, through points, to right-bottom)
                var areaPoints = new StringBuilder();
                areaPoints.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0},{1} ", pad, pad + innerH);
                areaPoints.Append(area.ToString());
                areaPoints.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0},{1}", pad + innerW, pad + innerH);

                string svg = $"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\">" +
                             $"<title>{label}</title>" +
                             $"<rect x=\"0\" y=\"0\" width=\"{width}\" height=\"{height}\" rx=\"4\" ry=\"4\" fill=\"transparent\" />" +
                             $"<line x1=\"{pad}\" y1=\"{pad + innerH}\" x2=\"{pad + innerW}\" y2=\"{pad + innerH}\" stroke=\"#d0d0d0\" stroke-width=\"1\" />" +
                             $"<polygon fill=\"{fillColor}\" points=\"{areaPoints.ToString().Trim()}\" />" +
                             $"<polyline fill=\"none\" stroke=\"{lineColor}\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\" points=\"{points.ToString().Trim()}\" />" +
                             $"</svg>";
                return svg;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void AppendHistory(Queue<double> history, double value)
        {
            history.Enqueue(Math.Clamp(value, 0.0, 100.0));
            while (history.Count > SparklineHistoryMax)
            {
                _ = history.Dequeue();
            }
        }

        private string GetLighterColorGradient(string baseColor, int amount = 92)
        {
            try
            {
                System.Drawing.Color color = System.Drawing.ColorTranslator.FromHtml(baseColor);
                System.Drawing.Color lighter = System.Drawing.Color.FromArgb(
                    Math.Min(255, color.A + amount),
                    Math.Min(255, color.R + amount),
                    Math.Min(255, color.G + amount),
                    Math.Min(255, color.B + amount)
                );
                return System.Drawing.ColorTranslator.ToHtml(lighter);
            }
            catch
            {
                return baseColor;
            }
        }


    }
}
