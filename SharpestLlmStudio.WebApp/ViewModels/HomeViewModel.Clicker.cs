using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using SharpestLlmStudio.Monitoring;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.WebApp.ViewModels
{
    public partial class HomeViewModel
    {
        private readonly ScreenClicker Clicker;
        private readonly List<string> clickerHistory = [];
        private readonly List<ClickerProtectedZone> clickerProtectedZones = [];
        private ScreenClicker.MarkedWindowInfo? clickerMarkedWindow;
        private CancellationTokenSource? clickerLoopCts;
        private string? clickerLastScreenshotPath;
        private const int ClickerHistoryMaxEntries = 8;
        private const string ClickerPreviewStageElementId = "clicker-preview-stage";
        private const string DefaultClickerProtectedZoneModelWarning = "Protected zones mark areas the user does not want clicked. Do not choose targets inside those protected zones.";

        public string ClickerInstructions { get; set; } = "Click the next required UI element for the described task. Prefer the primary actionable control and avoid decorative elements.";
        public int ClickerLoopIntervalMs { get; set; } = 2500;
        public int ClickerWindowMarkDelaySeconds { get; set; } = 3;
        public int ClickerPreviewScalePercent { get; set; } = 30;
        public bool ClickerIncludeWindowChrome { get; set; } = false;
        public bool ClickerShowPreviewMarker { get; set; } = true;
        public bool ClickerActivateWindowBeforeCapture { get; set; } = true;
        public bool ClickerUseBackgroundClick { get; set; } = true;
        public bool ClickerDryRun { get; set; } = false;
        public bool ClickerConfirmBeforeClick { get; set; } = false;
        public bool ClickerRequirePointInsideWindow { get; set; } = true;
        public bool ClickerUseChatInputAsLiveNote { get; set; } = true;
        public bool ClickerTellModelAboutProtectedZones { get; set; } = false;
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
        public bool HasClickerPreviewMarker => this.ClickerPreviewMarkerLeftPercent.HasValue && this.ClickerPreviewMarkerTopPercent.HasValue;
        public IEnumerable<ClickerProtectedZone> ClickerProtectedZones => this.clickerProtectedZones
            .OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase);
        public IEnumerable<ClickerProtectedZone> VisibleClickerProtectedZones => this.clickerProtectedZones
            .Where(z => z.IncludeWindowChrome == this.ClickerIncludeWindowChrome)
            .OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase);
        public int ClickerProtectedZoneCount => this.clickerProtectedZones.Count;

        [SupportedOSPlatform("windows")]
        public async Task MarkClickerWindowAsync()
        {
            if (this.IsClickerBusy)
            {
                return;
            }

            int delaySeconds = Math.Clamp(this.ClickerWindowMarkDelaySeconds, 1, 15);
            this.LastActionMessage = $"Switch to the target window. Marking foreground window in {delaySeconds} second(s)...";
            this.RequestUiRefresh();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
            catch
            {
                return;
            }

            if (this.Clicker.TryMarkForegroundWindow(out var window) && window != null)
            {
                bool windowChanged = this.clickerMarkedWindow == null || this.clickerMarkedWindow.Handle != window.Handle;
                this.clickerMarkedWindow = window;
                if (windowChanged)
                {
                    this.ClearClickerProtectedZonesInternal();
                }
                this.LastActionMessage = $"Marked window: {window.DisplayLabel}";
            }
            else
            {
                this.LastActionMessage = "Could not mark the foreground window.";
            }

            this.RequestUiRefresh();
        }

        public void ClearClickerWindow()
        {
            this.Clicker.ReleaseHeldPointer();
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
            this.ClickerPreviewMarkerLeftPercent = null;
            this.ClickerPreviewMarkerTopPercent = null;
            this.clickerHistory.Clear();
            this.ClearClickerProtectedZonesInternal();
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
            CancellationTokenSource loopCts = this.clickerLoopCts;
            CancellationToken token = loopCts.Token;
            this.ClickerIterationCount = 0;
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

            this.Clicker.ReleaseHeldPointer();

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
                this.LastActionMessage = "Load a model before starting the Clicker.";
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

            try
            {
                if (this.ClickerUseBackgroundClick)
                {
                    try
                    {
                        shouldRestorePromptFocus = await this.Js.InvokeAsync<bool>("sharpestNavMenu.isElementFocused", "promptInput");
                    }
                    catch
                    {
                    }
                }

                var prepared = await this.TryPrepareClickerScreenshotAsync(window, cancellationToken);
                if (!prepared.Success || prepared.Window == null)
                {
                    return;
                }

                window = prepared.Window;

                string clickerSystemPrompt = this.BuildEffectiveSystemPrompt(
                    "You are a careful UI pointer planner. Study the screenshot, the user goal, the current pointer state, protected zones, and the recent iteration history before deciding. Learn from previous successful or failed attempts instead of repeating them blindly. Return exactly one JSON object only. Include an optional action field. Supported actions are click, doubleclick, down, and up. If action is omitted, empty, or null, it means click. Use down and up to support drag-and-drop workflows. Prefer {\"point_2d\":[x,y],\"action\":\"click\",\"reason\":\"...\"}. Coordinates must refer to the attached screenshot and should usually be normalized to the 0..1000 scale. The reason should be grounded in visible UI evidence and should explain why this action advances the task, especially with respect to recent iteration history. If no safe target exists, return {\"point_2d\":[-1,-1],\"action\":null,\"reason\":\"not found\"}.");

                string? liveOperatorNote = this.ClickerUseChatInputAsLiveNote
                    ? this.UserInput?.Trim()
                    : null;

                string clickerPrompt = this.BuildClickerPrompt(window, liveOperatorNote);
                var request = new LlamaGenerationRequest
                {
                    Prompt = clickerPrompt,
                    SystemPrompt = clickerSystemPrompt,
                    Images = [this.clickerLastScreenshotPath!],
                    Isolated = true,
                    PersistConversation = false,
                    IncludeConversationHistory = false,
                    MaxTokens = 384,
                    Temperature = 0.15,
                    TopP = 0.9,
                    TopK = 40,
                    MaxWidthAndHeight = 0,
                    ImageFormat = "png",
                    Stream = false
                };

                var sb = new StringBuilder();
                await foreach (string chunk in this.Client.GenerateAsync(request, cancellationToken))
                {
                    sb.Append(chunk);
                }

                string responseText = sb.ToString().Trim();
                this.ClickerLastResponse = responseText;
                this.ClickerLastRunAtUtc = DateTime.UtcNow;

                if (!this.Clicker.TryParseClickPoint(responseText, window, this.ClickerIncludeWindowChrome, out var parsedCommand, out string normalizedJson, out string errorMessage) || parsedCommand == null)
                {
                    this.ClickerLastParsedPoint = string.Empty;
                    this.ClickerLastParsedAction = string.Empty;
                    this.ClickerLastNormalizedJson = normalizedJson;
                    this.ClickerLastReason = string.IsNullOrWhiteSpace(responseText)
                        ? string.Empty
                        : responseText;
                    this.AppendClickerHistory($"Model response could not be parsed: {TrimForHistory(responseText)}");
                    this.LastActionMessage = $"Clicker could not parse a valid point: {errorMessage}";
                    await StaticLogger.LogAsync($"[Clicker] Raw unparseable model response: {TrimForHistory(responseText)}");
                    return;
                }

                var parsedPoint = parsedCommand.Point;
                var parsedAction = parsedCommand.Action;
                this.ClickerLastParsedPoint = parsedPoint.DisplayLabel;
                this.ClickerLastParsedAction = parsedAction.ToString();
                this.ClickerLastNormalizedJson = normalizedJson;
                this.ClickerLastReason = ExtractPreferredReason(normalizedJson, responseText);
                await StaticLogger.LogAsync($"[Clicker] Parsed pointer command: action={parsedAction}, point={parsedPoint.DisplayLabel}, reason={TrimForHistory(this.ClickerLastReason)}");

                if (parsedPoint.X < 0 || parsedPoint.Y < 0)
                {
                    this.AppendClickerHistory($"No safe target found. Reason: {TrimForHistory(this.ClickerLastReason)}");
                    this.LastActionMessage = "Clicker response indicates that no safe target was found.";
                    await StaticLogger.LogAsync("[Clicker] Model returned a sentinel/no-target point. Execution skipped.");
                    return;
                }

                Point screenPoint = this.Clicker.ConvertToScreenPoint(window, parsedPoint, this.ClickerIncludeWindowChrome);
                this.ClickerLastTargetScreenPoint = $"{screenPoint.X}, {screenPoint.Y}";

                var referenceBounds = this.Clicker.GetReferenceBounds(window, this.ClickerIncludeWindowChrome);
                double markerLeft = referenceBounds.Width > 0
                    ? ((screenPoint.X - referenceBounds.Left) / (double) referenceBounds.Width) * 100.0
                    : 0.0;
                double markerTop = referenceBounds.Height > 0
                    ? ((screenPoint.Y - referenceBounds.Top) / (double) referenceBounds.Height) * 100.0
                    : 0.0;
                this.ClickerPreviewMarkerLeftPercent = Math.Clamp(markerLeft, 0.0, 100.0);
                this.ClickerPreviewMarkerTopPercent = Math.Clamp(markerTop, 0.0, 100.0);

                if (this.ClickerRequirePointInsideWindow && !this.Clicker.IsScreenPointInsideWindow(window, screenPoint, this.ClickerIncludeWindowChrome, margin: 2))
                {
                    this.AppendClickerHistory($"Rejected out-of-bounds target {this.ClickerLastParsedPoint}. Reason: {TrimForHistory(this.ClickerLastReason)}");
                    this.LastActionMessage = "Clicker rejected the target because it lies outside the marked window.";
                    await StaticLogger.LogAsync($"[Clicker] Rejected target outside window bounds: action={parsedAction}, screenPoint={screenPoint.X},{screenPoint.Y}, window='{window.DisplayLabel}'.");
                    return;
                }

                if (this.TryFindBlockingProtectedZone(window, screenPoint, this.ClickerIncludeWindowChrome, out ClickerProtectedZone? protectedZone) && protectedZone != null)
                {
                    this.ClickerLastConfirmationOutcome = "Protected zone";
                    this.AppendClickerHistory($"Blocked {this.ClickerLastParsedAction} target {this.ClickerLastParsedPoint} because it falls inside protected zone '{protectedZone.Name}'.");
                    this.LastActionMessage = $"Clicker blocked the {this.ClickerLastParsedAction.ToLowerInvariant()} because it falls inside protected zone '{protectedZone.Name}'.";
                    await StaticLogger.LogAsync($"[Clicker] Blocked target inside protected zone '{protectedZone.Name}': action={parsedAction}, screenPoint={screenPoint.X},{screenPoint.Y}.");
                    return;
                }

                this.ClickerIterationCount++;

                if (this.ClickerDryRun)
                {
                    this.AppendClickerHistory($"Dry run {this.ClickerLastParsedAction} target {this.ClickerLastParsedPoint}. Reason: {TrimForHistory(this.ClickerLastReason)}");
                    this.ClickerLastConfirmationOutcome = "Dry run";
                    this.LastActionMessage = $"Dry run: parsed {this.ClickerLastParsedAction.ToLowerInvariant()} target at {screenPoint.X}, {screenPoint.Y}.";
                    return;
                }

                if (this.ClickerConfirmBeforeClick)
                {
                    string confirmation = await this.AwaitClickerConfirmationAsync(screenPoint, cancellationToken);
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

                string executionMode = this.ClickerUseBackgroundClick
                    ? (this.ClickerActivateWindowBeforeCapture && (parsedAction == ScreenClicker.PointerAction.Click || parsedAction == ScreenClicker.PointerAction.DoubleClick)
                        ? "background-with-foreground-fallback"
                        : "background-postmessage")
                    : "foreground";

                await StaticLogger.LogAsync($"[Clicker] Executing pointer action: action={parsedAction}, screenPoint={screenPoint.X},{screenPoint.Y}, mode={executionMode}, background={this.ClickerUseBackgroundClick}, activateWindow={this.ClickerActivateWindowBeforeCapture}, includeChrome={this.ClickerIncludeWindowChrome}.");

                if (this.Clicker.TryExecutePointerAction(window, parsedPoint, parsedAction, out screenPoint, this.ClickerActivateWindowBeforeCapture, this.ClickerIncludeWindowChrome, this.ClickerUseBackgroundClick))
                {
                    string actionText = this.ClickerLastParsedAction.ToLowerInvariant();
                    this.AppendClickerHistory($"Executed {actionText} on {this.ClickerLastParsedPoint} at {screenPoint.X},{screenPoint.Y}. Reason: {TrimForHistory(this.ClickerLastReason)}");
                    this.LastActionMessage = $"Clicker executed {actionText} at {screenPoint.X}, {screenPoint.Y}.";
                    await StaticLogger.LogAsync($"[Clicker] Pointer action succeeded: action={actionText}, screenPoint={screenPoint.X},{screenPoint.Y}. Held={this.Clicker.IsLeftButtonHeld}.");
                }
                else
                {
                    string actionText = this.ClickerLastParsedAction.ToLowerInvariant();
                    this.AppendClickerHistory($"{actionText} failed for target {this.ClickerLastParsedPoint}. Reason: {TrimForHistory(this.ClickerLastReason)}");
                    this.LastActionMessage = $"Clicker could not perform the {actionText}.";
                    await StaticLogger.LogAsync($"[Clicker] Pointer action failed: action={actionText}, screenPoint={screenPoint.X},{screenPoint.Y}, background={this.ClickerUseBackgroundClick}, held={this.Clicker.IsLeftButtonHeld}.");
                }
            }
            catch (OperationCanceledException)
            {
                this.AppendClickerHistory("Clicker iteration canceled.");
                this.LastActionMessage = "Clicker iteration canceled.";
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[Clicker] Iteration failed");
                this.LastActionMessage = $"Clicker failed: {ex.Message}";
            }
            finally
            {
                this.IsClickerBusy = false;
                this.RequestUiRefresh();

                if (shouldRestorePromptFocus && this.ClickerUseBackgroundClick)
                {
                    await this.RestorePromptInputFocusAsync();
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

        private string BuildClickerPrompt(ScreenClicker.MarkedWindowInfo window, string? liveOperatorNote)
        {
            string historyText = string.Join("\n", this.clickerHistory
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .TakeLast(ClickerHistoryMaxEntries)
                .Select(s => $"- {s}"));

            string protectedZonePrompt = string.Empty;
            List<ClickerProtectedZone> promptZones = this.VisibleClickerProtectedZones.ToList();
            if (this.ClickerTellModelAboutProtectedZones && promptZones.Count > 0)
            {
                string warning = this.GetClickerProtectedZoneModelWarning();
                string zoneLines = string.Join("\n", promptZones.Select(z => $"- {z.Name}: [{z.LeftNormalized}, {z.TopNormalized}, {z.RightNormalized}, {z.BottomNormalized}] in 0..1000 coordinates"));
                protectedZonePrompt = $"Protected zones:\n{warning}\n{zoneLines}\n\n";
            }

            return "Analyze the attached screenshot of the marked application window.\n\n"
                + $"User goal / procedure:\n{this.ClickerInstructions.Trim()}\n\n"
                + (string.IsNullOrWhiteSpace(liveOperatorNote)
                    ? string.Empty
                    : $"Operator live note / current chat input:\n{liveOperatorNote.Trim()}\n\n")
                + $"Current pointer state:\n- Left mouse button currently held: {(this.Clicker.IsLeftButtonHeld ? "yes" : "no")}\n\n"
                + (string.IsNullOrWhiteSpace(historyText)
                    ? string.Empty
                    : $"Recent clicker context:\n{historyText}\n\n")
                + protectedZonePrompt
                + "Window info:\n"
                + $"- Title: {window.Title}\n"
                + $"- Bounds: {window.Left},{window.Top} {window.Width}x{window.Height}\n\n"
                + $"Screenshot scope: {(this.ClickerIncludeWindowChrome ? "full window including frame/title bar" : "client content only; ignore frame/title bar")}\n\n"
                + "Rules:\n"
                + "- Return JSON only.\n"
                + "- Preferred format: {\"point_2d\":[x,y],\"action\":\"click\",\"reason\":\"detailed grounded reason\"}.\n"
                + "- Use coordinates relative to the attached screenshot.\n"
                + "- Prefer x and y values in the 0..1000 range.\n"
                + "- Supported action values are click, doubleclick, down, and up.\n"
                + "- If action is omitted, empty, or null, it means click.\n"
                + "- Use down to press and hold, then return a later target with down again to keep dragging or with up to release at a new location.\n"
                + "- Use doubleclick when the UI requires a true double-click gesture.\n"
                + "- The reason should mention visible evidence, the intended UI element, and how the choice relates to the recent clicker history.\n"
                + "- Avoid repeating targets that already failed unless the screenshot clearly changed and the reason explains why retrying is now appropriate.\n"
                + (this.ClickerTellModelAboutProtectedZones && promptZones.Count > 0
                    ? "- Never choose a point that lies inside a protected zone.\n"
                    : string.Empty)
                + "- If no safe pointer action exists, return {\"point_2d\":[-1,-1],\"action\":null,\"reason\":\"not found\"}.\n";
        }

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

            this.DeleteLastClickerScreenshot();
            this.clickerLastScreenshotPath = screenshotPath;
            this.ClickerScreenshotDataUrl = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(screenshotPath, cancellationToken));
            this.ClickerPreviewMarkerLeftPercent = null;
            this.ClickerPreviewMarkerTopPercent = null;
            this.RequestUiRefresh();
            return (true, window);
        }

        private bool TryFindBlockingProtectedZone(ScreenClicker.MarkedWindowInfo window, Point screenPoint, bool includeWindowChrome, out ClickerProtectedZone? protectedZone)
        {
            protectedZone = null;
            Rectangle referenceBounds = this.Clicker.GetReferenceBounds(window, includeWindowChrome);
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
            this.IsClickerProtectedZoneSelectionActive = false;
            this.Clicker.ReleaseHeldPointer();
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
