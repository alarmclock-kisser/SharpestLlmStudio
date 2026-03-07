using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.Web;
using System.IO;
using System.Text;
using System.Diagnostics;
using SharpestLlmStudio.Shared;
using SharpestLlmStudio.Runtime;
using System.Runtime.Versioning;
using System.Net;
using System.Text.RegularExpressions;

namespace SharpestLlmStudio.WebApp.ViewModels
{
    public class HomeViewModel : IDisposable
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
        public ICollection<LlamaModelInfo> LlamaModels { get; private set; } = [];

        // State Data
        public string? SelectedDirectMlDevice { get; set; }
        public int DirectMlDeviceIndex => this.DirectMlDevices != null && this.SelectedDirectMlDevice != null ? this.DirectMlDevices.ToList().IndexOf(this.SelectedDirectMlDevice) -1 : -1;
        public string? SelectedModelName { get; set; } = null;

        public static readonly IReadOnlyList<string> ModelSortOptions = ["A - Z", "Biggest", "Params", "Newest", "Vision"];

        private string modelSortMode = "A - Z";
        public string ModelSortMode
        {
            get => this.modelSortMode;
            set
            {
                this.modelSortMode = value ?? "A - Z";
                // keep index in sync
                var idx = ModelSortOptions.ToList().IndexOf(this.modelSortMode);
                this.ModelSortIndex = idx >= 0 ? idx : 0;
                this.ApplyModelSort();
                this.RequestUiRefresh();
            }
        }

        private int modelSortIndex = 0;
        public int ModelSortIndex
        {
            get => this.modelSortIndex;
            set
            {
                int idx = Math.Clamp(value, 0, ModelSortOptions.Count - 1);
                this.modelSortIndex = idx;
                this.modelSortMode = ModelSortOptions[idx];
                this.ApplyModelSort();
                // after sorting, select the top-most model automatically
                this.SelectedModelName = this.LlamaModels.FirstOrDefault()?.Name;
                this.RequestUiRefresh();
            }
        }

        public bool ForceUnload { get; set; } = true;
        public LlamaModelInfo? LoadedModel { get; set; } = null;
        public int ContextSize { get; set; } = 1024;
        public bool UseMmproj { get; set; } = true;
        public bool UseFlashAttention { get; set; } = true;
        public bool UseSystemPrompt { get; set; } = true;
        public bool IsolatedGeneration { get; set; } = false;
        public bool AutoSaveEnabled { get; set; } = true;
        // Use 0 to disable downsizing (send full-size images). Default 720.
        public int ImageMaxDimension { get; set; } = 720;

        private bool useJsonOutputFormat;
        public bool UseJsonOutputFormat
        {
            get => this.useJsonOutputFormat;
            set
            {
                if (value && !this.HasJsonOutputFormat)
                {
                    this.useJsonOutputFormat = false;
                    this.JsonOutputFormatWarning = "JSON output format is enabled, but no valid JSON format file is loaded.";
                    this.RequestUiRefresh();
                    return;
                }

                this.useJsonOutputFormat = value;
                this.RequestUiRefresh();
            }
        }

        public string JsonOutputFormatTemplate { get; private set; } = string.Empty;
        public string? JsonOutputFormatFileName { get; private set; }
        public string? JsonOutputFormatWarning { get; private set; }
        public bool HasJsonOutputFormat => !string.IsNullOrWhiteSpace(this.JsonOutputFormatTemplate);

        public ICollection<string> ContextFiles { get; private set; } = [];
        public bool IsCurrentContextSaved { get; private set; } = false;


        public string ConversationLabelColor => this.IsCurrentContextSaved ? "green" : "orange";


        public string ModelLoadingTimeString { get; set; } = "No model loaded yet.";
        public string? LastLoadError { get; set; } = null;
        public bool IsLoaded { get; set; } = false;
        public bool IsReusedInstance { get; set; } = false;
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
        public string SystemPrompt { get; set; } = string.Empty;

        public List<LlamaChatMessage> ChatMessages { get; private set; } = [];
        // Sum of estimated tokens for uploaded images (updated on upload)
        public int ImageEstimatedTokensTotal { get; private set; } = 0;

        public string ContextSaveName { get; set; } = "session";
        public string? SelectedContextFilePath { get; set; } = null;

        public string KnowledgeKey { get; set; } = string.Empty;
        public string KnowledgeContent { get; set; } = string.Empty;
        public string KnowledgeQuery { get; set; } = string.Empty;
        public int KnowledgeTopK { get; set; } = 3;
        public IReadOnlyList<LlamaKnowledgeSearchResult> KnowledgeResults { get; private set; } = [];
        public IReadOnlyList<LlamaKnowledgeEntry> KnowledgeEntries { get; private set; } = [];

        private readonly object lastActionMessageSync = new();
        private CancellationTokenSource? lastActionMessageCts;
        private string? lastActionMessage;
        public string? LastActionMessage
        {
            get => this.lastActionMessage;
            set
            {
                this.lastActionMessage = value;
                this.LastActionIsAllowedNonAdminCommand = false;
                this.ScheduleLastActionMessageAutoDismiss(value);
            }
        }
        public bool LastActionIsAllowedNonAdminCommand { get; private set; }
        public bool MonitoringEnabled => this.Settings.EnableMonitoring;
        public bool HasSavedContextBaseline => !string.IsNullOrWhiteSpace(this.SelectedContextFilePath);
        public bool IsVolatileContext => !this.HasSavedContextBaseline;

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

        private bool isContextPanelExpanded = false;
        public bool IsContextPanelExpanded
        {
            get => this.isContextPanelExpanded;
            set
            {
                this.isContextPanelExpanded = value;
                try { this.NotifyStateChanged?.Invoke(); } catch { }
            }
        }

        private bool isKnowledgePanelExpanded = false;
        public bool IsKnowledgePanelExpanded
        {
            get => this.isKnowledgePanelExpanded;
            set
            {
                this.isKnowledgePanelExpanded = value;
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
        private decimal genRepetitionPenalty = 1.1m;
        public decimal GenRepetitionPenalty
        {
            get => this.genRepetitionPenalty;
            set
            {
                this.genRepetitionPenalty = value;
                this.RequestUiRefresh();
            }
        }
        public float GenTopP { get; set; } = 0.9f;

        // Panel persistence constants
        public const string ModelExpandedStorageKey = "home.model.expanded";
        public const string ContextExpandedStorageKey = "home.context.expanded";
        public const string KnowledgeExpandedStorageKey = "home.knowledge.expanded";
        public const string GenSettingsExpandedStorageKey = "home.gensettings.expanded";
        public const string ChatOutputElementId = "chat-output";
        public const string ChatFooterElementId = "chat-footer";
        public const string TopPanelsContentElementId = "top-panels-content";
        public const string TopPanelsResizeHandleElementId = "top-panels-resize-handle";

        // UI state tracking (moved from Razor @code)
        private bool? _lastLoadedState;
        private int _lastChatMessageCount;
        private bool _panelStateLoaded;
        public bool ImageAttachmentsExpanded { get; set; } = true;
        public bool GenSettingsExpanded { get; set; } = false;
        public bool AutoScrollEnabled { get; set; } = true;
        public bool EnableCommandAgentMode { get; set; } = true;
        public bool EnableWebSearchAgentMode { get; set; } = true;
        public bool AutoAllowWebSearch { get; set; } = true;
        public bool AutoContinueAgentActions { get; set; } = false;
        public bool AllowAllNonAdminCommands { get; set; } = false;
        public bool AgentShowCommandWindow { get; set; }

        public LlamaCommandRequest? PendingCommandRequest { get; private set; }
        public LlamaWebSearchRequest? PendingWebSearchRequest { get; private set; }
        public LlamaCommandSafetyAssessment? PendingCommandSafety { get; private set; }

        public bool HasPendingCommandRequest => this.PendingCommandRequest != null;
        public bool HasPendingWebSearchRequest => this.PendingWebSearchRequest != null;

        // Computed properties (moved from Razor @code)
        public LlamaModelInfo? SelectedModelInfo => this.LlamaModels.FirstOrDefault(m => m.Name == this.SelectedModelName);
        public bool HasMmproj => this.SelectedModelInfo?.MmprojFilePath != null;
        public bool IsSelectedOmni => this.SelectedModelInfo?.IsOmni == true;

        public string MmprojLabel => this.IsSelectedOmni
            ? "Model is Any-to-Any (Omni)"
            : this.HasMmproj
                ? "Load multimodal projection (mmproj)"
                : "Multimodal projection \u2013 not available for this model";

        public string GenerationStatsContingent
        {
            get
            {
                var stats = this.LastGenerationStats;
                if (stats == null)
                {
                    return "";
                }

                string total = stats.TotalGenerationTime.HasValue ? $"{stats.TotalGenerationTime.Value.TotalSeconds:F1}s" : "-";
                string ttft = stats.TimeTilFirstToken > 0 ? $"{stats.TimeTilFirstToken:F3}s" : "-";
                return $"[{stats.TotalContextTokens} of {stats.ContextSize} tokens used]";
            }
        }

        public string GenerationStatsLast
        {
            get
            {
                var stats = this.LastGenerationStats;
                if (stats == null)
                {
                    return "";
                }

                string total = stats.TotalGenerationTime.HasValue ? $"{stats.TotalGenerationTime.Value.TotalSeconds:F1}s" : "-";
                string ttft = stats.TimeTilFirstToken > 0 ? $"{stats.TimeTilFirstToken:F3}s" : "-";
                return $"(tok: {stats.TotalTokensGenerated} | tok/s: {stats.TokensPerSecond:F3} | TTFT: {ttft} | total: {total})";
            }
        }

        public bool FirstRender { get; private set; } = true;
        private bool selectDefaultModelAfterReusedUnload;


        public HomeViewModel(LlamaCppClient ApiClient, IJSRuntime js, WebAppSettings webAppSettings)
        {
            this.Client = ApiClient;
            this.Js = js;
            this.Settings = webAppSettings;
            this.AgentShowCommandWindow = this.Settings.AgentShowCommandWindow;
            this.AutoContinueAgentActions = this.Settings.AgentAutoContinue;
            this.AllowAllNonAdminCommands = this.Settings.AllowAllNonAdminCommands;
            this.AutoAllowWebSearch = this.Settings.AutoAllowWebSearch;
            this.SystemPrompt = this.BuildDefaultSystemPromptFromSettings();
            // Initialize image preferences from settings defaults
            this.ImageMaxDimension = Math.Max(0, this.Settings.DefaultImageMaxDimension);
            this.ImageFormat = string.IsNullOrWhiteSpace(this.Settings.DefaultImageFormat) ? "jpg" : NormalizeImageFormat(this.Settings.DefaultImageFormat);
        }

        // Handle UI interaction for ImageMaxDimension numeric control.
        // Steps of 16. If value snaps below 448, treat as 0 (disabled). When at 0 and user increments, go to 448.
        public void OnImageMaxDimensionChanged(int newValue)
        {
            // If coming from 0 and user increments to positive small value, set to 448
            if (this.ImageMaxDimension == 0 && newValue > 0 && newValue < 448)
            {
                this.ImageMaxDimension = 448;
                this.RequestUiRefresh();
                return;
            }

            // Snap to 0 if below 448
            if (newValue > 0 && newValue < 448)
            {
                this.ImageMaxDimension = 0;
                this.RequestUiRefresh();
                return;
            }

            // Otherwise keep value rounded to nearest multiple of 16
            int rounded = Math.Clamp((int)Math.Round(newValue / 16.0) * 16, 0, 8192);
            this.ImageMaxDimension = rounded;
            this.RequestUiRefresh();
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
                }, null, 0, Math.Max(100, this.AutoRefreshIntervalMs));
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
        [SupportedOSPlatform("windows")]
        public async Task OnEnterPressed()
        {
            if (this.IsGenerating)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(this.UserInput))
            {
                return;
            }

            await this.StartGenerationAsync();
        }

        [JSInvokable]
        [SupportedOSPlatform("windows")]
        public async Task OnClipboardImagePasted(string dataUrl, string contentType)
        {
            if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                int commaIndex = dataUrl.IndexOf(',');
                if (commaIndex < 0 || commaIndex >= dataUrl.Length - 1)
                {
                    return;
                }

                string base64 = dataUrl[(commaIndex + 1)..];
                byte[] bytes = Convert.FromBase64String(base64);

                string extension = contentType?.ToLowerInvariant() switch
                {
                    "image/png" => ".png",
                    "image/bmp" => ".bmp",
                    "image/tiff" => ".tiff",
                    "image/tif" => ".tif",
                    _ => ".jpg"
                };

                string tempDir = Path.Combine(Path.GetTempPath(), "SharpestLlmStudio", "clipboard");
                Directory.CreateDirectory(tempDir);
                string fileName = $"clip_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}{extension}";
                string tempPath = Path.Combine(tempDir, fileName);
                await File.WriteAllBytesAsync(tempPath, bytes);

                int width = 0;
                int height = 0;
                try
                {
                    var dimensions = await this.Js.InvokeAsync<int[]>("sharpestNavMenu.getImageDimensionsFromDataUrl", dataUrl);
                    if (dimensions is { Length: >= 2 })
                    {
                        width = Math.Max(0, dimensions[0]);
                        height = Math.Max(0, dimensions[1]);
                    }
                }
                catch
                {
                }

                if (!this.SelectedImagePaths.Contains(tempPath, StringComparer.OrdinalIgnoreCase))
                {
                    this.SelectedImagePaths.Add(tempPath);
                    this.loadedImageMetadata[tempPath] = new LoadedImageMetadata
                    {
                        FileName = fileName,
                        Width = width,
                        Height = height,
                        FileSizeBytes = bytes.Length
                    };

                    try
                    {
                        int estimatedTokens = EstimateImageTokens(width, height, this.AsBytes, this.ImageFormat, this.BitDepthEnabled ? this.BitDepth : null);
                        this.ImageEstimatedTokensTotal += estimatedTokens;
                        int convTokens = CountRoughTokens(string.Join(" ", this.Client.GetConversationSnapshot().Select(m => m.Content)));
                        int totalEstimated = Math.Min(this.ContextSize, convTokens + this.ImageEstimatedTokensTotal);
                        await StaticLogger.LogAsync($"[HomeViewModel] Clipboard image '{fileName}' estimated ~{estimatedTokens} tokens (width={width},height={height},format={this.ImageFormat},bytes={bytes.Length})");
                        await StaticLogger.LogAsync($"[HomeViewModel] Estimated context usage after clipboard paste: {totalEstimated} / {this.ContextSize} tokens (conversation {convTokens} + images {this.ImageEstimatedTokensTotal})");
                    }
                    catch
                    {
                    }
                }

                this.IsImagePathsExpanded = this.SelectedImagePaths.Count > 0;
                this.LastActionMessage = $"Clipboard image attached: {fileName}";
                this.RequestUiRefresh();
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[HomeViewModel] Failed to process clipboard image");
            }
        }


        public Task InitializeAsync()
        {
            return this.InitializeInternalAsync();
        }

        private async Task InitializeInternalAsync()
        {
            await this.RefreshAsync();
            await this.RefreshContextAsync();
            this.SyncChatMessagesFromClient();

            if (!this.Settings.KillExistingServerInstances)
            {
                try
                {
                    var attachResult = await this.Client.TryAttachToRunningServerAsync(
                        contextSize: this.ContextSize,
                        batchSize: Math.Max(1, this.Settings.DefaultBatchSize));
                    if (attachResult?.Success == true)
                    {
                        this.IsLoaded = true;
                        this.LoadedModel = this.ResolveModelFromServerId(attachResult.ActiveModelId) ?? this.LlamaModels.FirstOrDefault(m => m.Name.Equals(this.SelectedModelName, StringComparison.OrdinalIgnoreCase));
                        this.IsReusedInstance = true;
                        this.IsModelPanelExpanded = true;
                        if (this.LoadedModel != null)
                        {
                            this.SelectedModelName = this.LoadedModel.Name;
                        }

                        this.ModelLoadingTimeString = "Attached to existing llama-server instance.";
                        this.LastActionMessage = "Existing llama-server instance detected and reused.";
                        this.ScheduleModelPanelAutoCollapse();
                    }
                }
                catch
                {
                }
            }

            if (this.AutoRefreshEnabled)
            {
                this.StartAutoRefresh();
            }
        }

        public async Task RefreshContextAsync()
        {
            try
            {
                var contextFiles = await this.Client.GetSavedContextFilesAsync();
                this.ContextFiles = contextFiles.ToList();
                if (!string.IsNullOrWhiteSpace(this.SelectedContextFilePath) && !this.ContextFiles.Contains(this.SelectedContextFilePath))
                {
                    this.SelectedContextFilePath = null;
                }

                // Intentionally do NOT auto-select the first saved context.
                // App should start in a fresh/volatile context unless user explicitly selects one.
                this.IsCurrentContextSaved = !string.IsNullOrWhiteSpace(this.SelectedContextFilePath);
            }
            catch
            {
                this.ContextFiles = [];
                this.IsCurrentContextSaved = false;
            }
        }

        public async Task ResetConversationAsync()
        {
            this.Client.ResetConversation();

            bool serverContextCleared = await this.Client.ClearServerContextAsync();
            if (!serverContextCleared && this.IsLoaded)
            {
                await StaticLogger.LogAsync("[HomeViewModel] Reset requested, but server context erase failed.");
                this.LastActionMessage = "Conversation reset locally, but server context erase failed.";
            }
            else
            {
                this.LastActionMessage = "Conversation and server context reset.";
            }

            this.GeneratedOutput = string.Empty;
            this.ChatMessages = [];
            // When resetting the conversation, clear any saved-context selection and
            // the save-name so autosave does not unintentionally overwrite an existing file.
            this.IsCurrentContextSaved = false;
            this.SelectedContextFilePath = null;
            this.ContextSaveName = string.Empty;
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

        public async Task AddImageUploadsAsync(IEnumerable<IBrowserFile> files, CancellationToken cancellationToken = default)
        {
            if (files == null)
            {
                return;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var stream = file.OpenReadStream(100 * 1024 * 1024, cancellationToken);
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, cancellationToken);

                    string contentType = string.IsNullOrWhiteSpace(file.ContentType) ? GuessMimeTypeByExtension(file.Name) : file.ContentType;
                    string dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(ms.ToArray())}";
                    if (!this.SelectedImagePaths.Contains(dataUrl, StringComparer.Ordinal))
                    {
                        int width = 0;
                        int height = 0;
                        bool isTiff = contentType.Contains("tiff", StringComparison.OrdinalIgnoreCase) || contentType.Contains("tif", StringComparison.OrdinalIgnoreCase);
                        if (!isTiff)
                        {
                            try
                            {
                                var dimensions = await this.Js.InvokeAsync<int[]>("sharpestNavMenu.getImageDimensionsFromDataUrl", dataUrl);
                                if (dimensions is { Length: >= 2 })
                                {
                                    width = Math.Max(0, dimensions[0]);
                                    height = Math.Max(0, dimensions[1]);
                                }
                            }
                            catch
                            {
                            }
                        }

                        this.SelectedImagePaths.Add(dataUrl);
                        this.loadedImageMetadata[dataUrl] = new LoadedImageMetadata
                        {
                            FileName = file.Name,
                            Width = width,
                            Height = height,
                            FileSizeBytes = (long) file.Size
                        };

                        // Estimate tokens for this image and update total
                        try
                        {
                            int estimatedTokens = EstimateImageTokens(width, height, this.AsBytes, this.ImageFormat, this.BitDepthEnabled ? this.BitDepth : null);
                            this.ImageEstimatedTokensTotal += estimatedTokens;
                            await StaticLogger.LogAsync($"[HomeViewModel] Uploaded image '{file.Name}' estimated ~{estimatedTokens} tokens (width={width},height={height},format={this.ImageFormat},bytes={file.Size})");

                            // Log approximate context usage (conversation + images)
                            int convTokens = CountRoughTokens(string.Join(" ", this.Client.GetConversationSnapshot().Select(m => m.Content)));
                            int totalEstimated = Math.Min(this.ContextSize, convTokens + this.ImageEstimatedTokensTotal);
                            await StaticLogger.LogAsync($"[HomeViewModel] Estimated context usage after upload: {totalEstimated} / {this.ContextSize} tokens (conversation {convTokens} + images {this.ImageEstimatedTokensTotal})");
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    await StaticLogger.LogAsync($"[HomeViewModel] Could not load uploaded image '{file.Name}': {ex.Message}");
                }
            }

            this.IsImagePathsExpanded = this.SelectedImagePaths.Count > 0;
            this.RequestUiRefresh();
        }

        public void ClearJsonOutputFormat()
        {
            this.JsonOutputFormatTemplate = string.Empty;
            this.JsonOutputFormatFileName = null;
            this.JsonOutputFormatWarning = null;
            this.UseJsonOutputFormat = false;
            this.LastActionMessage = "JSON output format removed.";
            _ = StaticLogger.LogAsync("[HomeViewModel] JSON output format removed by user.");
            this.RequestUiRefresh();
        }

        public async Task LoadJsonOutputFormatAsync(IBrowserFile file, CancellationToken cancellationToken = default)
        {
            if (file == null)
            {
                return;
            }

            try
            {
                using var stream = file.OpenReadStream(5 * 1024 * 1024, cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string content = await reader.ReadToEndAsync(cancellationToken);

                this.JsonOutputFormatFileName = file.Name;

                if (!StaticLogics.TryFormatJson(content, out string formattedJson))
                {
                    this.JsonOutputFormatTemplate = string.Empty;
                    this.UseJsonOutputFormat = false;
                    this.JsonOutputFormatWarning = $"Invalid JSON format in '{file.Name}'. JSON output format was not enabled.";
                    this.LastActionMessage = this.JsonOutputFormatWarning;
                    await StaticLogger.LogAsync($"[HomeViewModel] Invalid JSON output format file '{file.Name}'. Validation failed.");
                    this.RequestUiRefresh();
                    return;
                }

                this.JsonOutputFormatTemplate = formattedJson;
                this.UseJsonOutputFormat = true;
                this.JsonOutputFormatWarning = null;
                this.LastActionMessage = $"JSON output format loaded: {file.Name}";

                await StaticLogger.LogAsync($"[HomeViewModel] JSON output format loaded and validated from '{file.Name}'.");
            }
            catch (Exception ex)
            {
                this.JsonOutputFormatTemplate = string.Empty;
                this.UseJsonOutputFormat = false;
                this.JsonOutputFormatWarning = $"Failed to read JSON format file '{file.Name}': {ex.Message}";
                this.LastActionMessage = this.JsonOutputFormatWarning;
                await StaticLogger.LogAsync(ex, "[HomeViewModel] Could not load JSON output format file");
            }

            this.RequestUiRefresh();
        }

        [SupportedOSPlatform("windows")]
        public async Task StartGenerationAsync()
        {
            if (this.IsGenerating || !this.IsLoaded || string.IsNullOrWhiteSpace(this.UserInput))
            {
                return;
            }

            this.generationCts?.Cancel();
            this.generationCts?.Dispose();
            this.generationCts = new CancellationTokenSource();

            string prompt = this.UserInput.Trim();
            string promptForGeneration = prompt;
            string assistantText = string.Empty;

            this.IsGenerating = true;
            this.GeneratedOutput = string.Empty;
            this.LastLoadError = null;

            if (this.UseJsonOutputFormat)
            {
                this.JsonOutputFormatWarning = null;
            }

            var generationStats = new GenerationStats
            {
                GenerationStarted = DateTime.UtcNow,
                TotalContextTokens = 0,
                ContextSize = this.ContextSize
            };
            this.LastGenerationStats = generationStats;

            if (!this.IsolatedGeneration)
            {
                this.ChatMessages.Add(new LlamaChatMessage { Role = "user", Content = prompt, CreatedAtUtc = DateTime.UtcNow });
            }

            var assistantMessage = new LlamaChatMessage { Role = "assistant", Content = string.Empty, CreatedAtUtc = DateTime.UtcNow };
            this.ChatMessages.Add(assistantMessage);
            this.RequestUiRefresh();

            try
            {
                // Automatically augment prompt with knowledge context when available
                if (this.KnowledgeEntries.Count > 0)
                {
                    try
                    {
                        promptForGeneration = await this.Client.BuildKnowledgeAugmentedPromptAsync(prompt, this.KnowledgeTopK, this.ContextSize, this.GenMaxTokens, this.generationCts.Token);
                    }
                    catch (Exception ex)
                    {
                        await StaticLogger.LogAsync(ex, "[HomeViewModel] Could not augment prompt with knowledge context");
                        promptForGeneration = prompt;
                    }
                }

                LlamaGenerationRequest request = new()
                {
                    Prompt = promptForGeneration,
                    Images = this.SelectedImagePaths.ToArray(),
                    Isolated = this.IsolatedGeneration,
                    PersistConversation = !this.IsolatedGeneration,
                    IncludeConversationHistory = !this.IsolatedGeneration,
                    MaxTokens = this.GenMaxTokens,
                    Temperature = this.GenTemperature,
                    RepetitionPenalty = (double)this.GenRepetitionPenalty,
                    TopP = this.GenTopP,
                    // Pass image prefs from UI into the generation request
                    MaxWidthAndHeight = this.ImageMaxDimension,
                    ImageFormat = this.ImageFormat,
                    Stream = true,
                    SystemPrompt = this.BuildEffectiveSystemPrompt()
                };

                // Clear image attachments immediately after capturing them for the request
                // so they are not re-sent on subsequent prompts
                this.SelectedImagePaths.Clear();
                this.loadedImageMetadata.Clear();
                this.ImageEstimatedTokensTotal = 0;
                this.RequestUiRefresh();

                await foreach (var chunk in this.Client.GenerateAsync(request, this.generationCts.Token))
                {
                    assistantText += chunk;
                    this.GeneratedOutput = assistantText;
                    assistantMessage.Content = assistantText;
                    this.LastGenerationStats = this.Client.GetLastGenerationStatsSnapshot();
                    this.RequestUiRefresh();
                }

                this.UserInput = string.Empty;
                this.LastGenerationStats = this.Client.GetLastGenerationStatsSnapshot();

                if (this.AutoSaveEnabled && !this.IsolatedGeneration && this.HasSavedContextBaseline)
                {
                    string saveName = NormalizeContextSaveName(Path.GetFileNameWithoutExtension(this.SelectedContextFilePath) ?? this.ContextSaveName);
                    var saveResult = await this.Client.SaveContextAsync(saveName);
                    this.IsCurrentContextSaved = saveResult.Success;
                    if (saveResult.Success)
                    {
                        this.SelectedContextFilePath = saveResult.FilePath;
                        await this.RefreshContextAsync();
                    }
                }
                else if (!this.IsolatedGeneration)
                {
                    this.IsCurrentContextSaved = false;
                }

                bool expectedJsonOutput = this.UseJsonOutputFormat && this.HasJsonOutputFormat;
                bool receivedValidJsonOutput = StaticLogics.TryFormatJson(assistantText, out _);

                if (expectedJsonOutput && !receivedValidJsonOutput)
                {
                    this.JsonOutputFormatWarning = "Expected strict JSON output, but model response is not valid JSON.";
                    this.LastActionMessage = "Generation finished (warning: invalid JSON response).";
                    await StaticLogger.LogAsync("[HomeViewModel] JSON output mode enabled, but response validation failed.");
                }
                else
                {
                    this.LastActionMessage = "Generation finished.";
                }

                this.DetectPendingAgentActions(assistantText);

                // Sync UI chat messages from client — ring buffer may have trimmed oldest messages
                // Keep isolated output visible in UI; do not overwrite it from persistent history.
                if (!this.IsolatedGeneration)
                {
                    this.SyncChatMessagesFromClient();
                }
            }
            catch (OperationCanceledException)
            {
                assistantMessage.Content = string.IsNullOrWhiteSpace(assistantText) ? "[Generation canceled]" : assistantText;
                this.LastGenerationStats = this.Client.GetLastGenerationStatsSnapshot();
                this.LastActionMessage = "Generation canceled.";
                if (!this.IsolatedGeneration)
                {
                    this.SyncChatMessagesFromClient();
                }
            }
            catch (Exception ex)
            {
                this.LastLoadError = ex.Message;
                assistantMessage.Content = string.IsNullOrWhiteSpace(assistantText) ? $"[Error] {ex.Message}" : assistantText;
                this.LastGenerationStats = this.Client.GetLastGenerationStatsSnapshot();
                await StaticLogger.LogAsync(ex, "[HomeViewModel] Error while generating response");
                if (!this.IsolatedGeneration)
                {
                    this.SyncChatMessagesFromClient();
                }
            }
            finally
            {
                this.IsGenerating = false;
                await this.TryAutoExecuteAllowedNonAdminCommandAsync();
                await this.TryAutoExecuteWebSearchAsync();
                this.RequestUiRefresh();
            }
        }

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
            this.LlamaModels = this.Client.Models.ToList();
            this.ApplyModelSort();

            if (!this.IsLoaded && this.selectDefaultModelAfterReusedUnload)
            {
                this.SelectDefaultModelFromSettings();
                this.selectDefaultModelAfterReusedUnload = false;
            }

            if (this.FirstRender)
            {
                // this.DirectMlDevices = await this.Client.GetDirectMlDevicesAsync();
                this.ContextSize = this.Settings.DefaultContextSize;

                this.SelectedModelName = this.LlamaModels.FirstOrDefault(m => m.Name.Equals(this.Settings.DefaultModel, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? this.LlamaModels.FirstOrDefault(m => m.Name.Contains(this.Settings.DefaultModel, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? this.LlamaModels.FirstOrDefault()?.Name;

                this.GenMaxTokens = this.Settings.DefaultMaxTokens;
                this.GenTemperature = (float) this.Settings.DefaultTemperature;
                this.GenRepetitionPenalty = (decimal)this.Settings.DefaultRepetitionPenalty;


                this.FirstRender = false;
            }
        }

        private void ApplyModelSort()
        {
            string? previousSelected = this.SelectedModelName;

            this.LlamaModels = this.ModelSortMode switch
            {
                "Biggest" => this.LlamaModels.OrderByDescending(m => m.SizeInMb).ToList(),
                "Params" => this.LlamaModels.OrderByDescending(m => m.ParametersB ?? 0).ToList(),
                "Newest" => this.LlamaModels.OrderByDescending(m => m.LastModified).ToList(),
                "Vision" => this.LlamaModels.OrderByDescending(m => m.IsOmni).ThenByDescending(m => File.Exists(m.MmprojFilePath)).ThenByDescending(m => m.ParametersB ?? 0).ToList(),
                _ => this.LlamaModels.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList()
            };

            // Keep loaded/reused model selected when dropdown is disabled
            if (this.IsLoaded && this.LoadedModel != null && this.LlamaModels.Any(m => m.Name.Equals(this.LoadedModel.Name, StringComparison.OrdinalIgnoreCase)))
            {
                this.SelectedModelName = this.LoadedModel.Name;
                return;
            }

            // Preserve previous selection if still present
            if (!string.IsNullOrWhiteSpace(previousSelected) && this.LlamaModels.Any(m => m.Name.Equals(previousSelected, StringComparison.OrdinalIgnoreCase)))
            {
                this.SelectedModelName = previousSelected;
                return;
            }

            // Fallback to first model if nothing is selected
            if (this.LlamaModels.FirstOrDefault() is LlamaModelInfo first)
            {
                this.SelectedModelName = first.Name;
            }
        }

        private void SelectDefaultModelFromSettings()
        {
            this.SelectedModelName = this.LlamaModels.FirstOrDefault(m => m.Name.Equals(this.Settings.DefaultModel, StringComparison.OrdinalIgnoreCase))?.Name
                ?? this.LlamaModels.FirstOrDefault(m => m.Name.Contains(this.Settings.DefaultModel, StringComparison.OrdinalIgnoreCase))?.Name
                ?? this.LlamaModels.FirstOrDefault()?.Name;
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
            if (this.loadedImageMetadata.TryGetValue(imagePath, out var meta))
            {
                string sizeText = FormatSize(meta.FileSizeBytes);
                return meta.Width > 0 && meta.Height > 0
                    ? $"{meta.FileName} [{meta.Width}x{meta.Height}] ({sizeText})"
                    : $"{meta.FileName} ({sizeText})";
            }

            if (imagePath.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return "uploaded-image";
            }

            return Path.GetFileName(imagePath);
        }

        public string GetImageDisplayStyle(string imagePath)
        {
            if (!this.TryGetImageDisplayInfo(imagePath, out var info))
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

        private static string GuessMimeTypeByExtension(string fileName)
        {
            return Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".tif" or ".tiff" => "image/tiff",
                _ => "image/jpeg"
            };
        }

        private static string NormalizeContextSaveName(string input)
        {
            string name = input?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                return "session";
            }

            // Prevent growth like "Name.chat.chat.chat" during autosave cycles.
            while (name.EndsWith(".chat", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^5];
            }

            return string.IsNullOrWhiteSpace(name) ? "session" : name;
        }

        public async Task SaveContextAsync()
        {
            var result = await this.Client.SaveContextAsync(this.ContextSaveName);
            if (result.Success)
            {
                this.SelectedContextFilePath = result.FilePath;
                this.IsCurrentContextSaved = true;
            }

            this.LastActionMessage = result.Success
                ? $"Context saved: {Path.GetFileName(result.FilePath)}"
                : $"Context save failed: {result.ErrorMessage}";

            await this.RefreshContextAsync();
            this.RequestUiRefresh();
        }

        public async Task LoadSelectedContextAsync()
        {
            if (string.IsNullOrWhiteSpace(this.SelectedContextFilePath))
            {
                return;
            }

            bool success = await this.Client.LoadContextAsync(this.SelectedContextFilePath);
            this.LastActionMessage = success
                ? $"Context loaded: {Path.GetFileName(this.SelectedContextFilePath)}"
                : $"Context load failed: {Path.GetFileName(this.SelectedContextFilePath)}";

            this.IsCurrentContextSaved = success;

            this.SyncChatMessagesFromClient();
            this.RequestUiRefresh();
        }

        public async Task DeleteSelectedContextAsync()
        {
            if (string.IsNullOrWhiteSpace(this.SelectedContextFilePath))
            {
                return;
            }

            bool success = await this.Client.DeleteContextAsync(this.SelectedContextFilePath);
            this.LastActionMessage = success
                ? $"Context deleted: {Path.GetFileName(this.SelectedContextFilePath)}"
                : $"Context delete failed: {Path.GetFileName(this.SelectedContextFilePath)}";

            if (success)
            {
                this.SelectedContextFilePath = null;
                this.IsCurrentContextSaved = false;
            }

            await this.RefreshContextAsync();
            this.RequestUiRefresh();
        }

        public async Task AddKnowledgeAsync()
        {
            if (string.IsNullOrWhiteSpace(this.KnowledgeKey) || string.IsNullOrWhiteSpace(this.KnowledgeContent))
            {
                return;
            }

            _ = await this.Client.UpsertKnowledgeAsync(this.KnowledgeKey.Trim(), this.KnowledgeContent.Trim());
            this.LastActionMessage = $"Knowledge upserted: {this.KnowledgeKey.Trim()}";
            this.KnowledgeKey = string.Empty;
            this.KnowledgeContent = string.Empty;

            // Refresh local snapshot for UI
            try { this.KnowledgeEntries = this.Client.GetKnowledgeEntriesSnapshot(); } catch { this.KnowledgeEntries = []; }
            this.RequestUiRefresh();
        }

        public async Task AddKnowledgeFromFilesAsync(IEnumerable<IBrowserFile> files, CancellationToken cancellationToken = default)
        {
            if (files == null)
            {
                return;
            }

            int added = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var stream = file.OpenReadStream(50 * 1024 * 1024, cancellationToken);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    string content = await reader.ReadToEndAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    string key = Path.GetFileName(file.Name);
                    await this.Client.UpsertKnowledgeAsync(key, content, file.Name, cancellationToken);
                    added++;
                }
                catch (Exception ex)
                {
                    await StaticLogger.LogAsync($"[HomeViewModel] Could not import knowledge file '{file.Name}': {ex.Message}");
                }
            }

            this.LastActionMessage = added > 0
                ? $"Imported {added} knowledge file(s)."
                : "No knowledge files were imported.";

            // Refresh local snapshot for UI
            try { this.KnowledgeEntries = this.Client.GetKnowledgeEntriesSnapshot(); } catch { this.KnowledgeEntries = []; }
            this.RequestUiRefresh();
        }

        public async Task SearchKnowledgeAsync()
        {
            if (string.IsNullOrWhiteSpace(this.KnowledgeQuery))
            {
                this.KnowledgeResults = [];
                this.RequestUiRefresh();
                return;
            }

            try
            {
                this.KnowledgeResults = await this.Client.SearchKnowledgeAsync(this.KnowledgeQuery.Trim(), this.KnowledgeTopK);
                this.LastActionMessage = this.KnowledgeResults.Count == 0
                    ? "No matching knowledge entries found."
                    : $"Found {this.KnowledgeResults.Count} matching knowledge entries.";
            }
            catch (Exception ex)
            {
                this.KnowledgeResults = [];
                this.LastActionMessage = "Knowledge search failed.";
                await StaticLogger.LogAsync(ex, "[HomeViewModel] Error while searching knowledge");
            }

            this.RequestUiRefresh();
        }

        public async Task SaveKnowledgeStoreAsync()
        {
            string filePath = await this.Client.SaveKnowledgeStoreAsync();
            this.LastActionMessage = $"Knowledge store saved: {Path.GetFileName(filePath)}";
            this.RequestUiRefresh();
        }

        public void ClearKnowledgeStore()
        {
            this.Client.ClearKnowledgeStore();
            this.KnowledgeResults = [];
            this.LastActionMessage = "Knowledge store cleared.";
            this.RequestUiRefresh();
        }

        public async Task DeleteKnowledgeByKeyAsync(string baseKey)
        {
            if (string.IsNullOrWhiteSpace(baseKey))
            {
                return;
            }

            try
            {
                var snapshot = this.Client.GetKnowledgeEntriesSnapshot().ToList();
                var remaining = snapshot.Where(k =>
                    {
                        var idx = k.Key.IndexOf(" [chunk ", StringComparison.OrdinalIgnoreCase);
                        var bk = idx >= 0 ? k.Key.Substring(0, idx) : k.Key;
                        return !string.Equals(bk, baseKey, StringComparison.OrdinalIgnoreCase);
                    }).ToList();

                // Rebuild store: clear then re-insert remaining knowledge grouped by base key
                this.Client.ClearKnowledgeStore();

                var groups = remaining.GroupBy(k =>
                {
                    var idx = k.Key.IndexOf(" [chunk ", StringComparison.OrdinalIgnoreCase);
                    return idx >= 0 ? k.Key.Substring(0, idx) : k.Key;
                });

                foreach (var g in groups)
                {
                    string key = g.Key;
                    string combined = string.Join("\n\n", g.Select(x => x.Content ?? string.Empty));
                    string? source = g.Select(x => x.SourcePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                    await this.Client.UpsertKnowledgeAsync(key, combined, source);
                }

                // Refresh local snapshot
                this.KnowledgeEntries = this.Client.GetKnowledgeEntriesSnapshot();
                this.LastActionMessage = $"Removed knowledge: {baseKey}";
                this.RequestUiRefresh();
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[HomeViewModel] Error deleting knowledge by key");
            }
        }

        public Task KillAllLlamaServerExeInstancesAsync()
        {
            int? killed = this.Client.KillAllLlamaServerExeInstances();
            this.LastActionMessage = killed.HasValue
                ? $"Killed {killed.Value} llama-server instance(s)."
                : "Failed to kill llama-server instances.";

            // Server is gone — reset loaded state
            this.IsLoaded = false;
            this.LoadedModel = null;
            this.IsGenerating = false;
            this.LastGenerationStats = null;
            this.ModelLoadingTimeString = "Model unloaded (killed).";
            this.IsModelPanelExpanded = true;

            // Clear reused-instance flag because we killed servers
            this.IsReusedInstance = false;
            this.RequestUiRefresh();
            return Task.CompletedTask;
        }

        private void SyncChatMessagesFromClient()
        {
            this.ChatMessages = this.Client.GetConversationSnapshot().ToList();
        }

        private static int CountRoughTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            return text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        }


        private readonly record struct ImageDisplayInfo(string Label, int EstimatedTokens);

        private void RequestUiRefresh()
        {
            try { this.NotifyStateChanged?.Invoke(); } catch { }
        }

        private void ScheduleModelPanelAutoCollapse()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                if (this.IsLoaded)
                {
                    this.IsModelPanelExpanded = false;
                    this.RequestUiRefresh();
                }
            });
        }

        private void ScheduleLastActionMessageAutoDismiss(string? message)
        {
            CancellationTokenSource? ctsToCancel;
            CancellationTokenSource? newCts = null;

            lock (this.lastActionMessageSync)
            {
                ctsToCancel = this.lastActionMessageCts;
                this.lastActionMessageCts = null;

                if (!string.IsNullOrWhiteSpace(message))
                {
                    newCts = new CancellationTokenSource();
                    this.lastActionMessageCts = newCts;
                }
            }

            try
            {
                ctsToCancel?.Cancel();
                ctsToCancel?.Dispose();
            }
            catch
            {
            }

            if (newCts == null)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), newCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                bool shouldClear;
                lock (this.lastActionMessageSync)
                {
                    shouldClear = ReferenceEquals(this.lastActionMessageCts, newCts)
                        && string.Equals(this.lastActionMessage, message, StringComparison.Ordinal);
                    if (shouldClear)
                    {
                        this.lastActionMessage = null;
                        this.lastActionMessageCts = null;
                    }
                }

                if (shouldClear)
                {
                    this.RequestUiRefresh();
                }

                newCts.Dispose();
            });
        }

        // ── Lifecycle methods (called from Razor OnAfterRenderAsync) ──

        public async Task OnFirstRenderAsync(DotNetObjectReference<HomeViewModel> vmRef)
        {
            await this.InitializeAsync();
            await this.LoadPanelStatesAsync();
            this._panelStateLoaded = true;
            this._lastLoadedState = this.IsLoaded;

            await this.Js.InvokeVoidAsync("sharpestNavMenu.setupPromptEnter", "promptInput", vmRef);
            await this.Js.InvokeVoidAsync("sharpestNavMenu.setupClipboardImagePaste", "promptInput", vmRef);
            await this.Js.InvokeVoidAsync("sharpestNavMenu.setupVerticalResizeHandle", TopPanelsResizeHandleElementId, TopPanelsContentElementId, 140, 900);
            if (this.AutoScrollEnabled)
            {
                await this.Js.InvokeVoidAsync("sharpestNavMenu.scrollToBottom", ChatOutputElementId);
            }
            this._lastChatMessageCount = this.ChatMessages.Count;
        }

        public async Task OnSubsequentRenderAsync()
        {
            if (this._panelStateLoaded)
            {
                await this.PersistPanelStatesAsync();
            }

            if (this._lastLoadedState != this.IsLoaded)
            {
                this._lastLoadedState = this.IsLoaded;
                await this.PersistPanelStatesAsync();
            }

            if (this.AutoScrollEnabled && (this.ChatMessages.Count != this._lastChatMessageCount || this.IsGenerating))
            {
                this._lastChatMessageCount = this.ChatMessages.Count;
                await this.Js.InvokeVoidAsync("sharpestNavMenu.scrollToBottom", ChatOutputElementId);
            }
        }

        // ── Panel toggle methods ──

        public async Task ToggleModelPanelAsync()
        {
            this.IsModelPanelExpanded = !this.IsModelPanelExpanded;
            await this.PersistPanelStatesAsync();
        }

        public async Task ToggleContextPanelAsync()
        {
            this.IsContextPanelExpanded = !this.IsContextPanelExpanded;
            await this.PersistPanelStatesAsync();
        }

        public async Task ToggleKnowledgePanelAsync()
        {
            this.IsKnowledgePanelExpanded = !this.IsKnowledgePanelExpanded;
            await this.PersistPanelStatesAsync();
        }

        public async Task ToggleGenSettingsAsync()
        {
            this.GenSettingsExpanded = !this.GenSettingsExpanded;
            await this.PersistPanelStatesAsync();
        }

        public void ToggleImageAttachments()
        {
            this.ImageAttachmentsExpanded = !this.ImageAttachmentsExpanded;
        }

        // ── Event handlers (called directly from Razor markup) ──

        public void OnSelectedModelChanged()
        {
            this.UseMmproj = this.HasMmproj || this.IsSelectedOmni;
        }

        public async Task BrowseImagesClickAsync()
        {
            await this.Js.InvokeVoidAsync("sharpestNavMenu.triggerClick", "imagePicker");
        }

        public async Task BrowseJsonFormatClickAsync()
        {
            await this.Js.InvokeVoidAsync("sharpestNavMenu.triggerClick", "jsonFormatPicker");
        }

        public async Task BrowseKnowledgeFilesClickAsync()
        {
            await this.Js.InvokeVoidAsync("sharpestNavMenu.triggerClick", "knowledgeFilePicker");
        }

        public async Task OnImagesSelectedAsync(InputFileChangeEventArgs args)
        {
            await this.AddImageUploadsAsync(args.GetMultipleFiles());
        }

        public async Task OnKnowledgeFilesSelectedAsync(InputFileChangeEventArgs args)
        {
            await this.AddKnowledgeFromFilesAsync(args.GetMultipleFiles());
        }

        public async Task OnJsonFormatSelectedAsync(InputFileChangeEventArgs args)
        {
            var file = args.GetMultipleFiles(1).FirstOrDefault();
            if (file == null)
            {
                return;
            }

            await this.LoadJsonOutputFormatAsync(file);
        }

        public string RenderChatContent(string content)
        {
            string displayContent = StaticLogics.GetDisplayContent(content ?? string.Empty);
            return StaticLogics.RenderMarkdownOrJson(displayContent);
        }

        private void DetectPendingAgentActions(string assistantText)
        {
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return;
            }

            if (this.EnableCommandAgentMode && this.PendingCommandRequest == null
                && this.Client.TryExtractCommandRequest(assistantText, out var cmdRequest)
                && cmdRequest != null)
            {
                this.PendingCommandRequest = cmdRequest;
                this.PendingCommandSafety = this.Client.EvaluateCommandSafety(cmdRequest.Command);
                string safety = this.PendingCommandSafety.SafetyLevel;
                this.LastActionMessage = $"Agent-Aktion erkannt: CMD '{safety}' wartet auf Bestätigung.";
            }

            if (this.EnableWebSearchAgentMode && this.PendingWebSearchRequest == null
                && this.Client.TryExtractWebSearchRequest(assistantText, out var webRequest)
                && webRequest != null)
            {
                this.PendingWebSearchRequest = webRequest;
                this.LastActionMessage = this.PendingCommandRequest != null
                    ? "Agent-Aktionen erkannt: Kommando + Websuche warten auf Bestätigung."
                    : "Agent-Aktion erkannt: Websuche wartet auf Bestätigung.";
            }
        }

        [SupportedOSPlatform("windows")]
        private async Task TryAutoExecuteAllowedNonAdminCommandAsync()
        {
            if (!this.AllowAllNonAdminCommands || this.PendingCommandRequest == null || this.IsGenerating)
            {
                return;
            }

            var request = this.PendingCommandRequest;
            var safety = this.PendingCommandSafety ?? this.Client.EvaluateCommandSafety(request.Command);

            if (safety.IsBlocked)
            {
                return;
            }

            this.PendingCommandRequest = null;
            this.PendingCommandSafety = null;
            request.ShowWindow = this.AgentShowCommandWindow;

            this.LastActionMessage = $"Command auto-executed (allowed non-admin): {request.Command}";
            this.LastActionIsAllowedNonAdminCommand = true;
            this.RequestUiRefresh();

            bool allowElevated = safety.RequiresAdditionalConfirmation;
            var result = await this.Client.ExecuteCommandAsync(request, allowElevated: allowElevated, timeout: TimeSpan.FromSeconds(30));
            string injection = this.Client.BuildCommandResultInjectionPrompt(result);
            this.UserInput = AppendPromptForAgent(this.UserInput, injection);

            this.LastActionMessage = result.Success
                ? "Command executed automatically (allowed non-admin). Result was appended to prompt."
                : $"Auto command failed: {result.ErrorMessage ?? "Unknown error"}";
            this.LastActionIsAllowedNonAdminCommand = true;
            this.RequestUiRefresh();

            if (this.AutoContinueAgentActions && this.IsLoaded && !this.IsGenerating && !string.IsNullOrWhiteSpace(this.UserInput))
            {
                await this.StartGenerationAsync();
            }
        }

        [SupportedOSPlatform("windows")]
        private async Task TryAutoExecuteWebSearchAsync()
        {
            if (!this.AutoAllowWebSearch || this.PendingWebSearchRequest == null || this.IsGenerating)
            {
                return;
            }

            var request = this.PendingWebSearchRequest;
            this.PendingWebSearchRequest = null;

            this.LastActionMessage = request.IsDirectUrl
                ? $"WebSearch auto-executed URL: {request.Url}"
                : $"WebSearch auto-executed query: {request.Query}";
            this.RequestUiRefresh();

            var result = await this.Client.ExecuteWebSearchAsync(request);
            string injection = this.Client.BuildWebSearchResultInjectionPrompt(result);
            this.UserInput = AppendPromptForAgent(this.UserInput, injection);

            this.LastActionMessage = result.Success
                ? "WebSearch executed automatically. Result was appended to prompt."
                : $"Auto WebSearch failed: {result.ErrorMessage ?? "Unknown error"}";
            this.RequestUiRefresh();

            if (this.AutoContinueAgentActions && this.IsLoaded && !this.IsGenerating && !string.IsNullOrWhiteSpace(this.UserInput))
            {
                await this.StartGenerationAsync();
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task ConfirmPendingCommandAsync()
        {
            if (this.PendingCommandRequest == null || this.IsGenerating)
            {
                return;
            }

            var request = this.PendingCommandRequest;
            this.PendingCommandRequest = null;
            var safety = this.PendingCommandSafety ?? this.Client.EvaluateCommandSafety(request.Command);
            this.PendingCommandSafety = null;

            if (safety.IsBlocked)
            {
                this.LastActionMessage = $"Command blockiert: {safety.Reason}";
                this.RequestUiRefresh();
                return;
            }

            bool allowElevated = false;
            if (safety.RequiresAdditionalConfirmation)
            {
                bool confirmed = await this.Js.InvokeAsync<bool>(
                    "confirm",
                    $"Stärkerer Command erkannt ({safety.SafetyLevel}).\n\nCommand:\n{request.Command}\n\nGrund:\n{safety.Reason}\n\nWirklich ausführen?");

                if (!confirmed)
                {
                    this.LastActionMessage = "Stärkerer Command wurde vom Benutzer abgelehnt.";
                    this.RequestUiRefresh();
                    return;
                }

                allowElevated = true;
            }

            request.ShowWindow = this.AgentShowCommandWindow;
            this.LastActionMessage = $"Führe Command aus ({safety.SafetyLevel}): {request.Command}";
            this.RequestUiRefresh();

            var result = await this.Client.ExecuteCommandAsync(request, allowElevated, TimeSpan.FromSeconds(30));
            string injection = this.Client.BuildCommandResultInjectionPrompt(result);
            this.UserInput = AppendPromptForAgent(this.UserInput, injection);
            this.LastActionMessage = result.Success
                ? "Command ausgeführt. Ergebnis wurde an den Prompt angehängt."
                : $"Command fehlgeschlagen/geblockt: {result.ErrorMessage ?? "Unbekannter Fehler"}";

            this.RequestUiRefresh();

            if (this.AutoContinueAgentActions && this.IsLoaded && !this.IsGenerating && !string.IsNullOrWhiteSpace(this.UserInput))
            {
                await this.StartGenerationAsync();
            }
        }

        public void RejectPendingCommand()
        {
            if (this.PendingCommandRequest == null)
            {
                return;
            }

            this.PendingCommandRequest = null;
            this.PendingCommandSafety = null;
            this.LastActionMessage = "Command-Ausführung wurde verworfen.";
            this.RequestUiRefresh();
        }

        [SupportedOSPlatform("windows")]
        public async Task ConfirmPendingWebSearchAsync()
        {
            if (this.PendingWebSearchRequest == null || this.IsGenerating)
            {
                return;
            }

            var request = this.PendingWebSearchRequest;
            this.PendingWebSearchRequest = null;
            this.LastActionMessage = request.IsDirectUrl
                ? $"Lade URL: {request.Url}"
                : $"Starte Websuche: {request.Query}";
            this.RequestUiRefresh();

            var result = await this.Client.ExecuteWebSearchAsync(request);
            string injection = this.Client.BuildWebSearchResultInjectionPrompt(result);
            this.UserInput = AppendPromptForAgent(this.UserInput, injection);
            this.LastActionMessage = result.Success
                ? "Webergebnis geholt. Ergebnis wurde an den Prompt angehängt."
                : $"Websuche fehlgeschlagen: {result.ErrorMessage ?? "Unbekannter Fehler"}";

            this.RequestUiRefresh();

            if (this.AutoContinueAgentActions && this.IsLoaded && !this.IsGenerating && !string.IsNullOrWhiteSpace(this.UserInput))
            {
                await this.StartGenerationAsync();
            }
        }

        public void RejectPendingWebSearch()
        {
            if (this.PendingWebSearchRequest == null)
            {
                return;
            }

            this.PendingWebSearchRequest = null;
            this.LastActionMessage = "Websuche wurde verworfen.";
            this.RequestUiRefresh();
        }

        private static string AppendPromptForAgent(string existingPrompt, string injection)
        {
            if (string.IsNullOrWhiteSpace(existingPrompt))
            {
                return injection.Trim();
            }

            return existingPrompt.TrimEnd() + "\n\n" + injection.Trim();
        }

        // ── Panel state persistence ──

        private async Task LoadPanelStatesAsync()
        {
            await this.Js.InvokeVoidAsync("localStorage.removeItem", ModelExpandedStorageKey);
            await this.Js.InvokeVoidAsync("localStorage.removeItem", ContextExpandedStorageKey);
            await this.Js.InvokeVoidAsync("localStorage.removeItem", KnowledgeExpandedStorageKey);
            await this.Js.InvokeVoidAsync("localStorage.removeItem", GenSettingsExpandedStorageKey);

            this.IsModelPanelExpanded = true;
            this.IsContextPanelExpanded = false;
            this.IsKnowledgePanelExpanded = false;
            this.GenSettingsExpanded = false;
        }

        public async Task PersistPanelStatesAsync()
        {
            await this.Js.InvokeVoidAsync("localStorage.setItem", ModelExpandedStorageKey, this.IsModelPanelExpanded ? "1" : "0");
            await this.Js.InvokeVoidAsync("localStorage.setItem", ContextExpandedStorageKey, this.IsContextPanelExpanded ? "1" : "0");
            await this.Js.InvokeVoidAsync("localStorage.setItem", KnowledgeExpandedStorageKey, this.IsKnowledgePanelExpanded ? "1" : "0");
            await this.Js.InvokeVoidAsync("localStorage.setItem", GenSettingsExpandedStorageKey, this.GenSettingsExpanded ? "1" : "0");
        }


        public void Dispose()
        {
            CancellationTokenSource? ctsToCancel;
            lock (this.lastActionMessageSync)
            {
                ctsToCancel = this.lastActionMessageCts;
                this.lastActionMessageCts = null;
            }

            try
            {
                ctsToCancel?.Cancel();
                ctsToCancel?.Dispose();
            }
            catch
            {
            }

            this.autoRefreshTimer?.Dispose();
            this.autoRefreshTimer = null;

            GC.SuppressFinalize(this);
        }



        
        
        public async Task ToggleModelAsync()
        {
            if (this.IsBusy)
            {
                return;
            }

            this.IsBusy = true;
            this.LastLoadError = null;
            this.RequestUiRefresh();

            try
            {
                if (this.IsLoaded)
                {
                    // Unload
                    await StaticLogger.LogAsync("[Blazor] Unloading model...");

                    if (this.IsReusedInstance)
                    {
                        // Reused external instance cannot be unloaded in-place via tracked process handle.
                        // Kill server process(es) to actually release RAM/VRAM.
                        _ = this.Client.KillAllLlamaServerExeInstances();
                        this.selectDefaultModelAfterReusedUnload = true;
                    }
                    else
                    {
                        this.Client.UnloadModel();
                    }

                    this.IsLoaded = false;
                    this.IsReusedInstance = false;
                    this.LoadedModel = null;
                    this.ModelLoadingTimeString = "Model unloaded.";
                    this.IsModelPanelExpanded = true;
                    await StaticLogger.LogAsync("[Blazor] Model unloaded successfully.");
                }
                else
                {
                    // Load
                    LlamaModelInfo? modelToLoad = this.LlamaModels.FirstOrDefault(m => m.Name.Equals(this.SelectedModelName, StringComparison.OrdinalIgnoreCase));
                    if (modelToLoad == null)
                    {
                        this.LastLoadError = $"Model '{this.SelectedModelName}' not found in model list.";
                        await StaticLogger.LogAsync($"[Blazor] {this.LastLoadError}");
                        return;
                    }

                    LlamaModelLoadRequest loadRequest = new()
                    {
                        ModelInfo = modelToLoad,
                        ServerExecutablePath = this.Settings.ServerExecutablePath,
                        ContextSize = this.ContextSize,
                        BatchSize = Math.Max(1, this.Settings.DefaultBatchSize),
                        UseFlashAttention = this.UseFlashAttention,
                        IncludeMmproj = this.UseMmproj,
                    };

                    await StaticLogger.LogAsync($"[Blazor] Loading model '{modelToLoad.Name}'...");
                    await StaticLogger.LogAsync($"[Blazor]   Executable : {loadRequest.ServerExecutablePath}");
                    await StaticLogger.LogAsync($"[Blazor]   ModelFile  : {modelToLoad.ModelFilePath}");
                    await StaticLogger.LogAsync($"[Blazor]   Mmproj     : {(loadRequest.IncludeMmproj ? modelToLoad.MmprojFilePath ?? "(none)" : "(disabled)")}");
                    await StaticLogger.LogAsync($"[Blazor]   Context    : {loadRequest.ContextSize}  Batch: {loadRequest.BatchSize}  FlashAttn: {loadRequest.UseFlashAttention}");
                    if (loadRequest.UseFlashAttention && modelToLoad.IsTernaryQuantized)
                    {
                        await StaticLogger.LogAsync($"[Blazor]   NOTE: Flash Attention will be auto-disabled for ternary quantized model '{modelToLoad.Name}'.");
                    }
                    await StaticLogger.LogAsync($"[Blazor]   Endpoint   : http://{loadRequest.Host}:{loadRequest.Port}");

                    this.ModelLoadingTimeString = "Loading model…";
                    this.RequestUiRefresh();

                    Stopwatch sw = Stopwatch.StartNew();
                    LlamaModelLoadResult response = await this.Client.LoadModelAsync(loadRequest);
                    sw.Stop();

                    if (response.Success)
                    {
                        this.ModelLoadingTimeString = response.ReusedExistingInstance
                            ? "Attached to existing llama-server instance."
                            : $"{sw.Elapsed.TotalSeconds:F3} sec. elapsed loading.";
                        this.IsLoaded = true;
                        this.IsReusedInstance = response.ReusedExistingInstance;
                        this.LoadedModel = response.ReusedExistingInstance
                            ? (this.ResolveModelFromServerId(response.ActiveModelId) ?? modelToLoad)
                            : modelToLoad;
                        if (this.LoadedModel != null)
                        {
                            this.SelectedModelName = this.LoadedModel.Name;
                        }
                        this.IsModelPanelExpanded = true;
                        await StaticLogger.LogAsync($"[Blazor] Model loaded successfully in {sw.Elapsed.TotalSeconds:F3}s — API at {response.BaseApiUrl}");

                        if (response.ReusedExistingInstance)
                        {
                            this.LastActionMessage = "An existing llama-server instance was already running and is now reused.";
                        }

                        this.ScheduleModelPanelAutoCollapse();
                    }
                    else
                    {
                        this.ModelLoadingTimeString = $"Load failed after {sw.Elapsed.TotalSeconds:F3} sec.";
                        this.LastLoadError = response.ErrorMessage ?? "Unknown error during model load.";
                        this.IsLoaded = false;
                        this.LoadedModel = null;
                        await StaticLogger.LogAsync($"[Blazor] Model load FAILED after {sw.Elapsed.TotalSeconds:F3}s: {this.LastLoadError}");
                    }
                }
            }
            catch (Exception ex)
            {
                this.LastLoadError = ex.Message;
                this.ModelLoadingTimeString = "Load failed.";
                await StaticLogger.LogAsync("[Blazor] Exception during model load/unload: " + ex.Message);
                await StaticLogger.LogAsync(ex);
            }
            finally
            {
                this.IsBusy = false;
                await this.RefreshAsync();
                await this.PersistPanelStatesAsync();
                this.RequestUiRefresh();
            }
        }

        private LlamaModelInfo? ResolveModelFromServerId(string? activeModelId)
        {
            if (string.IsNullOrWhiteSpace(activeModelId))
            {
                return null;
            }

            string raw = activeModelId.Trim();
            string fileName = Path.GetFileName(raw);
            string fileNameNoExt = Path.GetFileNameWithoutExtension(raw);

            return this.LlamaModels.FirstOrDefault(m =>
                m.Name.Equals(raw, StringComparison.OrdinalIgnoreCase) ||
                m.Name.Equals(fileNameNoExt, StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains(fileNameNoExt, StringComparison.OrdinalIgnoreCase) ||
                fileNameNoExt.Contains(m.Name, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(m.ModelFilePath).Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(m.ModelFilePath).Equals(fileNameNoExt, StringComparison.OrdinalIgnoreCase) ||
                m.ModelFilePath.Contains(raw, StringComparison.OrdinalIgnoreCase));
        }

        private string? BuildEffectiveSystemPrompt()
        {
            string? baseSystemPrompt = this.UseSystemPrompt ? this.SystemPrompt : null;

            string toolInstructions = this.BuildToolInstructionPrompt();
            if (!string.IsNullOrWhiteSpace(toolInstructions))
            {
                baseSystemPrompt = string.IsNullOrWhiteSpace(baseSystemPrompt)
                    ? toolInstructions
                    : baseSystemPrompt.Trim() + "\n\n" + toolInstructions;
            }

            if (!this.UseJsonOutputFormat || !this.HasJsonOutputFormat)
            {
                return baseSystemPrompt;
            }

            const string jsonInstructionHeader =
                "You must respond with valid JSON only. Do not output markdown, code fences, prose, or additional commentary.";

            string strictFormatInstruction =
                $"{jsonInstructionHeader}\n"
                + "Use exactly this JSON structure (same keys and nesting):\n"
                + this.JsonOutputFormatTemplate;

            if (string.IsNullOrWhiteSpace(baseSystemPrompt))
            {
                return strictFormatInstruction;
            }

            return baseSystemPrompt.Trim() + "\n\n" + strictFormatInstruction;
        }

        private string BuildDefaultSystemPromptFromSettings()
        {
            var configured = this.Settings.SystemPrompts?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(EnsureSentenceEndsWithPunctuation)
                .ToList() ?? [];

            if (configured.Count > 0)
            {
                return string.Join(" ", configured);
            }

            return "You are a helpful, concise assistant.";
        }

        private string BuildToolInstructionPrompt()
        {
            var lines = new List<string>();

            if (this.EnableCommandAgentMode)
            {
                lines.Add("Only emit command requests when the user explicitly asks to execute a command.");
                lines.Add("Wrap executable command requests strictly in <cmd_start> and <cmd_end> tags.");
                lines.Add("Do not output command tags for normal explanations or command suggestions.");
            }

            if (this.EnableWebSearchAgentMode)
            {
                lines.Add("Wrap web requests in <websearch> and </websearch> tags when external lookup is required.");
            }

            return lines.Count == 0 ? string.Empty : string.Join("\n", lines);
        }

        private static string EnsureSentenceEndsWithPunctuation(string text)
        {
            string trimmed = text.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            char last = trimmed[^1];
            if (last is '.' or '!' or '?')
            {
                return trimmed;
            }

            return trimmed + ".";
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
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                this.LastHardwareStats = await this.Client.GetCurrentHardwareStatisticsAsync();

                if (this.LastHardwareStats?.CpuStats != null)
                {
                    StaticLogics.AppendHistory(this.cpuUsageHistory, this.LastHardwareStats.CpuStats.AverageLoadPercentage);
                }

                if (this.LastHardwareStats?.GpuStats != null)
                {
                    StaticLogics.AppendHistory(this.gpuUsageHistory, this.LastHardwareStats.GpuStats.CoreLoadPercentage);
                }
            }
            catch
            {
                // ignore errors
            }
        }

        public string GetCpuSparklineSvg(int width = 180, int height = 32)
        {
            return StaticLogics.GetSparklineSvg(this.cpuUsageHistory, width, height, this.SparklineCpuColor, StaticLogics.GetLighterColorGradient(this.SparklineCpuColor), this.CpuManufacturerName + " CPU");
        }

        public string GetGpuSparklineSvg(int width = 180, int height = 32)
        {
            return StaticLogics.GetSparklineSvg(this.gpuUsageHistory, width, height, this.SparklineGpuColor, StaticLogics.GetLighterColorGradient(this.SparklineGpuColor), this.GpuManufacturerName + " GPU");
        }

        


    }
}
