using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using System.Threading;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.Web;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using SharpestLlmStudio.Shared;
using SharpestLlmStudio.Runtime;
using SharpestLlmStudio.Runtime.ONNX;
using SharpesLlmStudio.Media;
using System.Runtime.Versioning;
using System.Net;
using System.Text.RegularExpressions;

namespace SharpestLlmStudio.WebApp.ViewModels
{
    public class HomeViewModel : IDisposable
    {
        private Action? stateChangedListeners;
        private readonly LlamaCppClient Client;
        private readonly OnnxWhisperService Whisper;
        private readonly IJSRuntime Js;
        private readonly WebAppSettings Settings;
        private CancellationTokenSource? generationCts;
        private ElementReference messageContainerRef;

        public void RegisterStateChangeListener(Action listener)
        {
            this.stateChangedListeners += listener;
        }



        public void UnregisterStateChangeListener(Action listener)
        {
            this.stateChangedListeners -= listener;
        }

        private void RaiseStateChanged()
        {
            try { this.stateChangedListeners?.Invoke(); } catch { }
        }


        // API Data
        public ICollection<string> DirectMlDevices { get; set; } = [];
        public ICollection<LlamaModelInfo> LlamaModels { get; private set; } = [];

        // State Data
        public string? SelectedDirectMlDevice { get; set; }
        public int DirectMlDeviceIndex => this.DirectMlDevices != null && this.SelectedDirectMlDevice != null ? this.DirectMlDevices.ToList().IndexOf(this.SelectedDirectMlDevice) -1 : -1;
        private string? selectedModelName = null;
        public string? SelectedModelName
        {
            get => this.selectedModelName;
            set
            {
                if (string.Equals(this.selectedModelName, value, StringComparison.Ordinal)) return;
                this.selectedModelName = value;
                // Keep dependent state in sync and rebuild system prompt when model changes
                this.OnSelectedModelChanged();
                // Rebuild the system prompt to include/exclude Vision prompts based on mmproj availability
                this.SystemPrompt = this.BuildDefaultSystemPromptFromSettings();
                this.RequestUiRefresh();
            }
        }

        public static readonly IReadOnlyList<string> ModelSortOptions = ["A - Z", "Biggest", "Params", "Newest", "Vision"];

        private string modelSortMode = "Newest";
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

        private int modelSortIndex = 3;
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
        public bool NoWarmup { get; set; } = false;
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
        public IEnumerable<ContextFileDisplayItem> ContextFileDisplayItems =>
            this.ContextFiles.Select(f => new ContextFileDisplayItem(f, Path.GetFileNameWithoutExtension(f)));
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

        // ── Whisper / Speech-to-Text ──
        public ICollection<OnnxWhisperModel> WhisperModels => this.Whisper.WhisperModels
            .Where(m => m.ModelFilePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .ToList();
        public string? SelectedWhisperModelName { get; set; }
        public bool IsWhisperLoaded => this.Whisper.IsLoaded;
        public bool IsWhisperTranscribing => this.Whisper.IsTranscribing;
        public bool IsWhisperLiveMode => this.Whisper.IsLiveMode;
        public bool IsMicRecording { get; private set; }
        public double MicLevel { get; private set; }
        private Task<AudioObj?>? _activeRecordingTask;
        // UI settings for transcription
        public string WhisperLanguage { get; set; } = "auto"; // 'auto' means automatic detection
        public bool WhisperTimestamps { get; set; } = false;
        public bool WhisperSpeakers { get; set; } = false;

        // Generation UI state
        private string userInput = string.Empty;
        public string UserInput
        {
            get => this.userInput;
            set
            {
                this.userInput = value ?? string.Empty;
                this.RaiseStateChanged();
            }
        }

        private void UpdateMicLevel(float level)
        {
            double clamped = Math.Clamp(level, 0f, 1f);
            this.MicLevel = Math.Max(clamped, this.MicLevel * 0.70);
            this.RaiseStateChanged();
        }

        private void ResetMicLevel()
        {
            this.MicLevel = 0;
            this.RaiseStateChanged();
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
                this.RaiseStateChanged();
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
                this.RaiseStateChanged();
                if (this.AutoRefreshEnabled)
                {
                    this.StartAutoRefresh();
                }
            }
        }


        // New overload: start generation from provided prompt (used by UI component)
        
          
        public string GeneratedOutput { get; set; } = string.Empty;
        public bool IsGenerating { get; set; } = false;
        // Indicates that an agent-invoked action (websearch/command) is currently running.
        // This keeps the Cancel button enabled so the user can abort agent tool calls too.
        public bool IsAgentActionRunning { get; set; } = false;

        public bool CanSend =>! this.IsGenerating && !string.IsNullOrWhiteSpace(this.UserInput);
        public string SystemPrompt { get; set; } = string.Empty;
        public string SystemPromptDisplay
        {
            get => FormatSystemPromptForDisplay(this.SystemPrompt);
            set
            {
                string normalized = NormalizeSystemPromptFromDisplay(value);
                if (string.Equals(this.SystemPrompt, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                this.SystemPrompt = normalized;
                this.RequestUiRefresh();
            }
        }

        public List<LlamaChatMessage> ChatMessages { get; private set; } = [];
        // Sum of estimated tokens for uploaded images (updated on upload)
        public int ImageEstimatedTokensTotal { get; private set; } = 0;

        public string ContextSaveName { get; set; } = string.Empty;
        public string? SelectedContextFilePath { get; set; } = null;

        public string KnowledgeKey { get; set; } = string.Empty;
        public string KnowledgeContent { get; set; } = string.Empty;
        public string KnowledgeQuery { get; set; } = string.Empty;
        public int KnowledgeTopK { get; set; } = 3;
        private bool useKnowledgeRagV2;
        public bool UseKnowledgeRagV2
        {
            get => this.useKnowledgeRagV2;
            set
            {
                if (this.useKnowledgeRagV2 == value)
                {
                    return;
                }

                this.useKnowledgeRagV2 = value;
                this.KnowledgeResults = [];
                this.RefreshKnowledgeEntriesFromClient();
            }
        }
        private bool knowledgeAutoChunkSize = true;
        public bool KnowledgeAutoChunkSize
        {
            get => this.knowledgeAutoChunkSize;
            set
            {
                this.knowledgeAutoChunkSize = value;
                if (value)
                {
                    this.knowledgeChunkSize = null;
                }
                else if (!this.knowledgeChunkSize.HasValue)
                {
                    this.knowledgeChunkSize = 1024;
                }

                // Switch preset to Custom when manually changing auto mode
                if (this.selectedKnowledgePreset != KnowledgePresetMode.Custom)
                {
                    this.selectedKnowledgePreset = KnowledgePresetMode.Custom;
                }

                this.RequestUiRefresh();
            }
        }

        private int? knowledgeChunkSize;
        public int? KnowledgeChunkSize
        {
            get => this.knowledgeChunkSize;
            set
            {
                this.knowledgeChunkSize = value.HasValue
                    ? Math.Clamp(value.Value, 256, 4096)
                    : null;

                // Switch preset to Custom when manually changing chunk size
                if (this.selectedKnowledgePreset != KnowledgePresetMode.Custom)
                {
                    this.selectedKnowledgePreset = KnowledgePresetMode.Custom;
                }

                this.RequestUiRefresh();
            }
        }

        public enum KnowledgePresetMode { Custom, Fast, Balanced, Precision }
        public static IReadOnlyList<KnowledgePresetMode> KnowledgePresetModes { get; } = Enum.GetValues<KnowledgePresetMode>();
        private KnowledgePresetMode selectedKnowledgePreset = KnowledgePresetMode.Balanced;
        public KnowledgePresetMode SelectedKnowledgePreset
        {
            get => this.selectedKnowledgePreset;
            set
            {
                this.selectedKnowledgePreset = value;
                switch (value)
                {
                    case KnowledgePresetMode.Fast:
                        this.knowledgeAutoChunkSize = false;
                        this.knowledgeChunkSize = 2048;
                        break;
                    case KnowledgePresetMode.Balanced:
                        this.knowledgeAutoChunkSize = true;
                        this.knowledgeChunkSize = null;
                        break;
                    case KnowledgePresetMode.Precision:
                        this.knowledgeAutoChunkSize = false;
                        this.knowledgeChunkSize = 512;
                        break;
                    case KnowledgePresetMode.Custom:
                    default:
                        break;
                }

                this.RequestUiRefresh();
            }
        }

        /// <summary>Effective chunk size for the active RAG mode (null = auto).</summary>
        public int? KnowledgeChunkSizeForRagV2 => !this.KnowledgeAutoChunkSize ? this.KnowledgeChunkSize : null;
        /// <summary>Effective chunk size for legacy RAG (null = batch-size-based auto).</summary>
        public int? KnowledgeChunkSizeForLegacy => !this.KnowledgeAutoChunkSize ? this.KnowledgeChunkSize : null;
        public bool IsKnowledgeBusy { get; private set; }
        public string KnowledgeBusyMessage { get; private set; } = string.Empty;
        public int KnowledgeProgressPercent { get; private set; }
        public string KnowledgeProgressCurrentItem { get; private set; } = string.Empty;
        public string KnowledgeElapsedText { get; private set; } = "00:00";
        public IReadOnlyList<LlamaKnowledgeSearchResult> KnowledgeResults { get; private set; } = [];
        public IReadOnlyList<LlamaKnowledgeEntry> KnowledgeEntries { get; private set; } = [];
        private CancellationTokenSource? knowledgeOperationCts;
        private Stopwatch? knowledgeOperationStopwatch;
        private System.Threading.Timer? knowledgeElapsedTimer;

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
        public string LastActionMessageCssClass
        {
            get
            {
                if (string.IsNullOrWhiteSpace(this.lastActionMessage))
                {
                    return "header-action-info";
                }

                if (this.LastActionIsAllowedNonAdminCommand)
                {
                    return "header-action-success";
                }

                string message = this.lastActionMessage;
                if (message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("discarded", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("rejected", StringComparison.OrdinalIgnoreCase))
                {
                    return "header-action-error";
                }

                if (message.Contains("loaded", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("complete", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("finished", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("saved", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("reused", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("started", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("stopped", StringComparison.OrdinalIgnoreCase))
                {
                    return "header-action-success";
                }

                return "header-action-info";
            }
        }
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
                this.RaiseStateChanged();
            }
        }

        private bool isContextPanelExpanded = false;
        public bool IsContextPanelExpanded
        {
            get => this.isContextPanelExpanded;
            set
            {
                this.isContextPanelExpanded = value;
                this.RaiseStateChanged();
            }
        }

        private bool isKnowledgePanelExpanded = false;
        public bool IsKnowledgePanelExpanded
        {
            get => this.isKnowledgePanelExpanded;
            set
            {
                this.isKnowledgePanelExpanded = value;
                this.RaiseStateChanged();
            }
        }

        private bool isStatsPanelExpanded = true;
        public bool IsStatsPanelExpanded
        {
            get => this.isStatsPanelExpanded;
            set
            {
                this.isStatsPanelExpanded = value;
                this.RaiseStateChanged();
            }
        }

        public float GenTemperature { get; set; } = 0.7f;
        public int GenMaxTokens { get; set; } = 512;
        private double genRepetitionPenalty = 1.1;
        public double GenRepetitionPenalty
        {
            get => this.genRepetitionPenalty;
            set
            {
                this.genRepetitionPenalty = value;
                this.RequestUiRefresh();
            }
        }
        public float GenTopP { get; set; } = 0.9f;
        public int GenTopK { get; set; } = 40;
        public int GenBatchSize { get; set; } = 512;
        public int GenUBatchSize { get; set; } = 512;

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
        // Controls whether the top management panels area is expanded (visible) or collapsed.
        private bool topPanelsExpanded = true;
        public bool TopPanelsExpanded
        {
            get => this.topPanelsExpanded;
            set
            {
                if (this.topPanelsExpanded == value) return;
                this.topPanelsExpanded = value;
                this.RequestUiRefresh();
            }
        }

        // Active management tab index (kept in ViewModel so it can control per-tab expansion heights)
        private int activeManagementTabIndex = 0;
        public int ActiveManagementTabIndex
        {
            get => this.activeManagementTabIndex;
            set
            {
                if (this.activeManagementTabIndex == value) return;
                this.activeManagementTabIndex = value;
                this.RequestUiRefresh();
            }
        }

        public int ViewPortHeight { get; private set; } = 900;

        // Returns an inline style for the top-panels-content that ensures the expanded area
        // fits the active tab's preferred height. When collapsed, max-height is set to 0.
        public string TopPanelsMaxHeightStyle
        {
            get
            {
                if (!this.TopPanelsExpanded)
                {
                    return "max-height:0;overflow:hidden;transition:max-height 0.28s ease;";
                }

                // preferred heights per tab (px). Tune as needed.
                int[] preferred = new int[] { 520, 360, 520, 360, 620 };
                int idx = Math.Clamp(this.ActiveManagementTabIndex, 0, preferred.Length - 1);
                int height = preferred[idx];
                // cap to viewport percentage if very tall
                int maxViewportPx = (int)(this.ViewPortHeight * 0.85);
                int final = Math.Min(height, Math.Max(200, maxViewportPx));
                return $"max-height:{final}px;overflow:hidden;transition:max-height 0.28s ease;";
            }
        }

        private bool enableCommandAgentMode = false;
        public bool EnableCommandAgentMode
        {
            get => this.enableCommandAgentMode;
            set
            {
                if (this.enableCommandAgentMode == value) return;
                this.enableCommandAgentMode = value;
                // rebuild stored SystemPrompt so agent prompts are added/removed immediately
                this.SystemPrompt = this.BuildDefaultSystemPromptFromSettings();
                this.RequestUiRefresh();
            }
        }

        private bool enableWebSearchAgentMode = false;
        public bool EnableWebSearchAgentMode
        {
            get => this.enableWebSearchAgentMode;
            set
            {
                if (this.enableWebSearchAgentMode == value) return;
                this.enableWebSearchAgentMode = value;
                // rebuild stored SystemPrompt so agent prompts are added/removed immediately
                this.SystemPrompt = this.BuildDefaultSystemPromptFromSettings();
                this.RequestUiRefresh();
            }
        }

        public bool AutoAllowWebSearch { get; set; } = true;
        public bool AutoContinueAgentActions { get; set; } = false;
        public bool AllowAllNonAdminCommands { get; set; } = false;
        public bool AgentShowCommandWindow { get; set; } = false;

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

                string cumulative = GenerationStats.AccumulatedUsedWattsApprox > 0.0
                    ? $" | cost: {GenerationStats.AccumulatedUsedWattsApprox:F1} W ({this.Settings.CurrencySymbol}{GenerationStats.AccumulatedCostApprox:F6})"
                    : string.Empty;
                return $"{stats.TotalContextTokens} of {stats.ContextSize} tokens used{cumulative}";
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
                return $"tok: {stats.TotalTokensGenerated} | tok/s: {stats.TokensPerSecond:F3} | TTFT: {ttft} | total: {total}";
            }
        }

        public string GenerationStatsPowerUsage
        {
            get
            {
                var stats = this.LastGenerationStats;
                if (stats == null)
                {
                    return "";
                }
                
                double wattsUsed = Math.Max(0.0, stats.UsedWattsApprox ?? 0.0);
                double timeElapsed = Math.Max(0.0, stats.TotalGenerationTime?.TotalHours ?? 0.0);
                double pricePerKwh = this.Settings.PricePerKiloWattHour;
                double price = (wattsUsed * timeElapsed / 1000.0) * pricePerKwh;
                string currency = this.Settings.CurrencySymbol ?? "₪";
                return wattsUsed > 0 && timeElapsed > 0
                    ? $"power: {wattsUsed:F1} W | cost: {currency}{price:F6}"
                    : "";
            }
        }

        public string StatsDisplay
        {
            get
            {
                var hw = this.LastHardwareStats;
                if (hw == null)
                {
                    return "RAM: -, VRAM: - GPU: -, CPU: -";
                }
                return $"RAM: {hw.RamStats.MemoryUsagePercentage:F1}% | VRAM: {hw.GpuStats.VramStats.MemoryUsagePercentage:F1}% | GPU: {hw.GpuStats.CoreLoadPercentage:F1}% | CPU: {hw.CpuStats.AverageLoadPercentage:F1}%";
            }
        }

        public bool FirstRender { get; private set; } = true;
        private bool selectDefaultModelAfterReusedUnload;


        public HomeViewModel(LlamaCppClient ApiClient, IJSRuntime js, WebAppSettings webAppSettings, OnnxWhisperService whisperService)
        {
            this.Client = ApiClient;
            this.Whisper = whisperService;
            this.Js = js;
            this.Settings = webAppSettings;
            this.AgentShowCommandWindow = this.Settings.AgentShowCommandWindow;
            this.AutoContinueAgentActions = this.Settings.AgentAutoContinue;
            this.AllowAllNonAdminCommands = this.Settings.AllowAllNonAdminCommands;
            this.AutoAllowWebSearch = this.Settings.AutoAllowWebSearch;
            this.useKnowledgeRagV2 = this.Settings.DefaultUseKnowledgeRagV2;
            this.knowledgeAutoChunkSize = this.Settings.DefaultKnowledgeAutoChunkSize || !this.Settings.DefaultKnowledgeChunkSize.HasValue;
            this.knowledgeChunkSize = this.knowledgeAutoChunkSize
                ? this.Settings.DefaultBatchSize
                : this.Settings.DefaultKnowledgeChunkSize ?? this.Settings.DefaultBatchSize;
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
                        this.RaiseStateChanged();
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
            GenerationStats.ResetAccumulatedTotals();

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
            this.LastGenerationStats = null;
            await this.RefreshContextAsync();
            this.RaiseStateChanged();
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
                this.RaiseStateChanged();
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
                    this.RaiseStateChanged();
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

            this.RaiseStateChanged();
        }

        public async Task ScrollChatToBottomAsync()
        {
            try
            {
                await this.Js.InvokeVoidAsync("sharpestNavMenu.scrollToBottom", ChatOutputElementId);
            }
            catch
            {
            }
        }

        private void CollapseManagementPanels(bool collapseContext = false, bool collapseKnowledge = false)
        {
            if (collapseContext)
            {
                this.IsContextPanelExpanded = false;
            }

            if (collapseKnowledge)
            {
                this.IsKnowledgePanelExpanded = false;
            }
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

            this.RaiseStateChanged();
        }

        public void RemoveImagePath(string path)
        {
            this.SelectedImagePaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            this.loadedImageMetadata.Remove(path);

            this.RaiseStateChanged();
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

            // Collapse top panels when starting a generation so the user sees the chat area
            try { this.TopPanelsExpanded = false; } catch { }

            this.generationCts?.Cancel();
            this.generationCts?.Dispose();
            this.generationCts = new CancellationTokenSource();

            string prompt = this.UserInput.Trim();
            string promptForGeneration = prompt;
            string assistantText = string.Empty;
            string? requestSystemPrompt = null;

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
                requestSystemPrompt = this.BuildEffectiveSystemPrompt();

                // Automatically augment prompt with knowledge context when available
                if (this.KnowledgeEntries.Count > 0)
                {
                    try
                    {
                        if (this.UseKnowledgeRagV2)
                        {
                            var promptPackage = await this.Client.BuildKnowledgePromptPackageV2Async(prompt, this.KnowledgeTopK, this.ContextSize, this.GenMaxTokens, this.generationCts.Token);
                            promptForGeneration = promptPackage.UserPrompt;
                            requestSystemPrompt = this.BuildEffectiveSystemPrompt(promptPackage.SystemPromptInstructions);
                        }
                        else
                        {
                            promptForGeneration = await this.Client.BuildKnowledgeAugmentedPromptAsync(prompt, this.KnowledgeTopK, this.ContextSize, this.GenMaxTokens, this.generationCts.Token);
                        }
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
                    TopK = this.GenTopK,
                    // Pass image prefs from UI into the generation request
                    MaxWidthAndHeight = this.ImageMaxDimension,
                    ImageFormat = this.ImageFormat,
                    Stream = true,
                    SystemPrompt = requestSystemPrompt
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

                // Detection of agent tool requests will be performed after generation completes

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
                this.LastGenerationStats = this.Client.GetLastGenerationStatsSnapshot();
                this.RequestUiRefresh();
                // Now that generation is fully finished, detect agent actions in the final assistant output
                try
                {
                    this.DetectPendingAgentActions(assistantText);
                }
                catch { }
                await this.ForceScrollToBottomAsync();
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


        // ── Whisper / Speech-to-Text ──

        public async Task LoadWhisperModelAsync()
        {
            if (string.IsNullOrEmpty(this.SelectedWhisperModelName)) return;
            var model = this.Whisper.WhisperModels.FirstOrDefault(m => m.ModelName == this.SelectedWhisperModelName);
            if (model == null) return;

            this.IsBusy = true;
            this.LastActionMessage = $"Loading Whisper model: {model.ModelName}...";
            this.RequestUiRefresh();
            try
            {
                var success = await this.Whisper.LoadModelAsync(model.ModelFilePath);
                this.LastActionMessage = success
                    ? $"Whisper model loaded: {model.ModelName}"
                    : $"Failed to load Whisper model: {model.ModelName}";
            }
            catch (Exception ex)
            {
                this.LastActionMessage = $"Whisper load error: {ex.Message}";
                await StaticLogger.LogAsync(ex, "[HomeViewModel] Whisper model load error");
            }
            finally
            {
                this.IsBusy = false;
                this.RequestUiRefresh();
            }
        }

        public void UnloadWhisperModel()
        {
            this.Whisper.UnloadModel();
            this.ResetMicLevel();
            this.LastActionMessage = "Whisper model unloaded.";
            this.RequestUiRefresh();
        }

        private void AppendWhisperText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            string trimmed = text.Trim();
            this.UserInput = string.IsNullOrWhiteSpace(this.UserInput)
                ? trimmed
                : this.UserInput.TrimEnd() + " " + trimmed;
        }

        private async Task StreamTranscriptionToUserInputAsync(IAsyncEnumerable<string> segments)
        {
            await foreach (var segment in segments)
            {
                this.AppendWhisperText(segment);
                this.RequestUiRefresh();
            }
        }

        [JSInvokable]
        public async Task OnMicClick()
        {
            if (this.IsMicRecording)
            {
                await this.StopMicAndTranscribeAsync();
            }
            else
            {
                await this.StartMicRecordingAsync();
            }
        }

        [JSInvokable]
        public async Task OnMicHoldStart()
        {
            await this.StartMicRecordingAsync();
        }

        [JSInvokable]
        public async Task OnMicHoldEnd()
        {
            await this.StopMicAndTranscribeAsync();
        }

        private async Task StartMicRecordingAsync()
        {
            if (this.IsMicRecording) return;
            this.IsMicRecording = true;
            this.ResetMicLevel();
            this.RequestUiRefresh();

            // Record at Whisper-native format (16 kHz, 16 bit, mono)
            this._activeRecordingTask = this.Whisper.Audio.RecordAudioAsync(
                sampleRate: OnnxWhisperService.WhisperSampleRate,
                bitDepth: 16,
                channels: OnnxWhisperService.WhisperChannels,
                onLevel: this.UpdateMicLevel);
        }

        private async Task StopMicAndTranscribeAsync()
        {
            if (!this.IsMicRecording) return;

            this.Whisper.Audio.StopRecording();
            this.IsMicRecording = false;
            this.ResetMicLevel();
            this.RequestUiRefresh();

            if (this._activeRecordingTask != null)
            {
                var audio = await this._activeRecordingTask;
                this._activeRecordingTask = null;

                if (audio != null && audio.Data.Length > 0 && this.Whisper.IsLoaded)
                {
                    this.LastActionMessage = "Transcribing audio...";
                    this.RequestUiRefresh();
                    try
                    {
                        string? lang = string.Equals(this.WhisperLanguage, "auto", StringComparison.OrdinalIgnoreCase) ? null : this.WhisperLanguage;
                        await this.StreamTranscriptionToUserInputAsync(this.Whisper.TranscribeAsyncEnumerable(audio, lang, this.WhisperTimestamps, this.WhisperSpeakers));
                        this.LastActionMessage = "Transcription complete.";
                    }
                    catch (Exception ex)
                    {
                        this.LastActionMessage = $"Transcription failed: {ex.Message}";
                        await StaticLogger.LogAsync(ex, "[Whisper] Transcription error");
                    }
                }
                else if (!this.Whisper.IsLoaded)
                {
                    this.LastActionMessage = "No Whisper model loaded. Recording discarded.";
                }
            }
            this.RequestUiRefresh();
        }

        public async Task ToggleLiveModeAsync()
        {
            if (this.Whisper.IsLiveMode)
            {
                this.Whisper.StopLiveMode();
                this.ResetMicLevel();
                this.LastActionMessage = "Live transcription stopped.";
                this.RequestUiRefresh();
            }
            else
            {
                if (!this.Whisper.IsLoaded)
                {
                    this.LastActionMessage = "Load a Whisper model first.";
                    this.RequestUiRefresh();
                    return;
                }

                this.LastActionMessage = "Starting live transcription...";
                this.RequestUiRefresh();

                string? lang = string.Equals(this.WhisperLanguage, "auto", StringComparison.OrdinalIgnoreCase) ? null : this.WhisperLanguage;
                await this.Whisper.StartLiveModeAsync(text =>
                {
                    this.AppendWhisperText(text);
                    this.RequestUiRefresh();
                }, lang, this.WhisperTimestamps, this.WhisperSpeakers, this.UpdateMicLevel);
            }
        }

        public async Task OnAudioFilesSelectedAsync(InputFileChangeEventArgs args)
        {
            var file = args.GetMultipleFiles(1).FirstOrDefault();
            if (file == null)
            {
                return;
            }

            if (!this.Whisper.IsLoaded)
            {
                this.LastActionMessage = "Load a Whisper model first.";
                this.RequestUiRefresh();
                return;
            }

            string extension = Path.GetExtension(file.Name);
            string tempDir = Path.Combine(Path.GetTempPath(), "SharpestLlmStudio", "audio-upload");
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, $"audio_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}{extension}");

            this.IsBusy = true;
            this.LastActionMessage = $"Uploading audio file: {file.Name}...";
            this.RequestUiRefresh();

            try
            {
                await using (var stream = file.OpenReadStream(512L * 1024L * 1024L))
                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await stream.CopyToAsync(fs);
                }

                this.LastActionMessage = $"Transcribing audio file: {file.Name}...";
                this.RequestUiRefresh();

                string? lang = string.Equals(this.WhisperLanguage, "auto", StringComparison.OrdinalIgnoreCase) ? null : this.WhisperLanguage;
                await this.StreamTranscriptionToUserInputAsync(this.Whisper.TranscribeFileAsyncEnumerable(tempPath, lang, this.WhisperTimestamps, this.WhisperSpeakers));
                this.LastActionMessage = $"Audio transcription complete: {file.Name}";
            }
            catch (Exception ex)
            {
                this.LastActionMessage = $"Audio transcription failed: {ex.Message}";
                await StaticLogger.LogAsync(ex, "[Whisper] Audio file transcription error");
            }
            finally
            {
                this.IsBusy = false;
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }

                this.RequestUiRefresh();
            }
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
                this.Whisper.GetWhisperModels();

                // this.DirectMlDevices = await this.Client.GetDirectMlDevicesAsync();
                this.ContextSize = this.Settings.DefaultContextSize;

                this.SelectedModelName = this.LlamaModels.FirstOrDefault(m => m.Name.Equals(this.Settings.DefaultModel, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? this.LlamaModels.FirstOrDefault(m => m.Name.Contains(this.Settings.DefaultModel, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? this.LlamaModels.FirstOrDefault()?.Name;

                this.GenMaxTokens = this.Settings.DefaultMaxTokens;
                this.GenBatchSize = this.Settings.DefaultBatchSize;
                this.GenUBatchSize = this.Settings.DefaultUBatchSize;
                this.GenTemperature = (float) this.Settings.DefaultTemperature;
                this.GenTopP = (float) this.Settings.DefaultTopP;
                this.GenTopK = this.Settings.DefaultTopK;
                this.GenRepetitionPenalty = this.Settings.DefaultRepetitionPenalty;
                this.SelectedWhisperModelName = this.WhisperModels.FirstOrDefault()?.ModelName;


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
                this.CollapseManagementPanels(collapseContext: true);
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
            if (success)
            {
                this.CollapseManagementPanels(collapseContext: true);
            }

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
                this.CollapseManagementPanels(collapseContext: true);
            }

            await this.RefreshContextAsync();
            this.RequestUiRefresh();
        }

        public async Task AddKnowledgeAsync()
        {
            if (this.IsKnowledgeBusy || string.IsNullOrWhiteSpace(this.KnowledgeKey) || string.IsNullOrWhiteSpace(this.KnowledgeContent))
            {
                return;
            }

            string key = this.KnowledgeKey.Trim();
            string content = this.KnowledgeContent.Trim();

            await this.RunKnowledgeOperationAsync("Knowledge Base is being vectorized...", async ct =>
            {
                int totalChunks = Math.Max(1, this.UseKnowledgeRagV2
                    ? this.Client.GetKnowledgeChunkCountV2(content, this.KnowledgeChunkSizeForRagV2)
                    : this.Client.GetKnowledgeChunkCount(content, this.KnowledgeChunkSizeForLegacy));
                int completedChunks = 0;
                this.UpdateKnowledgeProgress(0, totalChunks, key);

                if (this.UseKnowledgeRagV2)
                {
                    _ = await this.Client.UpsertKnowledgeV2Async(
                        key,
                        content,
                        cancellationToken: ct,
                        chunkSize: this.KnowledgeChunkSizeForRagV2,
                        progressCallback: currentItem =>
                        {
                            int done = System.Threading.Interlocked.Increment(ref completedChunks);
                            this.UpdateKnowledgeProgress(done, totalChunks, currentItem);
                        });
                }
                else
                {
                    _ = await this.Client.UpsertKnowledgeAsync(
                        key,
                        content,
                        cancellationToken: ct,
                        progressCallback: currentItem =>
                        {
                            int done = System.Threading.Interlocked.Increment(ref completedChunks);
                            this.UpdateKnowledgeProgress(done, totalChunks, currentItem);
                        },
                        chunkSize: this.KnowledgeChunkSizeForLegacy);
                }

                this.UpdateKnowledgeProgress(totalChunks, totalChunks, key);
                this.LastActionMessage = $"Knowledge upserted: {key}";
                this.KnowledgeKey = string.Empty;
                this.KnowledgeContent = string.Empty;
                this.CollapseManagementPanels(collapseKnowledge: true);
                this.RefreshKnowledgeEntriesFromClient();
            });
        }

        public async Task AddKnowledgeFromFilesAsync(IEnumerable<IBrowserFile> files, CancellationToken cancellationToken = default)
        {
            if (this.IsKnowledgeBusy || files == null)
            {
                return;
            }

            await this.RunKnowledgeOperationAsync("Knowledge files are being imported and embedded...", async ct =>
            {
                var workItems = new List<KnowledgeImportWorkItem>();
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        using var stream = file.OpenReadStream(50 * 1024 * 1024, ct);
                        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                        string content = await reader.ReadToEndAsync(ct);
                        if (string.IsNullOrWhiteSpace(content))
                        {
                            continue;
                        }

                        string key = Path.GetFileName(file.Name);
                        int chunkCount = this.UseKnowledgeRagV2
                            ? Math.Max(1, this.Client.GetKnowledgeChunkCountV2(content, this.KnowledgeChunkSizeForRagV2))
                            : Math.Max(1, this.Client.GetKnowledgeChunkCount(content, this.KnowledgeChunkSizeForLegacy));
                        workItems.Add(new KnowledgeImportWorkItem(key, content, file.Name, chunkCount));
                    }
                    catch (Exception ex)
                    {
                        await StaticLogger.LogAsync($"[HomeViewModel] Could not import knowledge file '{file.Name}': {ex.Message}");
                    }
                }

                if (workItems.Count == 0)
                {
                    this.LastActionMessage = "No knowledge files were imported.";
                    this.RefreshKnowledgeEntriesFromClient();
                    return;
                }

                int totalChunks = workItems.Sum(w => w.ChunkCount);
                int completedChunks = 0;
                int added = 0;
                this.UpdateKnowledgeProgress(0, totalChunks, workItems[0].Key);

                int maxParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, Math.Min(4, workItems.Count));
                var parallelOptions = new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = maxParallelism
                };

                await Parallel.ForEachAsync(workItems, parallelOptions, async (item, ct) =>
                {
                    try
                    {
                        if (this.UseKnowledgeRagV2)
                        {
                            await this.Client.UpsertKnowledgeV2Async(
                                item.Key,
                                item.Content,
                                item.SourcePath,
                                ct,
                                this.KnowledgeChunkSizeForRagV2,
                                currentItem =>
                                {
                                    int done = System.Threading.Interlocked.Increment(ref completedChunks);
                                    this.UpdateKnowledgeProgress(done, totalChunks, currentItem);
                                });
                        }
                        else
                        {
                            await this.Client.UpsertKnowledgeAsync(
                                item.Key,
                                item.Content,
                                item.SourcePath,
                                ct,
                                currentItem =>
                                {
                                    int done = System.Threading.Interlocked.Increment(ref completedChunks);
                                    this.UpdateKnowledgeProgress(done, totalChunks, currentItem);
                                },
                                this.KnowledgeChunkSizeForLegacy);
                        }

                        System.Threading.Interlocked.Increment(ref added);
                    }
                    catch (Exception ex)
                    {
                        await StaticLogger.LogAsync($"[HomeViewModel] Could not import knowledge file '{item.SourcePath}': {ex.Message}");
                    }
                });

                this.UpdateKnowledgeProgress(totalChunks, totalChunks, string.Empty);

                this.LastActionMessage = added > 0
                    ? $"Imported {added} knowledge file(s)."
                    : "No knowledge files were imported.";

                if (added > 0)
                {
                    this.CollapseManagementPanels(collapseKnowledge: true);
                }

                this.RefreshKnowledgeEntriesFromClient();
            });
        }

        public async Task SearchKnowledgeAsync()
        {
            if (string.IsNullOrWhiteSpace(this.KnowledgeQuery))
            {
                this.KnowledgeResults = [];
                this.RequestUiRefresh();
                return;
            }

            if (this.IsKnowledgeBusy)
            {
                return;
            }

            string query = this.KnowledgeQuery.Trim();
            await this.RunKnowledgeOperationAsync("Knowledge Base is being searched...", async ct =>
            {
                try
                {
                    if (this.UseKnowledgeRagV2)
                    {
                        var resultsV2 = await this.Client.SearchKnowledgeV2Async(query, this.KnowledgeTopK, cancellationToken: ct);
                        this.KnowledgeResults = MapKnowledgeResultsV2ToUi(resultsV2);
                    }
                    else
                    {
                        this.KnowledgeResults = await this.Client.SearchKnowledgeAsync(query, this.KnowledgeTopK, cancellationToken: ct);
                    }

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
            });
        }

        public async Task SaveKnowledgeStoreAsync()
        {
            if (this.IsKnowledgeBusy)
            {
                return;
            }

            await this.RunKnowledgeOperationAsync("Knowledge Store is being saved...", async ct =>
            {
                string filePath = this.UseKnowledgeRagV2
                    ? await this.Client.SaveKnowledgeStoreV2Async(cancellationToken: ct)
                    : await this.Client.SaveKnowledgeStoreAsync(cancellationToken: ct);
                this.LastActionMessage = $"Knowledge store saved: {Path.GetFileName(filePath)}";
                this.CollapseManagementPanels(collapseKnowledge: true);
            });
        }

        public void ClearKnowledgeStore()
        {
            if (this.IsKnowledgeBusy)
            {
                return;
            }

            if (this.UseKnowledgeRagV2)
            {
                this.Client.ClearKnowledgeStoreV2();
            }
            else
            {
                this.Client.ClearKnowledgeStore();
            }

            this.KnowledgeEntries = [];
            this.KnowledgeResults = [];
            this.KnowledgeKey = string.Empty;
            this.KnowledgeContent = string.Empty;
            this.KnowledgeQuery = string.Empty;
            this.ResetKnowledgeProgress();
            this.LastActionMessage = "Knowledge store cleared.";
            this.CollapseManagementPanels(collapseKnowledge: true);
            this.RequestUiRefresh();
        }

        public async Task DeleteKnowledgeByKeyAsync(string baseKey)
        {
            if (this.IsKnowledgeBusy || string.IsNullOrWhiteSpace(baseKey))
            {
                return;
            }

            await this.RunKnowledgeOperationAsync("Knowledge entry is being removed...", async ct =>
            {
                try
                {
                    if (this.UseKnowledgeRagV2)
                    {
                        this.Client.DeleteKnowledgeBySourceKeyV2(baseKey);
                    }
                    else
                    {
                        var snapshot = this.Client.GetKnowledgeEntriesSnapshot().ToList();
                        var remaining = snapshot.Where(k =>
                        {
                            var idx = k.Key.IndexOf(" [chunk ", StringComparison.OrdinalIgnoreCase);
                            var bk = idx >= 0 ? k.Key.Substring(0, idx) : k.Key;
                            return !string.Equals(bk, baseKey, StringComparison.OrdinalIgnoreCase);
                        }).ToList();

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
                            await this.Client.UpsertKnowledgeAsync(key, combined, source, ct);
                        }
                    }

                    this.RefreshKnowledgeEntriesFromClient();
                    this.LastActionMessage = $"Removed knowledge: {baseKey}";
                    this.CollapseManagementPanels(collapseKnowledge: true);
                }
                catch (Exception ex)
                {
                    await StaticLogger.LogAsync(ex, "[HomeViewModel] Error deleting knowledge by key");
                }
            });
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

            // Reset conversation UI state (mirrors ResetConversationAsync)
            this.GeneratedOutput = string.Empty;
            this.ChatMessages = [];
            this.ResetKnowledgeBaseState();
            this.IsCurrentContextSaved = false;
            this.SelectedContextFilePath = null;
            this.ContextSaveName = string.Empty;
            GenerationStats.ResetAccumulatedTotals();

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
        private sealed record KnowledgeImportWorkItem(string Key, string Content, string SourcePath, int ChunkCount);
        public sealed record ContextFileDisplayItem(string FullPath, string DisplayName);

        private void RequestUiRefresh()
        {
            this.RaiseStateChanged();
        }

        private void RefreshKnowledgeEntriesFromClient()
        {
            try
            {
                this.KnowledgeEntries = this.UseKnowledgeRagV2
                    ? this.Client.GetKnowledgeEntriesV2Snapshot()
                    : this.Client.GetKnowledgeEntriesSnapshot();
            }
            catch
            {
                this.KnowledgeEntries = [];
            }

            this.RequestUiRefresh();
        }

        private void ResetKnowledgeBaseState()
        {
            this.Client.ClearKnowledgeStore();
            this.Client.ClearKnowledgeStoreV2();
            this.KnowledgeEntries = [];
            this.KnowledgeResults = [];
            this.KnowledgeKey = string.Empty;
            this.KnowledgeContent = string.Empty;
            this.KnowledgeQuery = string.Empty;
            this.ResetKnowledgeProgress();
        }

        private void UpdateKnowledgeProgress(int completed, int total, string? currentItem)
        {
            int safeTotal = Math.Max(1, total);
            int safeCompleted = Math.Clamp(completed, 0, safeTotal);
            this.KnowledgeProgressPercent = (int)Math.Round((safeCompleted * 100.0) / safeTotal);
            this.KnowledgeProgressCurrentItem = currentItem?.Trim() ?? string.Empty;
            this.RequestUiRefresh();
        }

        private void ResetKnowledgeProgress()
        {
            this.KnowledgeProgressPercent = 0;
            this.KnowledgeProgressCurrentItem = string.Empty;
            this.KnowledgeElapsedText = "00:00";
        }

        private IReadOnlyList<LlamaKnowledgeSearchResult> MapKnowledgeResultsV2ToUi(IReadOnlyList<LlamaKnowledgeSearchResultV2> results)
        {
            return results.Select(r => new LlamaKnowledgeSearchResult
            {
                Entry = new LlamaKnowledgeEntry
                {
                    Id = r.Chunk.Id,
                    Key = $"{r.Chunk.SourceKey} [{r.Chunk.CitationId}]",
                    Content = r.Chunk.Content,
                    SourcePath = r.Chunk.SourcePath,
                    Vector = r.Chunk.Vector,
                    CreatedAtUtc = r.Chunk.CreatedAtUtc
                },
                Similarity = r.FinalScore
            }).ToList();
        }

        private string? BuildEffectiveSystemPrompt(string? additionalInstructions)
        {
            string? basePrompt = this.BuildEffectiveSystemPrompt();
            if (string.IsNullOrWhiteSpace(additionalInstructions))
            {
                return basePrompt;
            }

            return string.IsNullOrWhiteSpace(basePrompt)
                ? additionalInstructions.Trim()
                : basePrompt.Trim() + "\n\n" + additionalInstructions.Trim();
        }

        public void CancelKnowledgeOperation()
        {
            try
            {
                this.knowledgeOperationCts?.Cancel();
            }
            catch
            {
            }
        }

        private void StartKnowledgeElapsedTimer()
        {
            this.knowledgeOperationStopwatch = Stopwatch.StartNew();
            this.KnowledgeElapsedText = "00:00";
            this.knowledgeElapsedTimer?.Dispose();
            this.knowledgeElapsedTimer = new System.Threading.Timer(_ =>
            {
                var stopwatch = this.knowledgeOperationStopwatch;
                if (stopwatch == null)
                {
                    return;
                }

                TimeSpan elapsed = stopwatch.Elapsed;
                this.KnowledgeElapsedText = elapsed.TotalHours >= 1
                    ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                    : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
                this.RequestUiRefresh();
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        private void StopKnowledgeElapsedTimer()
        {
            this.knowledgeElapsedTimer?.Dispose();
            this.knowledgeElapsedTimer = null;
            this.knowledgeOperationStopwatch?.Stop();
            this.knowledgeOperationStopwatch = null;
        }

        private async Task RunKnowledgeOperationAsync(string busyMessage, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            this.knowledgeOperationCts?.Dispose();
            this.knowledgeOperationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.IsKnowledgeBusy = true;
            this.KnowledgeBusyMessage = busyMessage;
            this.ResetKnowledgeProgress();
            this.StartKnowledgeElapsedTimer();
            this.RequestUiRefresh();

            try
            {
                await operation(this.knowledgeOperationCts.Token);
            }
            catch (OperationCanceledException)
            {
                this.LastActionMessage = "Knowledge operation canceled.";
            }
            finally
            {
                this.StopKnowledgeElapsedTimer();
                this.IsKnowledgeBusy = false;
                this.KnowledgeBusyMessage = string.Empty;
                this.knowledgeOperationCts?.Dispose();
                this.knowledgeOperationCts = null;
                this.ResetKnowledgeProgress();
                this.RequestUiRefresh();
            }
        }

        private async Task ForceScrollToBottomAsync()
        {
            if (!this.AutoScrollEnabled)
            {
                return;
            }

            try
            {
                await this.Js.InvokeVoidAsync("sharpestNavMenu.scrollToBottom", ChatOutputElementId);
            }
            catch { }
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

        public async Task OnFirstRenderAsync(DotNetObjectReference<HomeViewModel> vmRef, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return;

            await this.InitializeAsync();
            if (cancellationToken.IsCancellationRequested) return;
            await this.LoadPanelStatesAsync();
            this._panelStateLoaded = true;
            this._lastLoadedState = this.IsLoaded;

            try
            {
                if (cancellationToken.IsCancellationRequested) return;
                await this.Js.InvokeVoidAsync("sharpestNavMenu.setupPromptEnter", "promptInput", vmRef);
                if (cancellationToken.IsCancellationRequested) return;
                await this.Js.InvokeVoidAsync("sharpestNavMenu.setupClipboardImagePaste", "promptInput", vmRef);
                if (cancellationToken.IsCancellationRequested) return;
                await this.Js.InvokeVoidAsync("sharpestNavMenu.setupConditionalAutoScroll", ChatOutputElementId, 0.1);
                if (cancellationToken.IsCancellationRequested) return;
                await this.Js.InvokeVoidAsync("sharpestNavMenu.setupScrollToBottomButton", ChatOutputElementId, "chat-scroll-bottom-button");
                if (cancellationToken.IsCancellationRequested) return;
                await this.Js.InvokeVoidAsync("sharpestNavMenu.setupThinkBlocks", ChatOutputElementId);
                if (cancellationToken.IsCancellationRequested) return;
                // Initialize vertical resize handle so user can drag top panels down/up. Provide minHeight and default.
                await this.Js.InvokeVoidAsync("sharpestNavMenu.setupVerticalResizeHandle", TopPanelsResizeHandleElementId, TopPanelsContentElementId, 140, 900);
                if (cancellationToken.IsCancellationRequested) return;
                await this.Js.InvokeVoidAsync("sharpestNavMenu.setupMicButton", "micButton", vmRef, "audioFilePicker");
                if (this.AutoScrollEnabled)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    await this.Js.InvokeVoidAsync("sharpestNavMenu.scrollToBottom", ChatOutputElementId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            this._lastChatMessageCount = this.ChatMessages.Count;
        }

        public async Task OnSubsequentRenderAsync(DotNetObjectReference<HomeViewModel> vmRef, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return;
            await this.Js.InvokeVoidAsync("sharpestNavMenu.setupPromptEnter", "promptInput", vmRef);
            if (cancellationToken.IsCancellationRequested) return;
            await this.Js.InvokeVoidAsync("sharpestNavMenu.setupClipboardImagePaste", "promptInput", vmRef);
            if (cancellationToken.IsCancellationRequested) return;
            await this.Js.InvokeVoidAsync("sharpestNavMenu.setupConditionalAutoScroll", ChatOutputElementId, 0.1);
            if (cancellationToken.IsCancellationRequested) return;
            await this.Js.InvokeVoidAsync("sharpestNavMenu.setupScrollToBottomButton", ChatOutputElementId, "chat-scroll-bottom-button");
            if (cancellationToken.IsCancellationRequested) return;
            await this.Js.InvokeVoidAsync("sharpestNavMenu.setupThinkBlocks", ChatOutputElementId);
            if (cancellationToken.IsCancellationRequested) return;
            await this.Js.InvokeVoidAsync("sharpestNavMenu.setupMicButton", "micButton", vmRef, "audioFilePicker");

            if (this._panelStateLoaded)
            {
                if (cancellationToken.IsCancellationRequested) return;
                await this.PersistPanelStatesAsync();
            }

            if (this._lastLoadedState != this.IsLoaded)
            {
                this._lastLoadedState = this.IsLoaded;
                if (cancellationToken.IsCancellationRequested) return;
                await this.PersistPanelStatesAsync();
            }

            if (this.AutoScrollEnabled && (this.ChatMessages.Count != this._lastChatMessageCount || this.IsGenerating))
            {
                this._lastChatMessageCount = this.ChatMessages.Count;
                if (cancellationToken.IsCancellationRequested) return;
                await this.Js.InvokeVoidAsync("sharpestNavMenu.autoScrollIfSticky", ChatOutputElementId);
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
            // When opening generation settings, collapse the top management panels to focus the content area
            if (this.GenSettingsExpanded)
            {
                try { this.TopPanelsExpanded = false; } catch { }
            }
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

        public async Task BrowseAudioFilesClickAsync()
        {
            await this.Js.InvokeVoidAsync("sharpestNavMenu.triggerClick", "audioFilePicker");
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

        // ---- JSON + Image feature support ----
        public string JsonImageInput { get; set; } = string.Empty;
        public bool JsonImageInputHasError { get; private set; } = false;
        public string? JsonImageFileName { get; private set; }
        private string? jsonImageTempFilePath;
        public string? RenderedJsonImageDataUrl { get; private set; }
        public string JsonRenderColor { get; set; } = "#ff0000";
        public int JsonRenderStrokeWidth { get; set; } = 3;
        public bool JsonRenderLabels { get; set; } = false;

        public async Task BrowseJsonImageClickAsync()
        {
            try
            {
                await this.Js.InvokeVoidAsync("sharpestNavMenu.triggerClick", "jsonImagePicker");
            }
            catch
            {
                // ignore
            }
        }

        public async Task OnJsonImageSelectedAsync(InputFileChangeEventArgs args)
        {
            if (args == null) return;
            var file = args.GetMultipleFiles(1).FirstOrDefault();
            if (file == null) return;

            try
            {
                var wwwroot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot"));
                var tempDir = Path.Combine(wwwroot, "temp");
                Directory.CreateDirectory(tempDir);

                // If there is an existing temporary image, delete it (we replace with the new upload)
                try
                {
                    if (!string.IsNullOrWhiteSpace(this.jsonImageTempFilePath) && File.Exists(this.jsonImageTempFilePath))
                    {
                        File.Delete(this.jsonImageTempFilePath);
                    }
                }
                catch
                {
                    // ignore deletion failures
                }

                string safeFileName = Path.GetFileName(file.Name);
                string guid = Guid.NewGuid().ToString("N");
                string tempFileName = guid + "_" + safeFileName;
                string tempFilePath = Path.Combine(tempDir, tempFileName);

                using var stream = file.OpenReadStream(200 * 1024 * 1024);
                using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                await stream.CopyToAsync(fs);

                this.jsonImageTempFilePath = tempFilePath;
                this.JsonImageFileName = safeFileName;
                // keep the uploaded file and path around so user can re-render multiple times
                // clear any previous rendered result; user may re-render against same image or with new JSON
                this.RenderedJsonImageDataUrl = null;
                this.RequestUiRefresh();
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "OnJsonImageSelectedAsync");
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task RenderJsonImageAsync()
        {
            if (this.IsGenerating) return;

            this.JsonImageInputHasError = false;
            this.RenderedJsonImageDataUrl = null;
            this.IsGenerating = true;
            this.RequestUiRefresh();

            string? tempPath = this.jsonImageTempFilePath;
            if (string.IsNullOrWhiteSpace(tempPath) || !File.Exists(tempPath))
            {
                this.LastActionMessage = "No image selected.";
                this.IsGenerating = false;
                this.RequestUiRefresh();
                return;
            }

            JsonDocument? jsonDoc = null;

            string inputText = this.JsonImageInput ?? string.Empty;
            if (string.IsNullOrWhiteSpace(inputText))
            {
                this.JsonImageInputHasError = true;
                this.LastActionMessage = "Empty JSON input.";
                this.IsGenerating = false;
                this.RequestUiRefresh();
                return;
            }

            // Try to locate and extract the first JSON object/array from the provided text.
            static string? TryExtractJson(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                // Find first opening bracket '{' or '['
                int start = -1;
                char open = '\0';
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '{' || text[i] == '[')
                    {
                        start = i;
                        open = text[i];
                        break;
                    }
                }

                if (start == -1) return null;

                char close = open == '{' ? '}' : ']';
                int depth = 0;
                int end = -1;

                for (int i = start; i < text.Length; i++)
                {
                    if (text[i] == open) depth++;
                    else if (text[i] == close) depth--;

                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }

                if (end == -1 || end <= start) return null;

                var candidate = text.Substring(start, end - start + 1).Trim();
                return candidate.Length > 0 ? candidate : null;
            }

            string? extracted = TryExtractJson(inputText);
            if (extracted == null)
            {
                this.JsonImageInputHasError = true;
                this.LastActionMessage = "Invalid JSON input.";
                this.IsGenerating = false;
                this.RequestUiRefresh();
                return;
            }

            try
            {
                jsonDoc = JsonDocument.Parse(extracted, new JsonDocumentOptions { AllowTrailingCommas = true });
            }
            catch (JsonException)
            {
                this.JsonImageInputHasError = true;
                this.LastActionMessage = "Invalid JSON input.";
                this.IsGenerating = false;
                this.RequestUiRefresh();
                return;
            }

            try
            {
                var base64 = await ImageHandling.DrawJsonRectanglesOnImageFileAsync(tempPath, jsonDoc, this.JsonRenderColor, Math.Max(1, this.JsonRenderStrokeWidth), this.JsonRenderLabels);
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    this.RenderedJsonImageDataUrl = "data:image/png;base64," + base64;
                    this.LastActionMessage = "Rendered image successfully.";
                }
                else
                {
                    this.LastActionMessage = "Failed to render image.";
                }
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "RenderJsonImageAsync");
                this.LastActionMessage = "Error during rendering.";
            }
            finally
            {
                // Do NOT delete or clear the uploaded image here. Keep the temp file and name
                // so the user can re-render multiple times. Clearing/removal is handled by ClearJsonImageAsync.
                this.IsGenerating = false;
                this.RequestUiRefresh();
            }
        }

        public async Task ClearJsonImageAsync()
        {
            // Explicitly remove the uploaded temp file and clear UI state when user requests it.
            try
            {
                if (!string.IsNullOrWhiteSpace(this.jsonImageTempFilePath) && File.Exists(this.jsonImageTempFilePath))
                {
                    try { File.Delete(this.jsonImageTempFilePath); } catch { }
                }
            }
            catch { }

            this.jsonImageTempFilePath = null;
            this.JsonImageFileName = null;
            this.RenderedJsonImageDataUrl = null;
            this.RequestUiRefresh();
            await Task.CompletedTask;
        }

        public string RenderChatContent(string content)
        {
            string displayContent = StaticLogics.GetDisplayContent(content ?? string.Empty);
            return StaticLogics.RenderMarkdownOrJson(displayContent);
        }

        private void DetectPendingAgentActions(string assistantText)
        {
            // Do not detect or queue tool actions while a generation is still running.
            if (this.IsGenerating || string.IsNullOrWhiteSpace(assistantText))
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
                this.LastActionMessage = $"Agent action detected: command '{safety}' is awaiting confirmation.";
            }

            if (this.EnableWebSearchAgentMode && this.PendingWebSearchRequest == null
                && this.Client.TryExtractWebSearchRequest(assistantText, out var webRequest)
                && webRequest != null)
            {
                this.PendingWebSearchRequest = webRequest;
                this.LastActionMessage = this.PendingCommandRequest != null
                    ? "Agent actions detected: command + web search awaiting confirmation."
                    : "Agent action detected: web search awaiting confirmation.";
            }
        }

        [SupportedOSPlatform("windows")]
        private async Task TryAutoExecuteAllowedNonAdminCommandAsync()
        {
            if (!this.AllowAllNonAdminCommands || this.PendingCommandRequest == null || this.IsGenerating)
            {
                return;
            }

            this.IsAgentActionRunning = true;
            this.RequestUiRefresh();
            try
            {

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
            var result = await this.Client.ExecuteCommandAsync(request, allowElevated: allowElevated, timeout: TimeSpan.FromSeconds(30), cancellationToken: this.generationCts?.Token ?? CancellationToken.None);
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
            finally
            {
                this.IsAgentActionRunning = false;
                this.RequestUiRefresh();
            }
        }

        [SupportedOSPlatform("windows")]
        private async Task TryAutoExecuteWebSearchAsync()
        {
            if (!this.AutoAllowWebSearch || this.PendingWebSearchRequest == null || this.IsGenerating)
            {
                return;
            }

            this.IsAgentActionRunning = true;
            this.RequestUiRefresh();
            try
            {

            var request = this.PendingWebSearchRequest;
            this.PendingWebSearchRequest = null;

            this.LastActionMessage = request.IsDirectUrl
                ? $"WebSearch auto-executed URL: {request.Url}"
                : $"WebSearch auto-executed query: {request.Query}";
            this.RequestUiRefresh();

            var result = await this.Client.ExecuteWebSearchAsync(request, this.generationCts?.Token ?? CancellationToken.None);
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
            finally
            {
                this.IsAgentActionRunning = false;
                this.RequestUiRefresh();
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
                this.LastActionMessage = $"Command blocked: {safety.Reason}";
                this.RequestUiRefresh();
                return;
            }

            bool allowElevated = false;
            if (safety.RequiresAdditionalConfirmation)
            {
                bool confirmed = await this.Js.InvokeAsync<bool>(
                    "confirm",
                    $"Potentially elevated command detected ({safety.SafetyLevel}).\n\nCommand:\n{request.Command}\n\nReason:\n{safety.Reason}\n\nExecute anyway?");

                if (!confirmed)
                {
                    this.LastActionMessage = "Elevated command was rejected by the user.";
                    this.RequestUiRefresh();
                    return;
                }

                allowElevated = true;
            }

            request.ShowWindow = this.AgentShowCommandWindow;
            this.LastActionMessage = $"Executing command ({safety.SafetyLevel}): {request.Command}";
            this.RequestUiRefresh();

            this.IsAgentActionRunning = true;
            this.RequestUiRefresh();
            try
            {
                var result = await this.Client.ExecuteCommandAsync(request, allowElevated, TimeSpan.FromSeconds(30), this.generationCts?.Token ?? CancellationToken.None);
                string injection = this.Client.BuildCommandResultInjectionPrompt(result);
                this.UserInput = AppendPromptForAgent(this.UserInput, injection);
                this.LastActionMessage = result.Success
                    ? "Command executed. Result was appended to the prompt."
                    : $"Command failed/blocked: {result.ErrorMessage ?? "Unknown error"}";

                this.RequestUiRefresh();

                if (this.AutoContinueAgentActions && this.IsLoaded && !this.IsGenerating && !string.IsNullOrWhiteSpace(this.UserInput))
                {
                    await this.StartGenerationAsync();
                }
            }
            finally
            {
                this.IsAgentActionRunning = false;
                this.RequestUiRefresh();
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
            this.LastActionMessage = "Command execution was rejected.";
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
                ? $"Fetching URL: {request.Url}"
                : $"Starting web search: {request.Query}";
            this.RequestUiRefresh();

            this.IsAgentActionRunning = true;
            this.RequestUiRefresh();
            try
            {
                var result = await this.Client.ExecuteWebSearchAsync(request, this.generationCts?.Token ?? CancellationToken.None);
                string injection = this.Client.BuildWebSearchResultInjectionPrompt(result);
                this.UserInput = AppendPromptForAgent(this.UserInput, injection);
                this.LastActionMessage = result.Success
                    ? "Web search completed. Result was appended to the prompt."
                    : $"Web search failed: {result.ErrorMessage ?? "Unknown error"}";

                this.RequestUiRefresh();

                if (this.AutoContinueAgentActions && this.IsLoaded && !this.IsGenerating && !string.IsNullOrWhiteSpace(this.UserInput))
                {
                    await this.StartGenerationAsync();
                }
            }
            finally
            {
                this.IsAgentActionRunning = false;
                this.RequestUiRefresh();
            }
        }

        public void RejectPendingWebSearch()
        {
            if (this.PendingWebSearchRequest == null)
            {
                return;
            }

            this.PendingWebSearchRequest = null;
            this.LastActionMessage = "Web search was rejected.";
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
            try
            {
                await this.Js.InvokeVoidAsync("localStorage.removeItem", ModelExpandedStorageKey);
                await this.Js.InvokeVoidAsync("localStorage.removeItem", ContextExpandedStorageKey);
                await this.Js.InvokeVoidAsync("localStorage.removeItem", KnowledgeExpandedStorageKey);
                await this.Js.InvokeVoidAsync("localStorage.removeItem", GenSettingsExpandedStorageKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
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
            this.knowledgeElapsedTimer?.Dispose();
            this.knowledgeElapsedTimer = null;
            this.knowledgeOperationCts?.Cancel();
            this.knowledgeOperationCts?.Dispose();
            this.knowledgeOperationCts = null;

            // Whisper: stop any active mic recording or live mode
            if (this.IsMicRecording)
            {
                this.Whisper.Audio.StopRecording();
                this.IsMicRecording = false;
            }
            this.Whisper.StopLiveMode();

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
                    GenerationStats.ResetAccumulatedTotals();

                    // Clear conversation and chat on unload so a fresh context is created for next model
                    this.Client.ResetConversation();
                    this.GeneratedOutput = string.Empty;
                    this.ChatMessages = [];
                    this.ResetKnowledgeBaseState();
                    this.IsCurrentContextSaved = false;
                    this.SelectedContextFilePath = null;
                    this.ContextSaveName = string.Empty;
                    this.LastGenerationStats = null;

                    await StaticLogger.LogAsync("[Blazor] Model unloaded successfully.");
                }
                else
                {
                    this.ResetKnowledgeBaseState();

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
                        BatchSize = this.GenBatchSize,
                        UBatchSize = this.GenUBatchSize,
                        UseFlashAttention = this.UseFlashAttention,
                        IncludeMmproj = this.UseMmproj,
                        UseNoWarmup = this.NoWarmup
                    };

                    await StaticLogger.LogAsync($"[Blazor] Loading model '{modelToLoad.Name}'...");
                    await StaticLogger.LogAsync($"[Blazor]   Executable : {loadRequest.ServerExecutablePath}");
                    await StaticLogger.LogAsync($"[Blazor]   ModelFile  : {modelToLoad.ModelFilePath}");
                    await StaticLogger.LogAsync($"[Blazor]   Mmproj     : {(loadRequest.IncludeMmproj ? modelToLoad.MmprojFilePath ?? "(none)" : "(disabled)")}");
                    await StaticLogger.LogAsync($"[Blazor]   Context    : {loadRequest.ContextSize}  Batch: {loadRequest.BatchSize}  UBatch: {loadRequest.UBatchSize}  FlashAttn: {loadRequest.UseFlashAttention}");
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
                        GenerationStats.ResetAccumulatedTotals();
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
                        else
                        {
                            this.LastActionMessage = $"Model loaded: {this.LoadedModel?.Name ?? modelToLoad.Name}";
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

            // No hot-word filtering here: agent prompts are controlled by BuildDefaultSystemPromptFromSettings
            // via the AgentSystemPrompts dictionary and the UI toggles. Keep baseSystemPrompt as assembled.

            if (this.Settings.AddGenerationParametesToSystemPrompt)
            {
                string genParams = $"[Generation Parameters: Temperature={this.GenTemperature:F2}, MaxTokens={this.GenMaxTokens}, ContextSize={this.ContextSize}, BatchSize={this.GenBatchSize}, TopP={this.GenTopP:F2}, TopK={this.GenTopK}, RepetitionPenalty={this.GenRepetitionPenalty:F2}]";
                baseSystemPrompt = string.IsNullOrWhiteSpace(baseSystemPrompt)
                    ? genParams
                    : baseSystemPrompt.Trim() + "\n\n" + genParams;
            }

            if (this.Settings.AddCurrentDateTimeToSystemPrompt)
            {
                string currentDateTime = $"[Current Date and Time: {DateTime.Now}]";
                baseSystemPrompt = string.IsNullOrWhiteSpace(baseSystemPrompt)
                    ? currentDateTime
                    : baseSystemPrompt.Trim() + "\n\n" + currentDateTime;
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

        private static string FormatSystemPromptForDisplay(string? value)
        {
            var sentences = ExtractSystemPromptSentences(value).ToArray();
            return sentences.Length == 0
                ? string.Empty
                : string.Join(Environment.NewLine, sentences);
        }

        private static string NormalizeSystemPromptFromDisplay(string? value)
        {
            var sentences = ExtractSystemPromptSentences(value).ToArray();
            return sentences.Length == 0
                ? string.Empty
                : string.Join(" ", sentences);
        }

        private static IEnumerable<string> ExtractSystemPromptSentences(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            string normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (string line in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (Match match in Regex.Matches(line, @"[^.]+(?:\.|$)"))
                {
                    string sentence = Regex.Replace(match.Value, @"\s+", " ").Trim();
                    if (string.IsNullOrWhiteSpace(sentence))
                    {
                        continue;
                    }

                    if (sentence[^1] != '.' && sentence[^1] != '!' && sentence[^1] != '?' && sentence[^1] != ':')
                    {
                        sentence += ".";
                    }

                    yield return sentence;
                }
            }
        }

        private string BuildDefaultSystemPromptFromSettings()
        {
            var configured = this.Settings.SystemPrompts?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(EnsureSentenceEndsWithPunctuation)
                .ToList() ?? new List<string>();

            // Start with the general system prompts (non-agent-specific) from settings.
            var resultList = new List<string>(configured);

            // Append agent-specific prompts only when the corresponding agent toggles are enabled.
            if (this.Settings.AgentSystemPrompts != null)
            {
                foreach (var kv in this.Settings.AgentSystemPrompts)
                {
                    var key = (kv.Key ?? string.Empty).Trim();
                    var values = kv.Value ?? Array.Empty<string>();

                    if (string.Equals(key, "WebSearch", StringComparison.OrdinalIgnoreCase) && this.EnableWebSearchAgentMode)
                    {
                        resultList.AddRange(values.Where(s => !string.IsNullOrWhiteSpace(s)).Select(EnsureSentenceEndsWithPunctuation));
                        continue;
                    }

                    if (string.Equals(key, "Command", StringComparison.OrdinalIgnoreCase) && this.EnableCommandAgentMode)
                    {
                        resultList.AddRange(values.Where(s => !string.IsNullOrWhiteSpace(s)).Select(EnsureSentenceEndsWithPunctuation));
                        continue;
                    }
                }
            }

            // Append Vision system prompts (top-level setting) when the selected model supports mmproj
            if (this.HasMmproj && this.Settings.VisionSystemPrompts != null && this.Settings.VisionSystemPrompts.Count > 0)
            {
                resultList.AddRange(this.Settings.VisionSystemPrompts.Where(s => !string.IsNullOrWhiteSpace(s)).Select(EnsureSentenceEndsWithPunctuation));
            }

            return resultList.Count > 0
                ? string.Join(" ", resultList)
                : "You are a helpful, concise assistant.";
        }

        private string BuildToolInstructionPrompt()
        {
            var lines = new List<string>();

            if (this.EnableCommandAgentMode)
            {
                lines.Add("Only emit command requests when the user explicitly asks to execute a command.");
                lines.Add("Wrap executable command requests strictly in <commandline> and </commandline> tags.");
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
