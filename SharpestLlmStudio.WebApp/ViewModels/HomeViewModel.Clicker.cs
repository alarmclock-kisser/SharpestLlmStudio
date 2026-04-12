using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using SharpestLlmStudio.Monitoring;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.WebApp.ViewModels
{
    [SupportedOSPlatform("windows")]
    public partial class HomeViewModel
    {
        private readonly ScreenClicker Clicker;
        private readonly List<string> clickerHistory = [];
        private readonly List<ClickerProtectedZone> clickerProtectedZones = [];
        private readonly List<string> clickerPendingQuestionOptions = [];
        private enum WebChatClipboardStage
        {
            None,
            Image,
            Text,
            AwaitingResult
        }

        private ScreenClicker.MarkedWindowInfo? clickerMarkedWindow;
        private CancellationTokenSource? clickerLoopCts;
        private CancellationTokenSource? clickerPreviewRefreshCts;
        private TaskCompletionSource<string?>? clickerPendingQuestionTcs;
        private string? clickerLastScreenshotPath;
        private string clickerLastErrorFeedback = string.Empty;
        private string clickerLiveUserNote = string.Empty;
        private string webChatPreparedPromptText = string.Empty;
        private string webChatPreparedImageKey = string.Empty;
        private string webChatLastImportedResponse = string.Empty;
        private WebChatClipboardStage webChatClipboardStage = WebChatClipboardStage.None;
        private LlamaChatMessage? webChatPendingAssistantMessage;
        private CancellationTokenSource? webChatWatcherCts;
        private int clickerConsecutiveFailures;
        private const int ClickerHistoryMaxEntries = 12;
        private const string ClickerPreviewStageElementId = "clicker-preview-stage";
        private const string DefaultClickerProtectedZoneModelWarning = "Protected zones mark areas the user does not want clicked. Do not choose targets inside those protected zones.";

        public string ClickerInstructions { get; set; } = "Click the next required UI element for the described task. Prefer the primary actionable control and avoid decorative elements.";
        public string ClickerInstructionsDraft { get; set; } = "Click the next required UI element for the described task. Prefer the primary actionable control and avoid decorative elements.";
        public int ClickerLoopIntervalMs { get; set; } = 1000;
        public int ClickerWindowMarkDelaySeconds { get; set; } = 3;
        public int ClickerPreviewScalePercent { get; set; } = 30;
        public int ClickerModelImageScalePercent { get; set; } = 100;
        public bool ClickerIncludeWindowChrome { get; set; } = false;
        public bool ClickerShowPreviewMarker { get; set; } = true;
        public bool ClickerActivateWindowBeforeCapture { get; set; } = true;
        public bool ClickerUseBackgroundClick { get; set; } = true;
        public bool ClickerDryRun { get; set; } = false;
        public bool ClickerConfirmBeforeClick { get; set; } = false;
        public bool ClickerRequirePointInsideWindow { get; set; } = true;
        public bool ClickerUseChatInputAsLiveNote { get; set; } = true;
        public bool ClickerTellModelAboutProtectedZones { get; set; } = false;
        public bool ClickerSendLastErrorToModel { get; set; } = false;
        public bool ClickerAllowModelQuestions { get; set; } = true;
        public bool ClickerSafeMode { get; set; } = false;
        public bool ClickerLimitInteractionRegion { get; set; } = false;
        public int ClickerInteractionMinX { get; set; } = 0;
        public int ClickerInteractionMinY { get; set; } = 0;
        public int ClickerInteractionMaxX { get; set; } = 0;
        public int ClickerInteractionMaxY { get; set; } = 0;
        public bool IsClickerBusy { get; private set; }
        public bool IsClickerProtectedZoneSelectionActive { get; private set; }
        public bool IsClickerLoopRunning => this.clickerLoopCts != null && !this.clickerLoopCts.IsCancellationRequested;
        public string? ClickerScreenshotDataUrl { get; private set; }
        public string ClickerLastResponse { get; private set; } = string.Empty;
        public string ClickerLastParsedPoint { get; private set; } = string.Empty;
        public string ClickerLastParsedAction { get; private set; } = string.Empty;
        public string ClickerLastNormalizedJson { get; private set; } = string.Empty;
        public string ClickerLastReason { get; private set; } = string.Empty;
        public string ClickerLastTargetScreenPoint { get; private set; } = string.Empty;
        public string ClickerLastConfirmationOutcome { get; private set; } = string.Empty;
        public double? ClickerPreviewMarkerLeftPercent { get; private set; }
        public double? ClickerPreviewMarkerTopPercent { get; private set; }
        public int ClickerIterationCount { get; private set; }
        public DateTime? ClickerLastRunAtUtc { get; private set; }
        public string ClickerMarkedWindowLabel => this.clickerMarkedWindow?.DisplayLabel ?? "No window marked.";
        public string ClickerLastErrorFeedback => this.clickerLastErrorFeedback;
        public bool CanImportWebChatResponse => this.UseWebChatProvider
            && (this.webChatClipboardStage != WebChatClipboardStage.None
                || this.webChatPendingAssistantMessage != null
                || !string.IsNullOrWhiteSpace(this.webChatPreparedPromptText));
        public bool HasPendingClickerQuestion => this.clickerPendingQuestionTcs != null && (this.clickerPendingQuestionOptions.Count > 0 || this.ClickerPendingQuestionAddTextOption);
        public string ClickerPendingQuestionTitle { get; private set; } = string.Empty;
        public string ClickerPendingQuestionText { get; private set; } = string.Empty;
        public string ClickerPendingQuestionKind { get; private set; } = "question";
        public bool ClickerPendingQuestionAddTextOption { get; private set; }
        public string ClickerPendingQuestionTextLabel { get; private set; } = "Your answer";
        public string ClickerPendingQuestionTextPlaceholder { get; private set; } = string.Empty;
        public string ClickerPendingQuestionSubmitText { get; private set; } = "Send answer";
        public string ClickerPendingQuestionTextAnswer { get; set; } = string.Empty;
        public IEnumerable<string> ClickerPendingQuestionOptions => this.clickerPendingQuestionOptions;
        public string ClickerPendingQuestionIcon => this.ClickerPendingQuestionKind switch
        {
            "warning" => "warning",
            "danger" or "error" => "error",
            "success" => "check_circle",
            "info" => "info",
            _ => "help"
        };
        public string ClickerPendingQuestionAccentColor => this.ClickerPendingQuestionKind switch
        {
            "warning" => "#f59e0b",
            "danger" or "error" => "#dc2626",
            "success" => "#16a34a",
            "info" => "#2563eb",
            _ => "#7c3aed"
        };
        public string ClickerPendingQuestionSurfaceColor => this.ClickerPendingQuestionKind switch
        {
            "warning" => "#fff7ed",
            "danger" or "error" => "#fef2f2",
            "success" => "#f0fdf4",
            "info" => "#eff6ff",
            _ => "#f5f3ff"
        };
        public bool HasClickerPreviewMarker => this.ClickerPreviewMarkerLeftPercent.HasValue && this.ClickerPreviewMarkerTopPercent.HasValue;
        public IEnumerable<ClickerProtectedZone> ClickerProtectedZones => this.clickerProtectedZones
            .OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase);
        public IEnumerable<ClickerProtectedZone> VisibleClickerProtectedZones => this.clickerProtectedZones
            .Where(z => z.IncludeWindowChrome == this.ClickerIncludeWindowChrome)
            .OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase);
        public int ClickerProtectedZoneCount => this.clickerProtectedZones.Count;
        public string ClickerInteractionRegionLabel
        {
            get
            {
                if (this.clickerMarkedWindow == null)
                {
                    return "No window marked.";
                }

                Rectangle bounds = this.GetClickerEffectiveReferenceBounds(this.clickerMarkedWindow, this.ClickerIncludeWindowChrome);
                return $"{bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height}";
            }
        }

        public bool ShowClickerInteractionZoneOverlay => false;

        public double ClickerInteractionZoneLeftPercent => this.TryGetClickerInteractionZonePercentages(out var left, out _, out _, out _)
            ? left
            : 0.0;

        public double ClickerInteractionZoneTopPercent => this.TryGetClickerInteractionZonePercentages(out _, out var top, out _, out _)
            ? top
            : 0.0;

        public double ClickerInteractionZoneWidthPercent => this.TryGetClickerInteractionZonePercentages(out _, out _, out var width, out _)
            ? width
            : 0.0;

        public double ClickerInteractionZoneHeightPercent => this.TryGetClickerInteractionZonePercentages(out _, out _, out _, out var height)
            ? height
            : 0.0;

        public void CommitClickerInstructionsDraft()
        {
            string draft = this.ClickerInstructionsDraft?.Trim() ?? string.Empty;
            if (string.Equals(this.ClickerInstructions, draft, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(draft))
            {
                this.LastActionMessage = "Clicker prompt cannot be empty.";
                this.RequestUiRefresh();
                return;
            }

            this.ClickerInstructions = draft;
            this.LastActionMessage = "Clicker prompt updated.";
            this.RequestUiRefresh();
        }

        public void SelectClickerQuestionOption(string option)
        {
            if (this.clickerPendingQuestionTcs == null)
            {
                return;
            }

            this.clickerPendingQuestionTcs.TrySetResult(option);
        }

        public void SubmitClickerQuestionTextAnswer()
        {
            if (this.clickerPendingQuestionTcs == null || string.IsNullOrWhiteSpace(this.ClickerPendingQuestionTextAnswer))
            {
                return;
            }

            this.clickerPendingQuestionTcs.TrySetResult(this.ClickerPendingQuestionTextAnswer.Trim());
        }

        public void CancelPendingClickerQuestion()
        {
            this.clickerPendingQuestionTcs?.TrySetResult(null);
            this.clickerPendingQuestionTcs = null;
            this.ClickerPendingQuestionTitle = string.Empty;
            this.ClickerPendingQuestionText = string.Empty;
            this.ClickerPendingQuestionKind = "question";
            this.ClickerPendingQuestionAddTextOption = false;
            this.ClickerPendingQuestionTextLabel = "Your answer";
            this.ClickerPendingQuestionTextPlaceholder = string.Empty;
            this.ClickerPendingQuestionSubmitText = "Send answer";
            this.ClickerPendingQuestionTextAnswer = string.Empty;
            this.clickerPendingQuestionOptions.Clear();
            this.RequestUiRefresh();
        }

        private async Task<string?> AwaitClickerQuestionAnswerAsync(ClickerUserQuestion question, CancellationToken cancellationToken)
        {
            this.clickerPendingQuestionTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.ClickerPendingQuestionTitle = string.IsNullOrWhiteSpace(question.Title) ? "Question from model" : question.Title.Trim();
            this.ClickerPendingQuestionText = question.Question.Trim();
            this.ClickerPendingQuestionKind = string.IsNullOrWhiteSpace(question.Kind) ? "question" : question.Kind.Trim().ToLowerInvariant();
            this.ClickerPendingQuestionAddTextOption = question.AddTextOption;
            this.ClickerPendingQuestionTextLabel = string.IsNullOrWhiteSpace(question.TextLabel) ? "Your answer" : question.TextLabel.Trim();
            this.ClickerPendingQuestionTextPlaceholder = question.TextPlaceholder?.Trim() ?? string.Empty;
            this.ClickerPendingQuestionSubmitText = string.IsNullOrWhiteSpace(question.SubmitText) ? "Send answer" : question.SubmitText.Trim();
            this.ClickerPendingQuestionTextAnswer = string.Empty;
            this.clickerPendingQuestionOptions.Clear();
            this.clickerPendingQuestionOptions.AddRange(question.Options);
            this.RequestUiRefresh();

            using var registration = cancellationToken.Register(() => this.clickerPendingQuestionTcs?.TrySetCanceled(cancellationToken));

            try
            {
                string? result = await this.clickerPendingQuestionTcs.Task;
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                this.clickerPendingQuestionTcs = null;
                this.ClickerPendingQuestionTitle = string.Empty;
                this.ClickerPendingQuestionText = string.Empty;
                this.clickerPendingQuestionOptions.Clear();
                this.RequestUiRefresh();
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task MarkClickerWindowAsync()
        {
            if (this.IsClickerBusy)
            {
                return;
            }

            this.LastActionMessage = "Click the target window now. The next focused window (excluding taskbar/desktop) will be marked.";
            this.RequestUiRefresh();

            var initialForeground = this.Clicker.GetCurrentForegroundWindowHandle();
            var window = await this.TryMarkForegroundWindowByNextFocusChangeAsync(initialForeground, TimeSpan.FromSeconds(20));
            if (window != null)
            {
                bool windowChanged = this.clickerMarkedWindow == null || this.clickerMarkedWindow.Handle != window.Handle;
                this.clickerMarkedWindow = window;
                this.ResetClickerInteractionRegionToReferenceBounds(window);
                if (windowChanged)
                {
                    this.ClearClickerProtectedZonesInternal();
                }
                this.LastActionMessage = $"Marked window: {window.DisplayLabel}";
                await this.RefreshClickerPreviewFromMarkedWindowAsync(CancellationToken.None);
            }
            else
            {
                this.LastActionMessage = "Could not detect a new focused window. Try again and click the target window once.";
            }

            this.RequestUiRefresh();
        }

        public async Task OnClickerIncludeWindowChromeChangedAsync(bool value)
        {
            this.ClickerIncludeWindowChrome = value;
            if (this.clickerMarkedWindow != null)
            {
                this.ResetClickerInteractionRegionToReferenceBounds(this.clickerMarkedWindow);
            }

            await this.ScheduleClickerPreviewRefreshAsync();
        }

        public async Task OnClickerLimitInteractionRegionChangedAsync(bool value)
        {
            this.ClickerLimitInteractionRegion = value;
            this.NormalizeClickerInteractionRegionInputs();
            await this.ScheduleClickerPreviewRefreshAsync();
        }

        public async Task OnClickerInteractionRegionChangedAsync(string axis, int value)
        {
            switch (axis)
            {
                case "MinX":
                    this.ClickerInteractionMinX = value;
                    break;
                case "MinY":
                    this.ClickerInteractionMinY = value;
                    break;
                case "MaxX":
                    this.ClickerInteractionMaxX = value;
                    break;
                case "MaxY":
                    this.ClickerInteractionMaxY = value;
                    break;
                default:
                    return;
            }

            this.NormalizeClickerInteractionRegionInputs();
            await this.ScheduleClickerPreviewRefreshAsync(120);
        }

        public void ClearClickerWindow()
        {
            this.clickerPreviewRefreshCts?.Cancel();
            this.clickerPreviewRefreshCts?.Dispose();
            this.clickerPreviewRefreshCts = null;
            this.Clicker.ReleaseHeldInputs();
            this.clickerMarkedWindow = null;
            this.IsClickerProtectedZoneSelectionActive = false;
            this.ClickerScreenshotDataUrl = null;
            this.ClickerLastResponse = string.Empty;
            this.ClickerLastParsedPoint = string.Empty;
            this.ClickerLastParsedAction = string.Empty;
            this.ClickerLastNormalizedJson = string.Empty;
            this.ClickerLastReason = string.Empty;
            this.ClickerLastTargetScreenPoint = string.Empty;
            this.ClickerLastConfirmationOutcome = string.Empty;
            this.clickerLastErrorFeedback = string.Empty;
            this.clickerLiveUserNote = string.Empty;
            this.clickerConsecutiveFailures = 0;
            this.ClickerPreviewMarkerLeftPercent = null;
            this.ClickerPreviewMarkerTopPercent = null;
            this.clickerHistory.Clear();
            this.ClearClickerProtectedZonesInternal();
            this.ResetClickerInteractionRegionToDefaults();
            this.CancelPendingClickerQuestion();
            try
            {
                _ = this.Js.InvokeVoidAsync("sharpestNavMenu.cancelClickerProtectedZoneSelection");
            }
            catch
            {
            }
            this.DeleteLastClickerScreenshot();
            this.LastActionMessage = "Marked window cleared.";
            this.RequestUiRefresh();
        }

        [SupportedOSPlatform("windows")]
        public async Task ToggleClickerProtectedZoneSelectionAsync(DotNetObjectReference<HomeViewModel> vmRef, CancellationToken cancellationToken = default)
        {
            if (this.IsClickerProtectedZoneSelectionActive)
            {
                await this.CancelClickerProtectedZoneSelectionAsync("Protected zone selection canceled.");
                return;
            }

            if (this.IsClickerBusy || this.IsGenerating)
            {
                return;
            }

            if (!this.ValidateClickerPrerequisites(out var window) || window == null)
            {
                return;
            }

            this.IsClickerProtectedZoneSelectionActive = true;
            this.LastActionMessage = "Drag on the screenshot preview or click once to add a protected zone.";
            this.RequestUiRefresh();

            try
            {
                var prepared = await this.TryPrepareClickerScreenshotAsync(window, cancellationToken);
                if (!prepared.Success || prepared.Window == null)
                {
                    this.IsClickerProtectedZoneSelectionActive = false;
                    this.RequestUiRefresh();
                    return;
                }

                await Task.Delay(50, cancellationToken);
                if (!await this.HasSharpestNavMenuFunctionAsync("armClickerProtectedZoneSelection"))
                {
                    this.IsClickerProtectedZoneSelectionActive = false;
                    this.LastActionMessage = "Protected zone selection is unavailable because the required browser script is missing. Reload the page.";
                    this.RequestUiRefresh();
                    await StaticLogger.LogAsync("[Clicker] Protected zone selection requested, but sharpestNavMenu.armClickerProtectedZoneSelection is unavailable. Ask the user to reload the page/browser cache.");
                    return;
                }

                bool armed = await this.Js.InvokeAsync<bool>("sharpestNavMenu.armClickerProtectedZoneSelection", ClickerPreviewStageElementId, vmRef);
                if (!armed)
                {
                    this.IsClickerProtectedZoneSelectionActive = false;
                    this.LastActionMessage = "Could not start protected zone selection.";
                    this.RequestUiRefresh();
                    await StaticLogger.LogAsync("[Clicker] Protected zone selection could not be armed on the preview stage.");
                }
            }
            catch (OperationCanceledException)
            {
                this.IsClickerProtectedZoneSelectionActive = false;
                this.LastActionMessage = "Protected zone selection canceled.";
                this.RequestUiRefresh();
            }
            catch (Exception ex)
            {
                this.IsClickerProtectedZoneSelectionActive = false;
                this.LastActionMessage = "Could not start protected zone selection.";
                this.RequestUiRefresh();
                await StaticLogger.LogAsync(ex, "[Clicker] Could not start protected zone selection");
            }
        }

        public void RemoveClickerProtectedZone(Guid zoneId)
        {
            int removed = this.clickerProtectedZones.RemoveAll(z => z.Id == zoneId);
            if (removed > 0)
            {
                this.LastActionMessage = "Protected zone removed.";
                this.RequestUiRefresh();
            }
        }

        public void RenameClickerProtectedZone(Guid zoneId, string? name)
        {
            ClickerProtectedZone? zone = this.clickerProtectedZones.FirstOrDefault(z => z.Id == zoneId);
            if (zone == null)
            {
                return;
            }

            string trimmed = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            zone.Name = trimmed;
            this.RequestUiRefresh();
        }

        [SupportedOSPlatform("windows")]
        public async Task RunClickerOnceAsync()
        {
            if (this.IsClickerBusy || this.IsGenerating)
            {
                return;
            }

            await this.ExecuteClickerIterationAsync(CancellationToken.None);
        }

        [SupportedOSPlatform("windows")]
        public async Task ToggleClickerLoopAsync()
        {
            if (this.IsClickerLoopRunning)
            {
                this.StopClickerLoop();
                return;
            }

            if (!this.ValidateClickerPrerequisites(out _))
            {
                return;
            }

            this.clickerLoopCts?.Dispose();
            this.clickerLoopCts = new CancellationTokenSource();
            CancellationTokenSource loopCts = this.clickerLoopCts ?? throw new InvalidOperationException("Clicker loop source was not initialized.");
            CancellationToken token = loopCts.Token;
            this.ClickerIterationCount = 0;
            this.clickerConsecutiveFailures = 0;
            this.LastActionMessage = "Clicker loop started.";
            this.RequestUiRefresh();

            await Task.Yield();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await this.ExecuteClickerIterationAsync(token);
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        await Task.Delay(Math.Max(250, this.ClickerLoopIntervalMs), token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    await StaticLogger.LogAsync(ex, "[Clicker] Loop failed");
                    this.LastActionMessage = "Clicker loop stopped because of an error.";
                }
                finally
                {
                    if (ReferenceEquals(this.clickerLoopCts, loopCts) && loopCts.IsCancellationRequested)
                    {
                        this.LastActionMessage = "Clicker loop stopped.";
                    }

                    if (ReferenceEquals(this.clickerLoopCts, loopCts))
                    {
                        this.clickerLoopCts?.Dispose();
                        this.clickerLoopCts = null;
                    }

                    this.RequestUiRefresh();
                }
            }, token);
        }

        public void StopClickerLoop()
        {
            try
            {
                this.clickerLoopCts?.Cancel();
            }
            catch
            {
            }

            this.Clicker.ReleaseHeldInputs();
            this.CancelPendingClickerQuestion();

            this.LastActionMessage = "Clicker loop stopping...";
            this.RequestUiRefresh();
        }

        private bool ValidateClickerPrerequisites(out ScreenClicker.MarkedWindowInfo? window)
        {
            window = null;

            if (!OperatingSystem.IsWindows())
            {
                this.LastActionMessage = "Screen Clicker is only available on Windows.";
                this.RequestUiRefresh();
                return false;
            }

            if (!this.IsLoaded)
            {
                this.LastActionMessage = this.UseWebChatProvider
                    ? "Load a multimodal local model before starting the Clicker browser automation loop."
                    : "Load a model before starting the Clicker.";
                this.RequestUiRefresh();
                return false;
            }

            bool hasVisionSupport = this.LoadedModel?.IsOmni == true
                || (this.UseMmproj && !string.IsNullOrWhiteSpace(this.LoadedModel?.MmprojFilePath));
            if (!hasVisionSupport)
            {
                this.LastActionMessage = "Clicker requires a multimodal model with mmproj enabled.";
                this.RequestUiRefresh();
                return false;
            }

            if (this.clickerMarkedWindow == null)
            {
                this.LastActionMessage = "Mark a target window first.";
                this.RequestUiRefresh();
                return false;
            }

            if (string.IsNullOrWhiteSpace(this.ClickerInstructions))
            {
                this.LastActionMessage = "Enter Clicker instructions first.";
                this.RequestUiRefresh();
                return false;
            }

            window = this.clickerMarkedWindow;
            return true;
        }

        private void ResetClickerInteractionRegionToDefaults()
        {
            this.ClickerInteractionMinX = 0;
            this.ClickerInteractionMinY = 0;
            this.ClickerInteractionMaxX = 0;
            this.ClickerInteractionMaxY = 0;
        }

        private void ResetClickerInteractionRegionToReferenceBounds(ScreenClicker.MarkedWindowInfo window)
        {
            Rectangle bounds = this.Clicker.GetReferenceBounds(window, this.ClickerIncludeWindowChrome);
            this.ClickerInteractionMinX = 0;
            this.ClickerInteractionMinY = 0;
            this.ClickerInteractionMaxX = Math.Max(1, bounds.Width);
            this.ClickerInteractionMaxY = Math.Max(1, bounds.Height);
        }

        private Rectangle GetClickerEffectiveReferenceBounds(ScreenClicker.MarkedWindowInfo window, bool includeWindowChrome)
        {
            Rectangle baseBounds = this.Clicker.GetReferenceBounds(window, includeWindowChrome);
            if (!this.ClickerLimitInteractionRegion || baseBounds.Width <= 1 || baseBounds.Height <= 1)
            {
                return baseBounds;
            }

            int minX = Math.Clamp(this.ClickerInteractionMinX, 0, Math.Max(0, baseBounds.Width - 1));
            int minY = Math.Clamp(this.ClickerInteractionMinY, 0, Math.Max(0, baseBounds.Height - 1));
            int rawMaxX = this.ClickerInteractionMaxX <= 0 ? baseBounds.Width : this.ClickerInteractionMaxX;
            int rawMaxY = this.ClickerInteractionMaxY <= 0 ? baseBounds.Height : this.ClickerInteractionMaxY;
            int maxX = Math.Clamp(rawMaxX, minX + 1, baseBounds.Width);
            int maxY = Math.Clamp(rawMaxY, minY + 1, baseBounds.Height);

            return new Rectangle(baseBounds.Left + minX, baseBounds.Top + minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        }

        private bool TryGetClickerInteractionZonePercentages(out double left, out double top, out double width, out double height)
        {
            left = 0;
            top = 0;
            width = 0;
            height = 0;

            if (this.clickerMarkedWindow == null)
            {
                return false;
            }

            Rectangle fullBounds = this.Clicker.GetReferenceBounds(this.clickerMarkedWindow, this.ClickerIncludeWindowChrome);
            Rectangle limitedBounds = this.GetClickerEffectiveReferenceBounds(this.clickerMarkedWindow, this.ClickerIncludeWindowChrome);
            if (fullBounds.Width <= 0 || fullBounds.Height <= 0)
            {
                return false;
            }

            left = Math.Clamp(((limitedBounds.Left - fullBounds.Left) / (double)fullBounds.Width) * 100.0, 0.0, 100.0);
            top = Math.Clamp(((limitedBounds.Top - fullBounds.Top) / (double)fullBounds.Height) * 100.0, 0.0, 100.0);
            width = Math.Clamp((limitedBounds.Width / (double)fullBounds.Width) * 100.0, 0.0, 100.0);
            height = Math.Clamp((limitedBounds.Height / (double)fullBounds.Height) * 100.0, 0.0, 100.0);
            return true;
        }

        private async Task<ScreenClicker.MarkedWindowInfo?> TryMarkForegroundWindowByNextFocusChangeAsync(nint initialHandle, TimeSpan timeout)
        {
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < timeout)
            {
                await Task.Delay(120);

                if (!this.Clicker.TryMarkForegroundWindow(out var candidate) || candidate == null)
                {
                    continue;
                }

                if (candidate.Handle == initialHandle)
                {
                    continue;
                }

                if (this.Clicker.IsTaskbarOrShellWindow(candidate.Handle))
                {
                    continue;
                }

                return candidate;
            }

            return null;
        }

        private void NormalizeClickerInteractionRegionInputs()
        {
            if (this.clickerMarkedWindow == null)
            {
                return;
            }

            Rectangle baseBounds = this.Clicker.GetReferenceBounds(this.clickerMarkedWindow, this.ClickerIncludeWindowChrome);
            if (baseBounds.Width <= 1 || baseBounds.Height <= 1)
            {
                return;
            }

            this.ClickerInteractionMinX = Math.Clamp(this.ClickerInteractionMinX, 0, Math.Max(0, baseBounds.Width - 1));
            this.ClickerInteractionMinY = Math.Clamp(this.ClickerInteractionMinY, 0, Math.Max(0, baseBounds.Height - 1));
            int maxX = this.ClickerInteractionMaxX <= 0 ? baseBounds.Width : this.ClickerInteractionMaxX;
            int maxY = this.ClickerInteractionMaxY <= 0 ? baseBounds.Height : this.ClickerInteractionMaxY;
            this.ClickerInteractionMaxX = Math.Clamp(maxX, this.ClickerInteractionMinX + 1, baseBounds.Width);
            this.ClickerInteractionMaxY = Math.Clamp(maxY, this.ClickerInteractionMinY + 1, baseBounds.Height);
        }

        private async Task ScheduleClickerPreviewRefreshAsync(int delayMs = 140)
        {
            this.clickerPreviewRefreshCts?.Cancel();
            this.clickerPreviewRefreshCts?.Dispose();
            this.clickerPreviewRefreshCts = new CancellationTokenSource();
            CancellationToken token = this.clickerPreviewRefreshCts.Token;

            try
            {
                await Task.Delay(Math.Max(60, delayMs), token);
                await this.RefreshClickerPreviewFromMarkedWindowAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RefreshClickerPreviewFromMarkedWindowAsync(CancellationToken cancellationToken)
        {
            if (this.clickerMarkedWindow == null)
            {
                return;
            }

            if (!this.Clicker.TryRefreshWindow(this.clickerMarkedWindow, out var refreshedWindow) || refreshedWindow == null)
            {
                return;
            }

            this.clickerMarkedWindow = refreshedWindow;

            string screenshotDirectory = Path.Combine(Path.GetTempPath(), "SharpestLlmStudio", "clicker");
            string? screenshotPath = await this.Clicker.CaptureWindowToPngAsync(refreshedWindow, screenshotDirectory, this.ClickerIncludeWindowChrome, cancellationToken);
            if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
            {
                return;
            }

            screenshotPath = await this.TryCropClickerScreenshotToInteractionRegionAsync(refreshedWindow, screenshotPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
            {
                return;
            }

            this.DeleteLastClickerScreenshot();
            this.clickerLastScreenshotPath = screenshotPath;
            this.ClickerScreenshotDataUrl = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(screenshotPath, cancellationToken));
            this.RequestUiRefresh();
        }

        private ScreenClicker.MarkedWindowInfo CreateClickerModelWindow(ScreenClicker.MarkedWindowInfo window)
        {
            Rectangle effectiveBounds = this.GetClickerEffectiveReferenceBounds(window, this.ClickerIncludeWindowChrome);
            return new ScreenClicker.MarkedWindowInfo(window.Handle, window.Title, effectiveBounds, effectiveBounds);
        }

        private Point ConvertClickerModelPointToScreen(ScreenClicker.MarkedWindowInfo window, ScreenClicker.ClickPoint point)
        {
            ScreenClicker.MarkedWindowInfo modelWindow = this.CreateClickerModelWindow(window);
            return this.Clicker.ConvertToScreenPoint(modelWindow, point, includeWindowChrome: false);
        }

        [SupportedOSPlatform("windows")]
        private async Task ExecuteClickerIterationAsync(CancellationToken cancellationToken)
        {
            if (!this.ValidateClickerPrerequisites(out var window) || window == null)
            {
                return;
            }

            this.IsClickerBusy = true;
            this.RequestUiRefresh();
            bool shouldRestorePromptFocus = false;
            nint savedForegroundWindow = IntPtr.Zero;

            try
            {
                    if (this.UseWebChatProvider)
                    {
                        await this.PrepareWebChatClickerLoopStateAsync(cancellationToken);
                    }

                if (this.ClickerUseBackgroundClick)
                {
                    try
                    {
                        shouldRestorePromptFocus = await this.Js.InvokeAsync<bool>("sharpestNavMenu.isElementFocused", "promptInput");
                    }
                    catch
                    {
                    }

                    savedForegroundWindow = this.Clicker.GetCurrentForegroundWindowHandle();
                }

                var prepared = await this.TryPrepareClickerScreenshotAsync(window, cancellationToken);
                if (!prepared.Success || prepared.Window == null)
                {
                    return;
                }

                window = prepared.Window;
                ScreenClicker.MarkedWindowInfo modelWindow = this.CreateClickerModelWindow(window);

                string? clickerSystemPrompt = this.ClickerSafeMode
                    ? null
                    : SanitizeClickerPromptText(HomeViewModel.BuildCompactClickerSystemPrompt());

                string? liveOperatorNote = null;
                if (this.ClickerUseChatInputAsLiveNote)
                {
                    string? currentInput = this.UserInput?.Trim();
                    string? persistedNote = string.IsNullOrWhiteSpace(this.clickerLiveUserNote) ? null : this.clickerLiveUserNote.Trim();
                    if (!string.IsNullOrWhiteSpace(currentInput) && !string.IsNullOrWhiteSpace(persistedNote))
                    {
                        liveOperatorNote = persistedNote + "\n" + currentInput;
                    }
                    else
                    {
                        liveOperatorNote = currentInput ?? persistedNote;
                    }

                    // Consume the persisted note so it is not repeated on subsequent iterations
                    this.clickerLiveUserNote = string.Empty;
                }
                string? effectiveOperatorNote = liveOperatorNote;
                if (this.UseWebChatProvider)
                {
                    string webChatNote = this.BuildWebChatClickerAutomationNote();
                    effectiveOperatorNote = string.IsNullOrWhiteSpace(effectiveOperatorNote)
                        ? webChatNote
                        : effectiveOperatorNote.Trim() + "\n" + webChatNote;
                }

                int questionRound = 0;
                int clickerModelMaxWidthAndHeight = this.GetClickerModelMaxWidthAndHeight(this.clickerLastScreenshotPath!);

                string responseText;
                while (true)
                {
                    string clickerPrompt = this.ClickerSafeMode
                        ? LimitClickerPromptText(SanitizeClickerPromptText(this.BuildSafeModeClickerPrompt(modelWindow)), 220)
                        : LimitClickerPromptText(SanitizeClickerPromptText(this.BuildClickerPrompt(modelWindow, effectiveOperatorNote)), 280);
                    string clickerFallbackPrompt = this.ClickerSafeMode
                        ? clickerPrompt
                        : LimitClickerPromptText(SanitizeClickerPromptText(this.BuildFallbackClickerPrompt(modelWindow, effectiveOperatorNote)), 140);
                    await StaticLogger.LogAsync($"[Clicker] Prompt sizes: system={(clickerSystemPrompt?.Length ?? 0)}, user={clickerPrompt.Length}. SafeMode={this.ClickerSafeMode}.");
                    var request = new LlamaGenerationRequest
                    {
                        Prompt = clickerPrompt,
                        SystemPrompt = clickerSystemPrompt,
                        Images = [this.clickerLastScreenshotPath!],
                        Isolated = true,
                        PersistConversation = false,
                        IncludeConversationHistory = false,
                        MaxTokens = this.ClickerSafeMode ? 96 : 128,
                        Temperature = 0.15,
                        TopP = 0.9,
                        TopK = this.ClickerSafeMode ? 4 : 8,
                        MaxWidthAndHeight = clickerModelMaxWidthAndHeight,
                        ImageFormat = "jpg",
                        Stream = false
                    };

                    responseText = await this.GenerateClickerResponseAsync(request, clickerFallbackPrompt, cancellationToken);

                    this.LastGenerationStats = this.Client.GetLastGenerationStatsSnapshot();
                    await this.UpdateHardwareStatsAsync();
                    this.ClickerLastResponse = responseText;
                    this.ClickerLastRunAtUtc = DateTime.UtcNow;
                    await StaticLogger.LogAsync($"[Clicker] Model raw response ({responseText.Length} chars): {TrimForHistory(responseText)}");

                    if (!this.ClickerAllowModelQuestions || !this.TryParseClickerUserQuestion(responseText, out ClickerUserQuestion? question) || question == null)
                    {
                        break;
                    }

                    questionRound++;
                    if (questionRound > 3)
                    {
                        this.clickerLastErrorFeedback = "Model asked too many follow-up questions in a row.";
                        this.LastActionMessage = "Clicker stopped because the model asked too many questions in a row.";
                        return;
                    }

                    this.ClickerLastParsedPoint = string.Empty;
                    this.ClickerLastParsedAction = "Question";
                    this.ClickerLastNormalizedJson = question.NormalizedJson;
                    this.ClickerLastReason = question.Question;
                    string? selectedOption = await this.AwaitClickerQuestionAnswerAsync(question, cancellationToken);
                    if (string.IsNullOrWhiteSpace(selectedOption))
                    {
                        this.LastActionMessage = "Clicker question canceled.";
                        return;
                    }

                    string userChoiceLine = $"User selected option: {selectedOption}";
                    this.AppendClickerHistory(userChoiceLine);
                    effectiveOperatorNote = string.IsNullOrWhiteSpace(effectiveOperatorNote)
                        ? userChoiceLine
                        : effectiveOperatorNote.Trim() + "\n" + userChoiceLine;
                    this.clickerLastErrorFeedback = string.Empty;
                }

                if (!this.TryParseClickerPlan(responseText, modelWindow, includeWindowChrome: false, out List<ClickerPlanStep>? planSteps, out string normalizedJson, out string errorMessage) || planSteps == null || planSteps.Count == 0)
                {
                    this.ClickerLastParsedPoint = string.Empty;
                    this.ClickerLastParsedAction = string.Empty;
                    this.ClickerLastNormalizedJson = normalizedJson;
                    this.ClickerLastReason = string.IsNullOrWhiteSpace(responseText)
                        ? string.Empty
                        : responseText;
                    this.clickerLastErrorFeedback = $"Parse failure: {errorMessage}. Raw response: {TrimForHistory(responseText)}";
                    this.AppendClickerHistory($"Model response could not be parsed: {TrimForHistory(responseText)}");
                    this.LastActionMessage = $"Clicker could not parse a valid point: {errorMessage}";
                    await StaticLogger.LogAsync($"[Clicker] Raw unparseable model response: {TrimForHistory(responseText)}");

                    if (this.IsClickerLoopRunning)
                    {
                        this.clickerConsecutiveFailures++;
                        if (this.clickerConsecutiveFailures >= 5)
                        {
                            this.StopClickerLoop();
                            this.LastActionMessage = "Clicker loop stopped after repeated unparseable model responses.";
                        }
                    }
                    return;
                }

                ClickerPlanStep? firstPointerStep = planSteps.FirstOrDefault(s => s.IsPointer);
                this.ClickerLastParsedPoint = firstPointerStep?.Pointer?.DisplayLabel ?? string.Empty;
                this.ClickerLastParsedAction = DescribeClickerPlan(planSteps);
                this.ClickerLastNormalizedJson = normalizedJson;
                this.ClickerLastReason = ExtractPreferredReason(normalizedJson, responseText);
                await StaticLogger.LogAsync($"[Clicker] Parsed clicker plan: {this.ClickerLastParsedAction}. Reason={TrimForHistory(this.ClickerLastReason)}");
                await StaticLogger.LogAsync($"[Clicker] Parsed plan step count: {planSteps.Count}. IncludeChrome={this.ClickerIncludeWindowChrome}, BackgroundInput={this.ClickerUseBackgroundClick}, ActivateWindow={this.ClickerActivateWindowBeforeCapture}, RequireInsideWindow={this.ClickerRequirePointInsideWindow}.");
                for (int stepIndex = 0; stepIndex < planSteps.Count; stepIndex++)
                {
                    await StaticLogger.LogAsync($"[Clicker] Plan step {stepIndex + 1}/{planSteps.Count}: {DescribeClickerPlanStep(planSteps[stepIndex])}");
                }

                if (planSteps.All(s => s.IsPointer && s.Pointer != null && (s.Pointer.X < 0 || s.Pointer.Y < 0)))
                {
                    this.AppendClickerHistory($"No safe target found. Reason: {TrimForHistory(this.ClickerLastReason)}");
                    this.LastActionMessage = "Clicker response indicates that no safe target was found.";
                    await StaticLogger.LogAsync("[Clicker] Model returned a sentinel/no-target point. Execution skipped.");
                    return;
                }

                Point? firstPointerScreenPoint = null;
                if (firstPointerStep?.Pointer != null)
                {
                    Point previewPoint = this.ConvertClickerModelPointToScreen(window, firstPointerStep.Pointer);
                    firstPointerScreenPoint = previewPoint;
                    this.ClickerLastTargetScreenPoint = $"{previewPoint.X}, {previewPoint.Y}";

                    var referenceBounds = this.GetClickerEffectiveReferenceBounds(window, this.ClickerIncludeWindowChrome);
                    double markerLeft = referenceBounds.Width > 0
                        ? ((previewPoint.X - referenceBounds.Left) / (double)referenceBounds.Width) * 100.0
                        : 0.0;
                    double markerTop = referenceBounds.Height > 0
                        ? ((previewPoint.Y - referenceBounds.Top) / (double)referenceBounds.Height) * 100.0
                        : 0.0;
                    this.ClickerPreviewMarkerLeftPercent = Math.Clamp(markerLeft, 0.0, 100.0);
                    this.ClickerPreviewMarkerTopPercent = Math.Clamp(markerTop, 0.0, 100.0);
                }
                else
                {
                    this.ClickerLastTargetScreenPoint = string.Empty;
                }

                this.ClickerIterationCount++;

                if (this.ClickerDryRun)
                {
                    this.AppendClickerHistory($"Dry run {this.ClickerLastParsedAction}. Reason: {TrimForHistory(this.ClickerLastReason)}");
                    this.ClickerLastConfirmationOutcome = "Dry run";
                    this.LastActionMessage = $"Dry run: parsed plan {this.ClickerLastParsedAction}.";
                    return;
                }

                if (this.ClickerConfirmBeforeClick && firstPointerScreenPoint.HasValue)
                {
                    string confirmation = await this.AwaitClickerConfirmationAsync(firstPointerScreenPoint.Value, cancellationToken);
                    this.ClickerLastConfirmationOutcome = confirmation switch
                    {
                        "confirm" => "Confirmed",
                        "cancel-loop" => "Canceled loop",
                        _ => "Denied"
                    };

                    if (string.Equals(confirmation, "cancel-loop", StringComparison.Ordinal))
                    {
                        if (this.IsClickerLoopRunning)
                        {
                            this.StopClickerLoop();
                        }

                        this.AppendClickerHistory("Loop canceled by Esc before click execution.");
                        this.LastActionMessage = "Clicker loop canceled by Esc.";
                        return;
                    }

                    if (!string.Equals(confirmation, "confirm", StringComparison.Ordinal))
                    {
                        this.AppendClickerHistory($"{this.ClickerLastParsedAction} denied for target {this.ClickerLastParsedPoint}. Reason: {TrimForHistory(this.ClickerLastReason)}");
                        this.LastActionMessage = $"{this.ClickerLastParsedAction} denied by keyboard.";
                        return;
                    }
                }
                else
                {
                    this.ClickerLastConfirmationOutcome = "Auto";
                }

                if (!await this.ExecuteClickerPlanStepsAsync(window, planSteps, cancellationToken))
                {
                    await StaticLogger.LogAsync("[Clicker] Plan execution returned failure.");
                    return;
                }

                this.clickerLastErrorFeedback = string.Empty;
                this.clickerConsecutiveFailures = 0;
                bool wasAwaitingWebChatResponse = this.UseWebChatProvider && this.webChatClipboardStage == WebChatClipboardStage.AwaitingResult;
                if (this.UseWebChatProvider)
                {
                    this.AdvanceWebChatClickerLoopStateAfterSuccess();
                    if (wasAwaitingWebChatResponse)
                    {
                        _ = await this.TryImportWebChatResponseFromClipboardAsync(manualRequest: false);
                    }
                }

                this.AppendClickerHistory($"Executed plan: {this.ClickerLastParsedAction}. Reason: {TrimForHistory(this.ClickerLastReason)}");
                this.LastActionMessage = $"Clicker executed plan: {this.ClickerLastParsedAction}.";
                await StaticLogger.LogAsync("[Clicker] Plan execution completed successfully.");
            }
            catch (OperationCanceledException)
            {
                this.AppendClickerHistory("Clicker iteration canceled.");
                this.LastActionMessage = "Clicker iteration canceled.";
            }
            catch (Exception ex)
            {
                this.clickerLastErrorFeedback = ExtractClickerExceptionText(ex);
                this.ClickerLastResponse = ExtractClickerExceptionText(ex);
                if (string.IsNullOrWhiteSpace(this.ClickerLastReason))
                {
                    this.ClickerLastReason = this.ClickerLastResponse;
                }
                try
                {
                    _ = await this.Client.ClearServerContextAsync();
                }
                catch
                {
                }
                await StaticLogger.LogAsync(ex, "[Clicker] Iteration failed");
                this.LastActionMessage = $"Clicker failed: {ex.Message}";

                // Count exception-based failures towards the consecutive failure counter too.
                // This prevents infinite error loops when the server is fundamentally broken.
                if (this.IsClickerLoopRunning)
                {
                    this.clickerConsecutiveFailures++;
                    if (this.clickerConsecutiveFailures >= 5)
                    {
                        this.StopClickerLoop();
                        this.LastActionMessage = "Clicker loop stopped after repeated failures.";
                    }
                }
            }
            finally
            {
                try
                {
                    this.LastGenerationStats = this.Client.GetLastGenerationStatsSnapshot();
                    await this.UpdateHardwareStatsAsync();
                }
                catch
                {
                }

                this.IsClickerBusy = false;
                this.RequestUiRefresh();

                if (this.ClickerUseBackgroundClick)
                {
                    if (savedForegroundWindow != IntPtr.Zero)
                    {
                        try
                        {
                            this.Clicker.TryRestoreForegroundWindow(savedForegroundWindow);
                            await Task.Delay(30);
                        }
                        catch
                        {
                        }
                    }

                    if (shouldRestorePromptFocus)
                    {
                        await this.RestorePromptInputFocusAsync();
                    }
                }
            }
        }

        [JSInvokable]
        [SupportedOSPlatform("windows")]
        public Task OnClickerEscapePressed()
        {
            if (this.IsClickerLoopRunning)
            {
                this.StopClickerLoop();
                this.AppendClickerHistory("Loop canceled by Esc hotkey.");
                this.LastActionMessage = "Clicker loop canceled by Esc.";
                this.RequestUiRefresh();
            }

            return Task.CompletedTask;
        }

        [JSInvokable]
        public Task OnClickerProtectedZoneSelectionCanceled()
        {
            this.IsClickerProtectedZoneSelectionActive = false;
            this.LastActionMessage = "Protected zone selection canceled.";
            this.RequestUiRefresh();
            return Task.CompletedTask;
        }

        [JSInvokable]
        public Task OnClickerProtectedZoneSelected(int leftNormalized, int topNormalized, int widthNormalized, int heightNormalized)
        {
            int left = Math.Clamp(leftNormalized, 0, 999);
            int top = Math.Clamp(topNormalized, 0, 999);
            int width = Math.Clamp(widthNormalized, 1, 1000 - left);
            int height = Math.Clamp(heightNormalized, 1, 1000 - top);

            this.IsClickerProtectedZoneSelectionActive = false;
            this.clickerProtectedZones.Add(new ClickerProtectedZone
            {
                Id = Guid.NewGuid(),
                Name = this.GetNextClickerProtectedZoneName(),
                IncludeWindowChrome = this.ClickerIncludeWindowChrome,
                LeftNormalized = left,
                TopNormalized = top,
                WidthNormalized = width,
                HeightNormalized = height
            });

            this.LastActionMessage = "Protected zone added.";
            this.RequestUiRefresh();
            return Task.CompletedTask;
        }

        private async Task<string> GenerateClickerResponseAsync(LlamaGenerationRequest request, string fallbackPrompt, CancellationToken cancellationToken)
        {
            try
            {
                return await this.CollectClickerResponseAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (IsClickerInputParseFailure(ex))
            {
                string serverError = ExtractClickerServerError(ex);
                this.clickerLastErrorFeedback = serverError;
                await StaticLogger.LogAsync($"[Clicker] Primary request failed with parse-input error. Retrying with fallback prompt. Error={serverError}");
                await this.ClearInferenceStateAfterFailureAsync();

                var fallbackRequest = new LlamaGenerationRequest
                {
                    Prompt = fallbackPrompt,
                    SystemPrompt = null,
                    Images = request.Images,
                    Isolated = true,
                    PersistConversation = false,
                    IncludeConversationHistory = false,
                    MaxTokens = Math.Min(96, request.MaxTokens),
                    Temperature = request.Temperature,
                    TopP = request.TopP,
                    TopK = request.TopK,
                    MaxWidthAndHeight = Math.Max(192, request.MaxWidthAndHeight > 0 ? Math.Min(request.MaxWidthAndHeight, 512) : 512),
                    ImageFormat = "jpg",
                    Stream = false
                };

                await StaticLogger.LogAsync($"[Clicker] Fallback prompt size: user={fallbackPrompt.Length}.");
                try
                {
                    return await this.CollectClickerResponseAsync(fallbackRequest, cancellationToken);
                }
                catch (HttpRequestException fallbackEx) when (IsClickerInputParseFailure(fallbackEx))
                {
                    this.clickerLastErrorFeedback = ExtractClickerServerError(fallbackEx);
                    await StaticLogger.LogAsync("[Clicker] Fallback request still failed with parse-input error. Retrying with ultra-compact image payload.");
                    await this.ClearInferenceStateAfterFailureAsync();

                    var ultraFallbackRequest = new LlamaGenerationRequest
                    {
                        Prompt = fallbackPrompt,
                        SystemPrompt = null,
                        Images = request.Images,
                        Isolated = true,
                        PersistConversation = false,
                        IncludeConversationHistory = false,
                        MaxTokens = Math.Min(64, request.MaxTokens),
                        Temperature = request.Temperature,
                        TopP = request.TopP,
                        TopK = request.TopK,
                        MaxWidthAndHeight = 256,
                        ImageFormat = "jpg",
                        Stream = false
                    };

                    return await this.CollectClickerResponseAsync(ultraFallbackRequest, cancellationToken);
                }
            }
        }

        private async Task<string> CollectClickerResponseAsync(LlamaGenerationRequest request, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            await foreach (string chunk in this.Client.GenerateAsync(request, cancellationToken))
            {
                sb.Append(chunk);
            }

            return sb.ToString().Trim();
        }

        private async Task ClearInferenceStateAfterFailureAsync()
        {
            try
            {
                _ = await this.Client.ClearServerContextAsync(cancellationToken: CancellationToken.None);
            }
            catch
            {
            }

            try
            {
                this.Client.ResetConversation();
            }
            catch
            {
            }

            try
            {
                await Task.Delay(120);
            }
            catch
            {
            }
        }

        private static bool IsClickerInputParseFailure(HttpRequestException ex)
        {
            return ex.Message.Contains("Failed to parse input", StringComparison.OrdinalIgnoreCase)
                || (ex.Message.Contains("500", StringComparison.OrdinalIgnoreCase)
                    && ex.Message.Contains("server_error", StringComparison.OrdinalIgnoreCase)
                    && ex.Message.Contains("parse", StringComparison.OrdinalIgnoreCase));
        }

        private static string ExtractClickerServerError(HttpRequestException ex)
        {
            string message = ex.Message?.Trim() ?? string.Empty;
            if (message.Length <= 1200)
            {
                return message;
            }

            return message[..1197] + "...";
        }

        private static string ExtractClickerExceptionText(Exception ex)
        {
            string message = ex.ToString().Trim();
            if (message.Length <= 2000)
            {
                return message;
            }

            return message[..1997] + "...";
        }

        private int GetClickerModelMaxWidthAndHeight(string screenshotPath)
        {
            if (this.ClickerSafeMode)
            {
                return 128;
            }

            try
            {
                using var bitmap = new Bitmap(screenshotPath);
                int longestEdge = Math.Max(bitmap.Width, bitmap.Height);
                if (longestEdge <= 0)
                {
                    return 512;
                }

                double scale = Math.Clamp(this.ClickerModelImageScalePercent, 5, 100) / 100.0;
                int scaled = Math.Max(128, (int)Math.Round(longestEdge * scale));
                return Math.Clamp(scaled, 128, 1024);
            }
            catch
            {
                return 512;
            }
        }

        private string BuildClickerPrompt(ScreenClicker.MarkedWindowInfo window, string? liveOperatorNote)
        {
            string historyText = string.Join(" | ", this.clickerHistory
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .TakeLast(5)
                .Select(s => $"- {LimitClickerPromptText(SanitizeClickerPromptText(s), 280)}"));

            string protectedZonePrompt = string.Empty;
            List<ClickerProtectedZone> promptZones = this.VisibleClickerProtectedZones.ToList();
            if (this.ClickerTellModelAboutProtectedZones && promptZones.Count > 0)
            {
                string zoneLines = string.Join("; ", promptZones.Select(z => $"{z.Name}=[{z.LeftNormalized},{z.TopNormalized},{z.RightNormalized},{z.BottomNormalized}]"));
                protectedZonePrompt = $"Protected zones (do NOT click inside): {zoneLines}.\n";
            }

            var sb = new StringBuilder();
            sb.Append("Look at the screenshot and decide the next UI action.\n");
            sb.Append($"Goal: {LimitClickerPromptText(SanitizeClickerPromptText(this.ClickerInstructions.Trim()), 420)}\n");

            if (!string.IsNullOrWhiteSpace(liveOperatorNote))
                sb.Append($"Note: {LimitClickerPromptText(SanitizeClickerPromptText(liveOperatorNote.Trim()), 220)}\n");

            if (this.ClickerSendLastErrorToModel && !string.IsNullOrWhiteSpace(this.clickerLastErrorFeedback))
                sb.Append($"Previous error: {LimitClickerPromptText(SanitizeClickerPromptText(this.clickerLastErrorFeedback), 260)}\n");

            sb.Append($"Window: {SanitizeClickerPromptText(window.Title)} {window.Width}x{window.Height}\n");

            if (this.ClickerLimitInteractionRegion)
                sb.Append($"Active region: [{this.ClickerInteractionMinX},{this.ClickerInteractionMinY}] to [{this.ClickerInteractionMaxX},{this.ClickerInteractionMaxY}]\n");

            if (!string.IsNullOrWhiteSpace(historyText))
                sb.Append($"Recent actions: {historyText}\n");

            sb.Append(protectedZonePrompt);

            if (this.Clicker.IsLeftButtonHeld)
                sb.Append("Mouse button is currently held down.\n");

            sb.Append("Respond with exactly one JSON object:\n");
            sb.Append("{\"steps\":[...],\"reason\":\"brief explanation\"}\n");
            sb.Append("Step types:\n");
            sb.Append("  Pointer: {\"point_2d\":[x,y],\"action\":\"click\"} (also: doubleclick, down, up)\n");
            sb.Append("  Keys:    {\"keys\":[\"ctrl\",\"v\"],\"action\":\"press\"} (also: down, up). Single chars like \"+\" or \"-\" are valid.\n");
            sb.Append("  Text:    {\"type_text\":\"hello\"}\n");

            if (this.ClickerAllowModelQuestions)
                sb.Append("If you need user input first, respond with {\"question\":\"...\",\"options\":[\"A\",\"B\"],\"kind\":\"info\",\"reason\":\"...\",\"addTextOption\":true,\"textLabel\":\"Custom\",\"textPlaceholder\":\"Type here\",\"submitText\":\"Send\"}.\n");

            sb.Append("Use coordinates 0..1000 relative to the screenshot.\n");
            sb.Append("If no safe action exists, return {\"point_2d\":[-1,-1],\"action\":null,\"reason\":\"not found\"}.\n");

            return sb.ToString();
        }

        private string BuildSafeModeClickerPrompt(ScreenClicker.MarkedWindowInfo window)
        {
            List<ClickerProtectedZone> promptZones = this.VisibleClickerProtectedZones.ToList();
            string protectedZonePrompt = this.ClickerTellModelAboutProtectedZones && promptZones.Count > 0
                ? $" Avoid zones: {string.Join("; ", promptZones.Select(z => $"[{z.LeftNormalized},{z.TopNormalized},{z.RightNormalized},{z.BottomNormalized}]"))}."
                : string.Empty;

            return "Screenshot of a window. Decide the next action. "
                + $"Goal: {LimitClickerPromptText(SanitizeClickerPromptText(this.ClickerInstructions.Trim()), 120)}. "
                + $"Size: {window.Width}x{window.Height}."
                + (this.ClickerLimitInteractionRegion
                    ? $" Region: [{this.ClickerInteractionMinX},{this.ClickerInteractionMinY}]-[{this.ClickerInteractionMaxX},{this.ClickerInteractionMaxY}]."
                    : string.Empty)
                + protectedZonePrompt
                + " Reply JSON: {\"action\":\"click\",\"point_2d\":[x,y],\"reason\":\"why\"} or {\"keys\":[\"key\"],\"action\":\"press\"} or {\"type_text\":\"text\"}."
                + " Coords 0..1000."
                + " No action: {\"point_2d\":[-1,-1],\"action\":null}.";
        }

        private async Task PrepareWebChatClickerLoopStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            this.ResetWebChatClickerLoopStateIfNeeded();
            switch (this.webChatClipboardStage)
            {
                case WebChatClipboardStage.Image:
                    if (!string.IsNullOrWhiteSpace(this.webChatPreparedImageKey)
                        && await CopyImageSourceToClipboardAsync(this.webChatPreparedImageKey))
                    {
                        this.WebChatConnectionStatus = $"{this.SelectedWebChatProviderText} automation ready. Clipboard contains the queued image attachment for upload/paste.";
                    }
                    break;

                case WebChatClipboardStage.Text:
                    if (!string.IsNullOrWhiteSpace(this.webChatPreparedPromptText))
                    {
                        await SetClipboardTextAsync(this.webChatPreparedPromptText);
                        this.WebChatConnectionStatus = $"{this.SelectedWebChatProviderText} automation ready. Clipboard contains the queued prompt text.";
                    }
                    break;
            }
        }

        private void ResetWebChatClickerLoopStateIfNeeded()
        {
            string prompt = this.UserInput?.Trim() ?? string.Empty;
            string imageKey = this.SelectedImagePaths.FirstOrDefault() ?? string.Empty;

            if (this.webChatClipboardStage != WebChatClipboardStage.None
                && string.IsNullOrWhiteSpace(prompt)
                && string.IsNullOrWhiteSpace(imageKey))
            {
                return;
            }

            bool promptChanged = !string.Equals(this.webChatPreparedPromptText, prompt, StringComparison.Ordinal);
            bool imageChanged = !string.Equals(this.webChatPreparedImageKey, imageKey, StringComparison.Ordinal);
            if (!promptChanged && !imageChanged && this.webChatClipboardStage != WebChatClipboardStage.None)
            {
                return;
            }

            this.webChatPreparedPromptText = prompt;
            this.webChatPreparedImageKey = imageKey;
            this.webChatClipboardStage = !string.IsNullOrWhiteSpace(imageKey)
                ? WebChatClipboardStage.Image
                : !string.IsNullOrWhiteSpace(prompt)
                    ? WebChatClipboardStage.Text
                    : WebChatClipboardStage.None;
        }

        private string BuildWebChatClickerAutomationNote()
        {
            string providerHint = this.SelectedWebChatProvider switch
            {
                "qwen" => "Qwen usually places the composer across the bottom center, often with a leading plus button for attachments and a send or voice control on the right.",
                "gemini" => "Gemini usually uses a bottom composer with upload controls near the prompt box and a send action at the right side of the composer.",
                "chatgpt" => "ChatGPT usually uses a bottom composer with attachment controls to the left and a send button to the right.",
                _ => this.SelectedWebChatProviderHints
            };

            string stageInstruction = this.webChatClipboardStage switch
            {
                WebChatClipboardStage.Image => "The clipboard currently contains the queued image attachment. Focus the chat composer or attachment flow and paste or upload the image before sending any text.",
                WebChatClipboardStage.Text => "The clipboard currently contains the queued prompt text. Focus the input composer, paste with Ctrl+V, then send the message with Enter or the send button.",
                WebChatClipboardStage.AwaitingResult => "The queued prompt was already sent. Do not resend it. If a complete assistant reply is visible, copy only the latest assistant reply text into the clipboard. Never copy the user's own prompt unless it is unavoidably part of the selection. If the provider is still generating, wait and avoid unnecessary actions.",
                _ => "No clipboard payload is currently queued. Avoid unnecessary actions and wait for new work."
            };

            return $"Browser provider: {this.SelectedWebChatProviderText}. {providerHint} {stageInstruction} Prefer a complete multi-step plan that finishes the current queued clipboard stage safely. If the queued stage already appears completed on screen, return the sentinel no-action result.";
        }

        private void AdvanceWebChatClickerLoopStateAfterSuccess()
        {
            switch (this.webChatClipboardStage)
            {
                case WebChatClipboardStage.Image:
                    this.webChatClipboardStage = !string.IsNullOrWhiteSpace(this.webChatPreparedPromptText)
                        ? WebChatClipboardStage.Text
                        : WebChatClipboardStage.AwaitingResult;
                    this.WebChatConnectionStatus = !string.IsNullOrWhiteSpace(this.webChatPreparedPromptText)
                        ? $"{this.SelectedWebChatProviderText} automation advanced from image stage to prompt stage."
                        : $"{this.SelectedWebChatProviderText} image stage executed. Waiting for the page state to settle.";
                    break;

                case WebChatClipboardStage.Text:
                    this.webChatClipboardStage = WebChatClipboardStage.AwaitingResult;
                    this.WebChatConnectionStatus = $"{this.SelectedWebChatProviderText} prompt/send stage executed. Waiting for response.";
                    if (string.Equals(this.UserInput?.Trim(), this.webChatPreparedPromptText, StringComparison.Ordinal))
                    {
                        this.UserInput = string.Empty;
                    }
                    break;

                case WebChatClipboardStage.AwaitingResult:
                    this.WebChatConnectionStatus = $"{this.SelectedWebChatProviderText} response capture step executed. Trying to import clipboard text.";
                    break;
            }
        }

        private void QueueWebChatClipboardWork(string promptText, string imageKey)
        {
            this.webChatPreparedPromptText = promptText?.Trim() ?? string.Empty;
            this.webChatPreparedImageKey = imageKey?.Trim() ?? string.Empty;
            this.webChatClipboardStage = !string.IsNullOrWhiteSpace(this.webChatPreparedImageKey)
                ? WebChatClipboardStage.Image
                : !string.IsNullOrWhiteSpace(this.webChatPreparedPromptText)
                    ? WebChatClipboardStage.Text
                    : WebChatClipboardStage.None;
            this.webChatLastImportedResponse = string.Empty;
        }

        private void QueuePendingWebChatAssistantMessage(string statusText)
        {
            if (this.webChatPendingAssistantMessage != null && this.ChatMessages.Contains(this.webChatPendingAssistantMessage))
            {
                this.webChatPendingAssistantMessage.Content = statusText;
                this.webChatPendingAssistantMessage.CreatedAtUtc = DateTime.UtcNow;
                return;
            }

            this.webChatPendingAssistantMessage = new LlamaChatMessage
            {
                Role = "assistant",
                Content = statusText,
                CreatedAtUtc = DateTime.UtcNow
            };
            this.ChatMessages.Add(this.webChatPendingAssistantMessage);
        }

        [SupportedOSPlatform("windows")]
        private void StartWebChatClipboardWatcher()
        {
            this.webChatWatcherCts?.Cancel();
            this.webChatWatcherCts?.Dispose();
            this.webChatWatcherCts = new CancellationTokenSource();
            var token = this.webChatWatcherCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested && this.webChatPendingAssistantMessage != null && this.UseWebChatProvider)
                    {
                        await Task.Delay(1000, token);
                        if (token.IsCancellationRequested) break;

                        bool success = await this.TryImportWebChatResponseFromClipboardAsync(manualRequest: false);
                        if (success)
                        {
                            this.RequestUiRefresh();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) {}
            });
        }

        [SupportedOSPlatform("windows")]
        public async Task ImportWebChatResponseFromClipboardAsync()
        {
            bool imported = await this.TryImportWebChatResponseFromClipboardAsync(manualRequest: true);
            if (imported)
            {
                await this.ScrollChatToBottomAsync();
            }
        }

        [SupportedOSPlatform("windows")]
        private async Task<bool> TryImportWebChatResponseFromClipboardAsync(bool manualRequest)
        {
            if (!this.UseWebChatProvider)
            {
                return false;
            }

            string clipboardText;
            try
            {
                clipboardText = await GetClipboardTextAsync();
            }
            catch (Exception ex)
            {
                if (manualRequest)
                {
                    this.LastActionMessage = $"Clipboard read failed: {ex.Message}";
                    this.RequestUiRefresh();
                }

                return false;
            }

            string normalized = clipboardText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                if (manualRequest)
                {
                    this.LastActionMessage = "Clipboard does not contain text to import.";
                    this.RequestUiRefresh();
                }

                return false;
            }

            string extractedResponse = this.ExtractWebChatAssistantResponse(normalized);
            if (string.IsNullOrWhiteSpace(extractedResponse))
            {
                if (manualRequest)
                {
                    this.LastActionMessage = "Clipboard text does not contain a new assistant response yet.";
                    this.RequestUiRefresh();
                }

                return false;
            }

            if (string.Equals(extractedResponse, this.webChatLastImportedResponse, StringComparison.Ordinal))
            {
                if (manualRequest)
                {
                    this.LastActionMessage = "Clipboard text is not a new provider response yet.";
                    this.RequestUiRefresh();
                }

                return false;
            }

            if (this.webChatPendingAssistantMessage != null && this.ChatMessages.Contains(this.webChatPendingAssistantMessage))
            {
                this.webChatPendingAssistantMessage.Content = extractedResponse;
                this.webChatPendingAssistantMessage.CreatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                this.ChatMessages.Add(new LlamaChatMessage
                {
                    Role = "assistant",
                    Content = extractedResponse,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }

            if (!this.IsolatedGeneration)
            {
                this.Client.AddAssistantMessage(extractedResponse);
            }

            this.webChatLastImportedResponse = extractedResponse;
            this.webChatPendingAssistantMessage = null;
            this.webChatPreparedPromptText = string.Empty;
            this.webChatPreparedImageKey = string.Empty;
            this.webChatClipboardStage = WebChatClipboardStage.None;
            this.WebChatConnectionStatus = $"Imported response from {this.SelectedWebChatProviderText}.";
            this.LastActionMessage = $"Web chat response imported from clipboard.";
            this.RequestUiRefresh();
            return true;
        }

        [SupportedOSPlatform("windows")]
        private static Task<string> GetClipboardTextAsync()
        {
            return RunOnStaThreadAsync(() => System.Windows.Forms.Clipboard.ContainsText()
                ? System.Windows.Forms.Clipboard.GetText()
                : string.Empty);
        }

        private string ExtractWebChatAssistantResponse(string clipboardText)
        {
            string normalized = (clipboardText ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            string prompt = (this.webChatPreparedPromptText ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

            List<string> lines = normalized
                .Split('\n', StringSplitOptions.TrimEntries)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                while (lines.Count >= 2
                    && IsWebChatUserTranscriptLabel(lines[0])
                    && string.Equals(lines[1], prompt, StringComparison.Ordinal))
                {
                    lines.RemoveAt(0);
                    lines.RemoveAt(0);
                }

                lines.RemoveAll(line => string.Equals(line, prompt, StringComparison.Ordinal));
            }

            while (lines.Count > 1 && IsWebChatAssistantTranscriptLabel(lines[0]))
            {
                lines.RemoveAt(0);
            }

            string candidate = string.Join("\n", lines).Trim();
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                int promptIndex = candidate.LastIndexOf(prompt, StringComparison.Ordinal);
                if (promptIndex >= 0)
                {
                    string trailing = candidate[(promptIndex + prompt.Length)..].Trim();
                    if (!string.IsNullOrWhiteSpace(trailing))
                    {
                        candidate = trailing;
                    }
                }
            }

            candidate = candidate.Trim();
            return string.Equals(candidate, prompt, StringComparison.Ordinal) ? string.Empty : candidate;
        }

        private static bool IsWebChatUserTranscriptLabel(string text)
        {
            string value = (text ?? string.Empty).Trim().TrimEnd(':').ToLowerInvariant();
            return value is "user" or "you" or "me" or "prompt" or "question";
        }

        private static bool IsWebChatAssistantTranscriptLabel(string text)
        {
            string value = (text ?? string.Empty).Trim().TrimEnd(':').ToLowerInvariant();
            return value is "assistant" or "qwen" or "model" or "answer" or "reply";
        }

        public string BuildClickerPromptPreview()
        {
            return "Analyze the screenshot and return one JSON object only.\n"
                + $"Task: {LimitClickerPromptText(SanitizeClickerPromptText(this.ClickerInstructions.Trim()), 420)}\n"
                + (this.ClickerSendLastErrorToModel && !string.IsNullOrWhiteSpace(this.clickerLastErrorFeedback)
                    ? $"Last error: {LimitClickerPromptText(SanitizeClickerPromptText(this.clickerLastErrorFeedback), 260)}\n"
                    : string.Empty)
                + $"Pointer held: {(this.Clicker.IsLeftButtonHeld ? "yes" : "no")}\n"
                + "Reason must explain exactly which target is chosen, why it is the correct next planned step, and what effect the action should cause.\n"
                + $"Format: {{\"steps\":[step],\"reason\":\"strict explanation of target, intent, and expected result\"}}.\n"
                + "Use screenshot-relative coordinates, preferably 0..1000.\n"
                + "If no safe action exists, return {\"point_2d\":[-1,-1],\"action\":null,\"reason\":\"not found\"}.";
        }

        private string BuildFallbackClickerPrompt(ScreenClicker.MarkedWindowInfo window, string? liveOperatorNote)
        {
            return "Screenshot of a window. Decide the next action. "
                + $"Goal: {LimitClickerPromptText(SanitizeClickerPromptText(this.ClickerInstructions.Trim()), 220)}. "
                + (string.IsNullOrWhiteSpace(liveOperatorNote)
                    ? string.Empty
                    : $"Note: {LimitClickerPromptText(SanitizeClickerPromptText(liveOperatorNote.Trim()), 120)}. ")
                + (this.ClickerSendLastErrorToModel && !string.IsNullOrWhiteSpace(this.clickerLastErrorFeedback)
                    ? $"Previous error: {LimitClickerPromptText(SanitizeClickerPromptText(this.clickerLastErrorFeedback), 160)}. "
                    : string.Empty)
                + $"Size: {window.Width}x{window.Height}. "
                + (this.ClickerLimitInteractionRegion
                    ? $"Region: [{this.ClickerInteractionMinX},{this.ClickerInteractionMinY}]-[{this.ClickerInteractionMaxX},{this.ClickerInteractionMaxY}]. "
                    : string.Empty)
                + "Reason must explain the chosen target, why it is the correct next planned step, and the expected effect. "
                + "Reply JSON: {\"steps\":[{\"point_2d\":[x,y],\"action\":\"click\"}],\"reason\":\"...\"}. "
                + "Keys: {\"keys\":[\"key\"],\"action\":\"press\"} or {\"type_text\":\"text\"}. "
                + "Coords 0..1000. "
                + "No action: {\"point_2d\":[-1,-1],\"action\":null,\"reason\":\"not found\"}.";
        }

        private static string BuildCompactClickerSystemPrompt()
        {
            return "You are a visual UI automation agent. You receive a screenshot and must decide the next action. Respond with a single JSON object containing a steps array and a reason string. The reason must be strict and explicit: identify the intended target, explain why it is the correct next planned action, and describe the expected effect on the UI. Each step is one of: a pointer action with point_2d and action, a keyboard action with keys and action, or a text entry with type_text. Coordinates are relative to the screenshot in a 0..1000 range. Be precise, concise, and safe.";
        }

        private static string SanitizeClickerPromptText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                {
                    continue;
                }

                c = c switch
                {
                    '\u2013' or '\u2014' or '\u2212' => '-',
                    '\u2018' or '\u2019' => '\'',
                    '\u201C' or '\u201D' => '"',
                    '\u2026' => '.',
                    '\u00A0' => ' ',
                    '\u00E4' => 'a',
                    '\u00F6' => 'o',
                    '\u00FC' => 'u',
                    '\u00C4' => 'A',
                    '\u00D6' => 'O',
                    '\u00DC' => 'U',
                    '\u00DF' => 's',
                    _ => c
                };

                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c);
                        sb.Append(text[++i]);
                    }

                    continue;
                }

                if (char.IsLowSurrogate(c))
                {
                    continue;
                }

                if (c <= 127)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static string LimitClickerPromptText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text;
            }

            return text[..Math.Max(0, maxLength - 3)] + "...";
        }


        private async Task<bool> ExecuteClickerPlanStepsAsync(ScreenClicker.MarkedWindowInfo window, IReadOnlyList<ClickerPlanStep> planSteps, CancellationToken cancellationToken)
        {
            int maxSteps = Math.Min(8, planSteps.Count);
            await StaticLogger.LogAsync($"[Clicker] Starting plan execution. Steps={planSteps.Count}, Executing={maxSteps}.");
            for (int index = 0; index < maxSteps; index++)
            {
                ClickerPlanStep step = planSteps[index];
                await StaticLogger.LogAsync($"[Clicker] Executing step {index + 1}/{maxSteps}: {DescribeClickerPlanStep(step)}");

                if (step.DelayMs > 0)
                {
                    await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: delaying {step.DelayMs}ms before action.");
                    await Task.Delay(step.DelayMs, cancellationToken);
                }

                if (step.IsPointer && step.Pointer != null)
                {
                    if (step.Pointer.X < 0 || step.Pointer.Y < 0)
                    {
                        await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: pointer sentinel detected ({step.Pointer.X},{step.Pointer.Y}), skipping.");
                        continue;
                    }

                    Point screenPoint = this.ConvertClickerModelPointToScreen(window, step.Pointer);
                    this.ClickerLastTargetScreenPoint = $"{screenPoint.X}, {screenPoint.Y}";
                    await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: pointer mapped to screen point {screenPoint.X},{screenPoint.Y}. Action={step.PointerAction}.");

                    Rectangle effectiveBounds = this.GetClickerEffectiveReferenceBounds(window, this.ClickerIncludeWindowChrome);
                    if (this.ClickerRequirePointInsideWindow && !Rectangle.Inflate(effectiveBounds, -2, -2).Contains(screenPoint))
                    {
                        this.AppendClickerHistory($"Rejected out-of-bounds target {step.Pointer.DisplayLabel}. Reason: {TrimForHistory(this.ClickerLastReason)}");
                        this.LastActionMessage = "Clicker rejected a target because it lies outside the marked window.";
                        await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: rejected, target outside window bounds.");
                        return false;
                    }

                    if (this.TryFindBlockingProtectedZone(window, screenPoint, this.ClickerIncludeWindowChrome, out ClickerProtectedZone? protectedZone) && protectedZone != null)
                    {
                        this.ClickerLastConfirmationOutcome = "Protected zone";
                        this.AppendClickerHistory($"Blocked pointer step because it falls inside protected zone '{protectedZone.Name}'.");
                        this.LastActionMessage = $"Clicker blocked a pointer step because it falls inside protected zone '{protectedZone.Name}'.";
                        await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: blocked by protected zone '{protectedZone.Name}'.");
                        return false;
                    }

                    var screenPointCommand = new ScreenClicker.ClickPoint(screenPoint.X, screenPoint.Y, ScreenClicker.CoordinateSpace.ScreenPixels, step.Pointer.Source + "-screen");
                    bool pointerSuccess = false;
                    Point executedPoint = Point.Empty;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (attempt > 0)
                        {
                            await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: retrying pointer action (attempt {attempt + 1}/3).");
                            this.Clicker.TryRefreshWindow(window, out var retryWindow);
                            if (retryWindow != null)
                            {
                                window = retryWindow;
                            }

                            await Task.Delay(150 * attempt, cancellationToken);
                        }

                        if (this.Clicker.TryExecutePointerAction(window, screenPointCommand, step.PointerAction, out executedPoint, this.ClickerActivateWindowBeforeCapture, this.ClickerIncludeWindowChrome, this.ClickerUseBackgroundClick))
                        {
                            pointerSuccess = true;
                            break;
                        }
                    }

                    if (!pointerSuccess)
                    {
                        if (this.ClickerUseBackgroundClick)
                        {
                            await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: background click failed after retries. Trying one foreground fallback click.");
                            this.Clicker.TryRefreshWindow(window, out var foregroundRetryWindow);
                            if (foregroundRetryWindow != null)
                            {
                                window = foregroundRetryWindow;
                            }

                            if (this.Clicker.TryExecutePointerAction(window, screenPointCommand, step.PointerAction, out executedPoint, this.ClickerActivateWindowBeforeCapture, this.ClickerIncludeWindowChrome, useBackgroundClick: false))
                            {
                                pointerSuccess = true;
                                await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: foreground fallback click succeeded at {executedPoint.X},{executedPoint.Y}. Action={step.PointerAction}.");
                            }
                        }

                        if (pointerSuccess)
                        {
                            this.ClickerLastParsedPoint = step.Pointer.DisplayLabel;
                            this.ClickerLastTargetScreenPoint = $"{executedPoint.X}, {executedPoint.Y}";
                            continue;
                        }

                        this.LastActionMessage = "Clicker could not execute a pointer step.";
                        this.AppendClickerHistory($"Pointer step failed at {screenPoint.X},{screenPoint.Y}.");
                        await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: pointer execution failed at {screenPoint.X},{screenPoint.Y} after retries. Action={step.PointerAction}.");
                        return false;
                    }

                    this.ClickerLastParsedPoint = step.Pointer.DisplayLabel;
                    this.ClickerLastTargetScreenPoint = $"{executedPoint.X}, {executedPoint.Y}";
                    await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: pointer execution succeeded at {executedPoint.X},{executedPoint.Y}. Action={step.PointerAction}.");
                    continue;
                }

                if (step.IsKeyboard)
                {
                    var keyboardCommand = new ScreenClicker.ParsedKeyboardCommand(step.Keys ?? [], step.KeyboardAction, step.TypeText);
                    await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: executing keyboard action {step.KeyboardAction}. Keys='{string.Join("+", step.Keys ?? [])}', TextLength={(step.TypeText?.Length ?? 0)}.");
                    bool keyboardSuccess = false;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (attempt > 0)
                        {
                            await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: retrying keyboard action (attempt {attempt + 1}/3).");
                            this.Clicker.TryRefreshWindow(window, out var retryWindow);
                            if (retryWindow != null)
                            {
                                window = retryWindow;
                            }

                            await Task.Delay(150 * attempt, cancellationToken);
                        }

                        if (this.Clicker.TryExecuteKeyboardCommand(window, keyboardCommand, this.ClickerActivateWindowBeforeCapture, this.ClickerUseBackgroundClick))
                        {
                            keyboardSuccess = true;
                            break;
                        }
                    }

                    if (!keyboardSuccess)
                    {
                        this.LastActionMessage = "Clicker could not execute a keyboard step.";
                        this.AppendClickerHistory($"Keyboard step failed ({step.Describe()}).");
                        await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: keyboard execution failed after retries.");
                        return false;
                    }

                    this.ClickerLastParsedPoint = string.Empty;
                    await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: keyboard execution succeeded.");
                    continue;
                }

                await StaticLogger.LogAsync($"[Clicker] Step {index + 1}: no executable action detected, skipping.");
            }

            await StaticLogger.LogAsync("[Clicker] All executable plan steps finished.");
            return true;
        }

        private bool TryParseClickerPlan(string responseText, ScreenClicker.MarkedWindowInfo window, bool includeWindowChrome, out List<ClickerPlanStep>? steps, out string normalizedJson, out string errorMessage)
        {
            steps = [];
            normalizedJson = string.Empty;
            errorMessage = string.Empty;

            foreach (string candidate in EnumerateJsonCandidates(responseText))
            {
                try
                {
                    using var document = JsonDocument.Parse(candidate, new JsonDocumentOptions { AllowTrailingCommas = true });
                    normalizedJson = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
                    if (TryExtractClickerPlanSteps(document.RootElement, window, includeWindowChrome, out List<ClickerPlanStep>? extractedSteps) && extractedSteps != null && extractedSteps.Count > 0)
                    {
                        _ = StaticLogger.LogAsync($"[Clicker] TryParseClickerPlan: parsed {extractedSteps.Count} step(s) from JSON candidate.");
                        steps = extractedSteps;
                        return true;
                    }
                }
                catch
                {
                }
            }

            if (this.Clicker.TryParseClickPoint(responseText, window, includeWindowChrome, out var command, out normalizedJson, out _)
                && command != null)
            {
                _ = StaticLogger.LogAsync("[Clicker] TryParseClickerPlan: fallback single pointer command parsed.");
                steps.Add(new ClickerPlanStep(command.Point, command.Action, null, ScreenClicker.KeyboardAction.Press, null, 0));
                return true;
            }

            _ = StaticLogger.LogAsync("[Clicker] TryParseClickerPlan: failed to parse any pointer/keyboard steps.");
            errorMessage = "No pointer or keyboard action could be parsed from the model response.";
            return false;
        }

        private bool TryParseClickerUserQuestion(string responseText, out ClickerUserQuestion? question)
        {
            question = null;

            foreach (string candidate in EnumerateJsonCandidates(responseText))
            {
                try
                {
                    using var document = JsonDocument.Parse(candidate, new JsonDocumentOptions { AllowTrailingCommas = true });
                    if (TryExtractClickerUserQuestion(document.RootElement, out question) && question != null)
                    {
                        question = question with
                        {
                            NormalizedJson = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true })
                        };
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryExtractClickerUserQuestion(JsonElement root, out ClickerUserQuestion? question)
        {
            question = null;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? text = null;
            foreach (string propertyName in new[] { "question", "ask_user", "askUser", "user_question", "prompt_user", "prompt" })
            {
                if (root.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String)
                {
                    text = property.GetString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var options = new List<string>();
            foreach (string propertyName in new[] { "options", "choices", "answers", "buttons", "selection_options" })
            {
                if (!root.TryGetProperty(propertyName, out JsonElement optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement optionElement in optionsElement.EnumerateArray())
                {
                    switch (optionElement.ValueKind)
                    {
                        case JsonValueKind.String:
                            AddClickerQuestionOption(options, optionElement.GetString());
                            break;
                        case JsonValueKind.Object:
                            foreach (string textProperty in new[] { "label", "text", "value", "option" })
                            {
                                if (optionElement.TryGetProperty(textProperty, out JsonElement labelElement) && labelElement.ValueKind == JsonValueKind.String)
                                {
                                    AddClickerQuestionOption(options, labelElement.GetString());
                                    break;
                                }
                            }
                            break;
                    }
                }
            }

            if (options.Count == 0)
            {
                return false;
            }

            string title = root.TryGetProperty("title", out JsonElement titleElement) && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString() ?? string.Empty
                : string.Empty;

            bool addTextOption = false;
            foreach (string propertyName in new[] { "addTextOption", "add_text_option", "allowTextAnswer", "allow_text_answer", "allowFreeText", "allow_free_text" })
            {
                if (root.TryGetProperty(propertyName, out JsonElement boolElement)
                    && (boolElement.ValueKind == JsonValueKind.True || boolElement.ValueKind == JsonValueKind.False))
                {
                    addTextOption = boolElement.GetBoolean();
                    break;
                }
            }

            string kind = root.TryGetProperty("kind", out JsonElement kindElement) && kindElement.ValueKind == JsonValueKind.String
                ? kindElement.GetString() ?? string.Empty
                : string.Empty;
            string textLabel = root.TryGetProperty("textLabel", out JsonElement textLabelElement) && textLabelElement.ValueKind == JsonValueKind.String
                ? textLabelElement.GetString() ?? string.Empty
                : string.Empty;
            string textPlaceholder = root.TryGetProperty("textPlaceholder", out JsonElement textPlaceholderElement) && textPlaceholderElement.ValueKind == JsonValueKind.String
                ? textPlaceholderElement.GetString() ?? string.Empty
                : string.Empty;
            string submitText = root.TryGetProperty("submitText", out JsonElement submitTextElement) && submitTextElement.ValueKind == JsonValueKind.String
                ? submitTextElement.GetString() ?? string.Empty
                : string.Empty;

            question = new ClickerUserQuestion(title, text.Trim(), options, string.Empty, kind, addTextOption, textLabel, textPlaceholder, submitText);
            return true;
        }

        private static void AddClickerQuestionOption(List<string> options, string? value)
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed) || options.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            options.Add(trimmed);
        }

        private bool TryExtractClickerPlanSteps(JsonElement root, ScreenClicker.MarkedWindowInfo window, bool includeWindowChrome, out List<ClickerPlanStep>? steps)
        {
            steps = [];

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (string propertyName in new[] { "steps", "sequence", "actions", "commands" })
                {
                    if (root.TryGetProperty(propertyName, out JsonElement arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement stepElement in arrayElement.EnumerateArray())
                        {
                            if (TryParsePlanStep(stepElement, window, includeWindowChrome, out ClickerPlanStep? parsed) && parsed != null)
                            {
                                steps.Add(parsed);
                            }
                        }

                        if (steps.Count > 0)
                        {
                            return true;
                        }
                    }
                }
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    if (TryParsePlanStep(item, window, includeWindowChrome, out ClickerPlanStep? parsed) && parsed != null)
                    {
                        steps.Add(parsed);
                    }
                }

                return steps.Count > 0;
            }

            if (TryParsePlanStep(root, window, includeWindowChrome, out ClickerPlanStep? single) && single != null)
            {
                steps.Add(single);
                return true;
            }

            return false;
        }

        private bool TryParsePlanStep(JsonElement element, ScreenClicker.MarkedWindowInfo window, bool includeWindowChrome, out ClickerPlanStep? step)
        {
            step = null;
            int delayMs = ReadDelayMs(element);

            if (element.ValueKind == JsonValueKind.Object
                && TryReadStringProperty(element, out string? pointerKind, "pointer", "action")
                && TryReadStringProperty(element, out string? elementName, "element", "target", "label")
                && !string.IsNullOrWhiteSpace(pointerKind)
                && pointerKind.Contains("click", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(elementName))
            {
                return false;
            }

            string json = JsonSerializer.Serialize(element);
            if (this.Clicker.TryParseClickPoint(json, window, includeWindowChrome, out var parsedCommand, out _, out _)
                && parsedCommand != null)
            {
                step = new ClickerPlanStep(parsedCommand.Point, parsedCommand.Action, null, ScreenClicker.KeyboardAction.Press, null, delayMs);
                return true;
            }

            if (TryReadTypeText(element, out string? typeText) && !string.IsNullOrWhiteSpace(typeText))
            {
                step = new ClickerPlanStep(null, ScreenClicker.PointerAction.Click, [], ScreenClicker.KeyboardAction.Type, typeText.Trim(), delayMs);
                return true;
            }

            if (TryReadKeys(element, out List<string>? keys) && keys != null && keys.Count > 0)
            {
                step = new ClickerPlanStep(null, ScreenClicker.PointerAction.Click, keys, ParseKeyboardAction(element), null, delayMs);
                return true;
            }

            if (delayMs > 0)
            {
                step = new ClickerPlanStep(null, ScreenClicker.PointerAction.Click, null, ScreenClicker.KeyboardAction.Press, null, delayMs);
                return true;
            }

            return false;
        }

        private static bool TryReadStringProperty(JsonElement element, out string? value, params string[] names)
        {
            value = null;
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
                {
                    value = property.GetString();
                    return !string.IsNullOrWhiteSpace(value);
                }
            }

            return false;
        }

        private static string DescribeClickerPlan(IReadOnlyList<ClickerPlanStep> steps)
        {
            if (steps.Count == 0)
            {
                return string.Empty;
            }

            string summary = string.Join(" -> ", steps.Take(8).Select(s => s.Describe()));
            return TrimForHistory(summary);
        }

        private static string DescribeClickerPlanStep(ClickerPlanStep step)
        {
            return $"{step.Describe()}, delay_ms={step.DelayMs}";
        }

        private static bool TryReadTypeText(JsonElement element, out string? text)
        {
            text = null;
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (string propertyName in new[] { "type_text", "input_text", "text_input" })
            {
                if (element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String)
                {
                    text = property.GetString();
                    return !string.IsNullOrWhiteSpace(text);
                }
            }

            return false;
        }

        private static bool TryReadKeys(JsonElement element, out List<string>? keys)
        {
            keys = [];
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (string propertyName in new[] { "keys", "combo", "shortcut", "hotkey" })
            {
                if (!element.TryGetProperty(propertyName, out JsonElement property))
                {
                    continue;
                }

                if (property.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in property.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            AddKeys(keys, item.GetString());
                        }
                    }
                }
                else if (property.ValueKind == JsonValueKind.String)
                {
                    AddKeys(keys, property.GetString());
                }
            }

            if (keys.Count == 0 && element.TryGetProperty("key", out JsonElement keyProperty) && keyProperty.ValueKind == JsonValueKind.String)
            {
                AddKeys(keys, keyProperty.GetString());
            }

            return keys.Count > 0;
        }

        private static void AddKeys(List<string> keys, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string trimmed = value.Trim();
            if (trimmed is "+" or "-" or "*" or "/")
            {
                if (!keys.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    keys.Add(trimmed);
                }

                return;
            }

            foreach (string piece in trimmed.Split(new[] { '+', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!keys.Contains(piece, StringComparer.OrdinalIgnoreCase))
                {
                    keys.Add(piece);
                }
            }
        }

        private static ScreenClicker.KeyboardAction ParseKeyboardAction(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return ScreenClicker.KeyboardAction.Press;
            }

            foreach (string propertyName in new[] { "action", "keyboard_action", "key_action", "event" })
            {
                if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string action = property.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
                if (action is "keydown" or "key-down" or "key_down" or "down")
                {
                    return ScreenClicker.KeyboardAction.Down;
                }

                if (action is "keyup" or "key-up" or "key_up" or "up")
                {
                    return ScreenClicker.KeyboardAction.Up;
                }

                if (action is "type" or "type_text" or "text")
                {
                    return ScreenClicker.KeyboardAction.Type;
                }
            }

            return ScreenClicker.KeyboardAction.Press;
        }

        private static int ReadDelayMs(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            foreach (string propertyName in new[] { "delay_ms", "delayMs", "wait_ms", "sleep_ms" })
            {
                if (element.TryGetProperty(propertyName, out JsonElement property)
                    && TryReadDouble(property, out double delay)
                    && delay > 0)
                {
                    return Math.Clamp((int)Math.Round(delay), 1, 30000);
                }
            }

            foreach (string propertyName in new[] { "delay_seconds", "delaySeconds", "wait_seconds" })
            {
                if (element.TryGetProperty(propertyName, out JsonElement property)
                    && TryReadDouble(property, out double seconds)
                    && seconds > 0)
                {
                    return Math.Clamp((int)Math.Round(seconds * 1000.0), 1, 30000);
                }
            }

            return 0;
        }

        private static bool TryReadDouble(JsonElement element, out double value)
        {
            value = 0;
            if (element.ValueKind == JsonValueKind.Number)
            {
                return element.TryGetDouble(out value);
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(element.GetString(), out value);
            }

            return false;
        }

        private static IEnumerable<string> EnumerateJsonCandidates(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            string trimmed = text.Trim();
            yield return trimmed;

            int firstBrace = trimmed.IndexOf('{');
            int lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                yield return trimmed[firstBrace..(lastBrace + 1)];
            }
        }

        private sealed record ClickerPlanStep(
            ScreenClicker.ClickPoint? Pointer,
            ScreenClicker.PointerAction PointerAction,
            List<string>? Keys,
            ScreenClicker.KeyboardAction KeyboardAction,
            string? TypeText,
            int DelayMs)
        {
            public bool IsPointer => this.Pointer != null;
            public bool IsKeyboard => !this.IsPointer && (!string.IsNullOrWhiteSpace(this.TypeText) || (this.Keys?.Count ?? 0) > 0);

            public string Describe()
            {
                if (this.IsPointer && this.Pointer != null)
                {
                    return $"{this.PointerAction} @{this.Pointer.X:0.##},{this.Pointer.Y:0.##}";
                }

                if (!string.IsNullOrWhiteSpace(this.TypeText))
                {
                    return $"Type \"{TrimForHistory(this.TypeText)}\"";
                }

                if ((this.Keys?.Count ?? 0) > 0)
                {
                    return $"Key {this.KeyboardAction} {string.Join("+", this.Keys!)}";
                }

                return this.DelayMs > 0 ? $"Delay {this.DelayMs}ms" : "Step";
            }
        }

        private sealed record ClickerUserQuestion(string Title, string Question, List<string> Options, string NormalizedJson, string Kind, bool AddTextOption, string TextLabel, string TextPlaceholder, string SubmitText);

        private async Task RestorePromptInputFocusAsync()
        {
            try
            {
                await Task.Delay(25);
                await this.Js.InvokeVoidAsync("sharpestNavMenu.focusElementIfExists", "promptInput");
            }
            catch
            {
            }
        }

        private void AppendClickerHistory(string entry)
        {
            string trimmed = entry?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            this.clickerHistory.Add(trimmed);
            while (this.clickerHistory.Count > ClickerHistoryMaxEntries)
            {
                this.clickerHistory.RemoveAt(0);
            }
        }

        private static string ExtractPreferredReason(string normalizedJson, string rawResponse)
        {
            if (!string.IsNullOrWhiteSpace(normalizedJson))
            {
                try
                {
                    using var document = JsonDocument.Parse(normalizedJson);
                    if (TryExtractReasonRecursive(document.RootElement, out string? reason) && !string.IsNullOrWhiteSpace(reason))
                    {
                        return reason.Trim();
                    }
                }
                catch
                {
                }
            }

            return string.IsNullOrWhiteSpace(rawResponse)
                ? string.Empty
                : rawResponse.Trim();
        }

        private static bool TryExtractReasonRecursive(JsonElement element, out string? reason)
        {
            reason = null;

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (string name in new[] { "reason", "rationale", "explanation", "thought", "comment" })
                    {
                        if (element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
                        {
                            reason = property.GetString();
                            return !string.IsNullOrWhiteSpace(reason);
                        }
                    }

                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (TryExtractReasonRecursive(property.Value, out reason))
                        {
                            return true;
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement child in element.EnumerateArray())
                    {
                        if (TryExtractReasonRecursive(child, out reason))
                        {
                            return true;
                        }
                    }
                    break;

                case JsonValueKind.String:
                    reason = element.GetString();
                    return !string.IsNullOrWhiteSpace(reason);
            }

            return false;
        }

        private static string TrimForHistory(string? value)
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Length <= 180)
            {
                return trimmed;
            }

            return trimmed[..177] + "...";
        }

        private async Task<(bool Success, ScreenClicker.MarkedWindowInfo? Window)> TryPrepareClickerScreenshotAsync(ScreenClicker.MarkedWindowInfo window, CancellationToken cancellationToken)
        {
            if (!this.Clicker.TryRefreshWindow(window, out var refreshedWindow) || refreshedWindow == null)
            {
                this.LastActionMessage = "The marked window is no longer available.";
                this.clickerMarkedWindow = null;
                return (false, null);
            }

            window = refreshedWindow;
            this.clickerMarkedWindow = refreshedWindow;

            if (this.ClickerActivateWindowBeforeCapture)
            {
                this.Clicker.TryActivateWindow(window);
                await Task.Delay(320, cancellationToken);
            }

            string screenshotDirectory = Path.Combine(Path.GetTempPath(), "SharpestLlmStudio", "clicker");
            string? screenshotPath = await this.Clicker.CaptureWindowToPngAsync(window, screenshotDirectory, this.ClickerIncludeWindowChrome, cancellationToken);
            if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
            {
                this.LastActionMessage = "Could not capture the marked window.";
                return (false, null);
            }

            screenshotPath = await this.TryCropClickerScreenshotToInteractionRegionAsync(window, screenshotPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
            {
                this.LastActionMessage = "Could not crop the marked window screenshot.";
                return (false, null);
            }

            this.DeleteLastClickerScreenshot();
            this.clickerLastScreenshotPath = screenshotPath;
            this.ClickerScreenshotDataUrl = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(screenshotPath, cancellationToken));
            this.RequestUiRefresh();
            return (true, window);
        }

        private async Task CaptureProtectedZoneFromMarkedWindowAsync(ScreenClicker.MarkedWindowInfo window, CancellationToken cancellationToken)
        {
            Rectangle effectiveBounds = this.GetClickerEffectiveReferenceBounds(window, this.ClickerIncludeWindowChrome);
            if (effectiveBounds.Width <= 1 || effectiveBounds.Height <= 1)
            {
                this.IsClickerProtectedZoneSelectionActive = false;
                this.LastActionMessage = "Interaction region is invalid for protected zone capture.";
                this.RequestUiRefresh();
                return;
            }

            this.LastActionMessage = "Protected zone: click the first corner inside the marked window.";
            this.RequestUiRefresh();
            Point? first = await this.WaitForProtectedZonePointAsync(window, effectiveBounds, cancellationToken);
            if (!first.HasValue)
            {
                this.IsClickerProtectedZoneSelectionActive = false;
                this.LastActionMessage = "Protected zone selection canceled or timed out.";
                this.RequestUiRefresh();
                return;
            }

            this.LastActionMessage = "Protected zone: click the opposite corner inside the marked window.";
            this.RequestUiRefresh();
            Point? second = await this.WaitForProtectedZonePointAsync(window, effectiveBounds, cancellationToken);
            if (!second.HasValue)
            {
                this.IsClickerProtectedZoneSelectionActive = false;
                this.LastActionMessage = "Protected zone selection canceled or timed out.";
                this.RequestUiRefresh();
                return;
            }

            int left = Math.Min(first.Value.X, second.Value.X);
            int top = Math.Min(first.Value.Y, second.Value.Y);
            int right = Math.Max(first.Value.X, second.Value.X);
            int bottom = Math.Max(first.Value.Y, second.Value.Y);
            int width = Math.Max(1, right - left);
            int height = Math.Max(1, bottom - top);

            int normalizedLeft = Math.Clamp((int)Math.Round(((left - effectiveBounds.Left) / (double)effectiveBounds.Width) * 1000.0), 0, 999);
            int normalizedTop = Math.Clamp((int)Math.Round(((top - effectiveBounds.Top) / (double)effectiveBounds.Height) * 1000.0), 0, 999);
            int normalizedRight = Math.Clamp((int)Math.Round(((right - effectiveBounds.Left) / (double)effectiveBounds.Width) * 1000.0), normalizedLeft + 1, 1000);
            int normalizedBottom = Math.Clamp((int)Math.Round(((bottom - effectiveBounds.Top) / (double)effectiveBounds.Height) * 1000.0), normalizedTop + 1, 1000);

            this.clickerProtectedZones.Add(new ClickerProtectedZone
            {
                Id = Guid.NewGuid(),
                Name = this.GetNextClickerProtectedZoneName(),
                IncludeWindowChrome = this.ClickerIncludeWindowChrome,
                LeftNormalized = normalizedLeft,
                TopNormalized = normalizedTop,
                WidthNormalized = Math.Max(1, normalizedRight - normalizedLeft),
                HeightNormalized = Math.Max(1, normalizedBottom - normalizedTop)
            });

            this.IsClickerProtectedZoneSelectionActive = false;
            await this.RefreshClickerPreviewFromMarkedWindowAsync(cancellationToken);
            this.LastActionMessage = "Protected zone added.";
            this.RequestUiRefresh();
        }

        private async Task<Point?> WaitForProtectedZonePointAsync(ScreenClicker.MarkedWindowInfo window, Rectangle effectiveBounds, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                Point? point = await this.Clicker.WaitForNextLeftClickAsync(TimeSpan.FromSeconds(25), cancellationToken);
                if (!point.HasValue)
                {
                    return null;
                }

                if (!this.Clicker.TryRefreshWindow(window, out var refreshedWindow) || refreshedWindow == null)
                {
                    return null;
                }

                this.clickerMarkedWindow = refreshedWindow;
                effectiveBounds = this.GetClickerEffectiveReferenceBounds(refreshedWindow, this.ClickerIncludeWindowChrome);
                if (effectiveBounds.Contains(point.Value) && !this.Clicker.IsTaskbarOrShellWindow(this.Clicker.GetCurrentForegroundWindowHandle()))
                {
                    return point;
                }

                this.LastActionMessage = "Click landed outside the active marked window region. Try again.";
                this.RequestUiRefresh();
            }

            return null;
        }

        private bool TryFindBlockingProtectedZone(ScreenClicker.MarkedWindowInfo window, Point screenPoint, bool includeWindowChrome, out ClickerProtectedZone? protectedZone)
        {
            protectedZone = null;
            Rectangle referenceBounds = this.GetClickerEffectiveReferenceBounds(window, includeWindowChrome);
            if (referenceBounds.Width <= 0 || referenceBounds.Height <= 0)
            {
                return false;
            }

            int normalizedX = (int)Math.Round(((screenPoint.X - referenceBounds.Left) / (double) referenceBounds.Width) * 1000.0);
            int normalizedY = (int)Math.Round(((screenPoint.Y - referenceBounds.Top) / (double) referenceBounds.Height) * 1000.0);

            protectedZone = this.clickerProtectedZones.FirstOrDefault(zone =>
                zone.IncludeWindowChrome == includeWindowChrome
                && normalizedX >= zone.LeftNormalized
                && normalizedX <= zone.RightNormalized
                && normalizedY >= zone.TopNormalized
                && normalizedY <= zone.BottomNormalized);

            return protectedZone != null;
        }

        private async Task<string?> TryCropClickerScreenshotToInteractionRegionAsync(ScreenClicker.MarkedWindowInfo window, string screenshotPath, CancellationToken cancellationToken)
        {
            if (!this.ClickerLimitInteractionRegion)
            {
                return screenshotPath;
            }

            Rectangle fullBounds = this.Clicker.GetReferenceBounds(window, this.ClickerIncludeWindowChrome);
            Rectangle limitedBounds = this.GetClickerEffectiveReferenceBounds(window, this.ClickerIncludeWindowChrome);
            if (limitedBounds == fullBounds)
            {
                return screenshotPath;
            }

            try
            {
                using var bitmap = new Bitmap(screenshotPath);
                Rectangle crop = new(
                    Math.Max(0, limitedBounds.Left - fullBounds.Left),
                    Math.Max(0, limitedBounds.Top - fullBounds.Top),
                    Math.Min(bitmap.Width, limitedBounds.Width),
                    Math.Min(bitmap.Height, limitedBounds.Height));

                if (crop.Width <= 0 || crop.Height <= 0 || crop.Right > bitmap.Width || crop.Bottom > bitmap.Height)
                {
                    return screenshotPath;
                }

                using var cropped = bitmap.Clone(crop, bitmap.PixelFormat);
                string croppedPath = Path.Combine(Path.GetDirectoryName(screenshotPath)!, Path.GetFileNameWithoutExtension(screenshotPath) + "_region.png");
                cropped.Save(croppedPath, System.Drawing.Imaging.ImageFormat.Png);
                await Task.CompletedTask;

                try
                {
                    File.Delete(screenshotPath);
                }
                catch
                {
                }

                await StaticLogger.LogAsync($"[Clicker] Cropped screenshot to limited interaction region: {crop.X},{crop.Y} {crop.Width}x{crop.Height}");
                return croppedPath;
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[Clicker] Could not crop screenshot to interaction region");
                return screenshotPath;
            }
        }

        private async Task CancelClickerProtectedZoneSelectionAsync(string message)
        {
            try
            {
                await this.Js.InvokeVoidAsync("sharpestNavMenu.cancelClickerProtectedZoneSelection");
            }
            catch
            {
            }

            this.IsClickerProtectedZoneSelectionActive = false;
            this.LastActionMessage = message;
            this.RequestUiRefresh();
        }

        private void ClearClickerProtectedZonesInternal()
        {
            this.clickerProtectedZones.Clear();
        }

        private string GetNextClickerProtectedZoneName()
        {
            HashSet<int> usedNumbers = this.clickerProtectedZones
                .Select(zone => int.TryParse(zone.Name, out int number) && number >= 0 ? number : (int?) null)
                .Where(number => number.HasValue)
                .Select(number => number!.Value)
                .ToHashSet();

            int nextNumber = 0;
            while (usedNumbers.Contains(nextNumber))
            {
                nextNumber++;
            }

            return nextNumber.ToString();
        }

        private string GetClickerProtectedZoneModelWarning()
        {
            return string.IsNullOrWhiteSpace(this.Settings.ClickerProtectedZoneModelWarning)
                ? DefaultClickerProtectedZoneModelWarning
                : this.Settings.ClickerProtectedZoneModelWarning.Trim();
        }

        private void DeleteLastClickerScreenshot()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(this.clickerLastScreenshotPath) && File.Exists(this.clickerLastScreenshotPath))
                {
                    File.Delete(this.clickerLastScreenshotPath);
                }
            }
            catch
            {
            }

            this.clickerLastScreenshotPath = null;
        }

        private async Task<string> AwaitClickerConfirmationAsync(Point screenPoint, CancellationToken cancellationToken)
        {
            await StaticLogger.LogAsync($"[Clicker] Awaiting click confirmation for target {screenPoint.X},{screenPoint.Y}. Loop running: {this.IsClickerLoopRunning}.");

            try
            {
                if (!await this.HasSharpestNavMenuFunctionAsync("awaitClickerConfirmation"))
                {
                    await StaticLogger.LogAsync("[Clicker] sharpestNavMenu.awaitClickerConfirmation is unavailable. Falling back to the browser confirm dialog.");

                    bool confirmed = await this.Js.InvokeAsync<bool>(
                        "confirm",
                        cancellationToken,
                        $"Confirm pointer action at {screenPoint.X}, {screenPoint.Y}?\n\nOK = confirm\nCancel = deny");

                    string fallbackResult = confirmed ? "confirm" : "deny";
                    await StaticLogger.LogAsync($"[Clicker] Browser confirm fallback returned '{fallbackResult}' for target {screenPoint.X},{screenPoint.Y}.");
                    return fallbackResult;
                }

                string result = await this.Js.InvokeAsync<string>(
                    "sharpestNavMenu.awaitClickerConfirmation",
                    cancellationToken,
                    new object[] { screenPoint.X, screenPoint.Y, this.IsClickerLoopRunning });

                await StaticLogger.LogAsync($"[Clicker] Confirmation popup returned '{result}' for target {screenPoint.X},{screenPoint.Y}.");
                return result;
            }
            catch (JSException ex)
            {
                await StaticLogger.LogAsync(ex, "[Clicker] Confirmation popup JS call failed. Falling back to browser confirm if possible.");

                bool confirmed = await this.Js.InvokeAsync<bool>(
                    "confirm",
                    cancellationToken,
                    $"Confirm pointer action at {screenPoint.X}, {screenPoint.Y}?\n\nOK = confirm\nCancel = deny");

                string fallbackResult = confirmed ? "confirm" : "deny";
                await StaticLogger.LogAsync($"[Clicker] Browser confirm fallback after JS exception returned '{fallbackResult}' for target {screenPoint.X},{screenPoint.Y}.");
                return fallbackResult;
            }
            catch (OperationCanceledException)
            {
                if (await this.HasSharpestNavMenuFunctionAsync("dismissClickerConfirmation"))
                {
                    try
                    {
                        await this.Js.InvokeVoidAsync("sharpestNavMenu.dismissClickerConfirmation");
                    }
                    catch
                    {
                    }
                }

                await StaticLogger.LogAsync($"[Clicker] Confirmation request canceled for target {screenPoint.X},{screenPoint.Y}.");
                throw;
            }
        }

        private async Task<bool> HasSharpestNavMenuFunctionAsync(string functionName)
        {
            try
            {
                return await this.Js.InvokeAsync<bool>("sharpestNavMenu.hasFunction", $"sharpestNavMenu.{functionName}");
            }
            catch
            {
                return false;
            }
        }

        private void DisposeClickerResources()
        {
            try
            {
                this.clickerLoopCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                this.clickerLoopCts?.Dispose();
            }
            catch
            {
            }

            try
            {
                this.clickerPreviewRefreshCts?.Cancel();
                this.clickerPreviewRefreshCts?.Dispose();
            }
            catch
            {
            }

            try
            {
                _ = this.Js.InvokeVoidAsync("sharpestNavMenu.dismissClickerConfirmation");
            }
            catch
            {
            }

            try
            {
                _ = this.Js.InvokeVoidAsync("sharpestNavMenu.cancelClickerProtectedZoneSelection");
            }
            catch
            {
            }

            this.clickerLoopCts = null;
            this.clickerPreviewRefreshCts = null;
            this.IsClickerProtectedZoneSelectionActive = false;
            this.CancelPendingClickerQuestion();
            this.Clicker.ReleaseHeldInputs();
            this.DeleteLastClickerScreenshot();
        }

        public sealed class ClickerProtectedZone
        {
            public Guid Id { get; init; }
            public string Name { get; set; } = string.Empty;
            public bool IncludeWindowChrome { get; init; }
            public int LeftNormalized { get; init; }
            public int TopNormalized { get; init; }
            public int WidthNormalized { get; init; }
            public int HeightNormalized { get; init; }
            public int RightNormalized => Math.Min(1000, this.LeftNormalized + this.WidthNormalized);
            public int BottomNormalized => Math.Min(1000, this.TopNormalized + this.HeightNormalized);
            public double LeftPercent => this.LeftNormalized / 10.0;
            public double TopPercent => this.TopNormalized / 10.0;
            public double WidthPercent => this.WidthNormalized / 10.0;
            public double HeightPercent => this.HeightNormalized / 10.0;
            public string ScopeLabel => this.IncludeWindowChrome ? "Window" : "Client";
        }
    }
}
