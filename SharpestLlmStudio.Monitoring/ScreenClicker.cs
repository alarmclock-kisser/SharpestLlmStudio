using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Monitoring
{
    [SupportedOSPlatform("windows")]
    public sealed class ScreenClicker
    {
        public sealed record MarkedWindowInfo(nint Handle, string Title, Rectangle Bounds, Rectangle ClientBounds)
        {
            public int Left => this.Bounds.Left;
            public int Top => this.Bounds.Top;
            public int Width => this.Bounds.Width;
            public int Height => this.Bounds.Height;
            public string DisplayLabel => $"{this.Title} [{this.Left},{this.Top} {this.Width}x{this.Height}]";
        }

        public enum CoordinateSpace
        {
            Normalized0To1000,
            Normalized0To1,
            WindowPixels,
            ScreenPixels
        }

        public sealed record ClickPoint(double X, double Y, CoordinateSpace CoordinateSpace, string Source)
        {
            public string DisplayLabel => $"{this.Source}: ({this.X:0.###}, {this.Y:0.###}) [{this.CoordinateSpace}]";
        }

        public enum PointerAction
        {
            Click,
            DoubleClick,
            Down,
            Up
        }

        public sealed record ParsedPointerCommand(ClickPoint Point, PointerAction Action)
        {
            public string DisplayLabel => $"{this.Action}: {this.Point.DisplayLabel}";
        }

        public enum KeyboardAction
        {
            Press,
            Down,
            Up,
            Type
        }

        public sealed record ParsedKeyboardCommand(IReadOnlyList<string> Keys, KeyboardAction Action, string? Text)
        {
            public string DisplayLabel => this.Action == KeyboardAction.Type
                ? $"Type: {this.Text ?? string.Empty}"
                : $"{this.Action}: {string.Join("+", this.Keys)}";
        }

        private bool leftButtonHeld;
        private bool leftButtonHeldBackground;
        private nint? leftButtonHeldWindowHandle;
        private Point? lastHeldScreenPoint;
        private readonly HashSet<byte> heldKeyboardKeys = [];
        private bool heldKeyboardKeysBackground;
        private nint? heldKeyboardWindowHandle;

        public bool IsLeftButtonHeld => this.leftButtonHeld;

        public nint GetCurrentForegroundWindowHandle()
        {
            return GetForegroundWindow();
        }

        public bool IsTaskbarOrShellWindow(nint handle)
        {
            if (handle == IntPtr.Zero)
            {
                return true;
            }

            string className = GetWindowClassName(handle);
            if (string.IsNullOrWhiteSpace(className))
            {
                return false;
            }

            return className.Equals("Shell_TrayWnd", StringComparison.Ordinal)
                || className.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal)
                || className.Equals("Progman", StringComparison.Ordinal)
                || className.Equals("WorkerW", StringComparison.Ordinal)
                || className.Equals("Shell_ExperienceHost", StringComparison.Ordinal);
        }

        public bool TryRestoreForegroundWindow(nint handle)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle))
            {
                return false;
            }

            try
            {
                return SetForegroundWindow(handle);
            }
            catch
            {
                return false;
            }
        }

        public bool TryMarkForegroundWindow(out MarkedWindowInfo? window)
        {
            window = null;

            nint hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            return this.TryGetWindowInfo(hwnd, out window);
        }

        public bool TryActivateWindow(MarkedWindowInfo window)
        {
            if (!IsWindow(window.Handle))
            {
                return false;
            }

            try
            {
                ShowWindow(window.Handle, SW_RESTORE);
                if (SetForegroundWindow(window.Handle) && GetForegroundWindow() == window.Handle)
                {
                    return true;
                }

                // Use AttachThreadInput trick to allow SetForegroundWindow from a background thread.
                // Windows blocks SetForegroundWindow unless the calling thread owns the foreground —
                // temporarily attaching to the foreground thread's input queue bypasses this restriction.
                nint foregroundHwnd = GetForegroundWindow();
                uint foregroundThreadId = foregroundHwnd != IntPtr.Zero ? GetWindowThreadProcessId(foregroundHwnd, out _) : 0;
                uint currentThreadId = GetCurrentThreadId();
                bool attached = foregroundThreadId != 0 && foregroundThreadId != currentThreadId
                    && AttachThreadInput(currentThreadId, foregroundThreadId, true);

                try
                {
                    BringWindowToTop(window.Handle);
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        _ = SetForegroundWindow(window.Handle);
                        if (GetForegroundWindow() == window.Handle)
                        {
                            return true;
                        }

                        Thread.Sleep(40);
                    }

                    return GetForegroundWindow() == window.Handle;
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentThreadId, foregroundThreadId, false);
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        public bool TryRefreshWindow(MarkedWindowInfo window, out MarkedWindowInfo? refreshedWindow)
        {
            return this.TryGetWindowInfo(window.Handle, out refreshedWindow);
        }

        public Rectangle GetReferenceBounds(MarkedWindowInfo window, bool includeWindowChrome)
        {
            Rectangle referenceBounds = includeWindowChrome ? window.Bounds : window.ClientBounds;
            if (referenceBounds.Width <= 1 || referenceBounds.Height <= 1)
            {
                referenceBounds = window.Bounds;
            }

            return referenceBounds;
        }

        public async Task<string?> CaptureWindowToPngAsync(MarkedWindowInfo window, string outputDirectory, bool includeWindowChrome = false, CancellationToken cancellationToken = default)
        {
            if (!this.TryGetWindowInfo(window.Handle, out var refreshedWindow) || refreshedWindow == null)
            {
                return null;
            }

            Directory.CreateDirectory(outputDirectory);
            string filePath = Path.Combine(outputDirectory, $"clicker_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");

            Rectangle bounds = this.GetReferenceBounds(refreshedWindow, includeWindowChrome);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            bool capturedFromScreen = TryCaptureFromScreen(bitmap, bounds);
            bool looksBlank = IsBitmapMostlyBlank(bitmap);

            try
            {
                if (looksBlank)
                {
                    await Task.Delay(120, cancellationToken);
                    capturedFromScreen = TryCaptureFromScreen(bitmap, bounds) || capturedFromScreen;
                    looksBlank = IsBitmapMostlyBlank(bitmap);
                }

                if (looksBlank)
                {
                    using var printBitmap = new Bitmap(bounds.Width, bounds.Height);
                    bool printWorked = TryCaptureWithPrintWindow(refreshedWindow.Handle, printBitmap);

                    if (printWorked && !IsBitmapMostlyBlank(printBitmap))
                    {
                        using var g = Graphics.FromImage(bitmap);
                        g.DrawImageUnscaled(printBitmap, 0, 0);
                        looksBlank = false;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                bitmap.Save(filePath, ImageFormat.Png);

                if (looksBlank)
                {
                    await StaticLogger.LogAsync($"[ScreenClicker] Screenshot still appears blank after capture fallback for window '{refreshedWindow.Title}'. Visible capture used: {capturedFromScreen}.");
                }

                await StaticLogger.LogAsync($"[ScreenClicker] Captured window screenshot: {filePath}");
                return filePath;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch
                {
                }

                return null;
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[ScreenClicker] CaptureWindowToPngAsync failed");
                return null;
            }
        }

        private static bool TryCaptureFromScreen(Bitmap bitmap, Rectangle bounds)
        {
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCaptureWithPrintWindow(nint windowHandle, Bitmap bitmap)
        {
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                IntPtr hdc = graphics.GetHdc();
                try
                {
                    return PrintWindow(windowHandle, hdc, 0);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBitmapMostlyBlank(Bitmap bitmap)
        {
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return true;
            }

            int stepX = Math.Max(1, bitmap.Width / 32);
            int stepY = Math.Max(1, bitmap.Height / 32);
            int samples = 0;
            int nearBlack = 0;
            int uniqueBuckets = 0;
            var buckets = new HashSet<int>();

            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                for (int x = 0; x < bitmap.Width; x += stepX)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    samples++;

                    int brightness = (pixel.R + pixel.G + pixel.B) / 3;
                    if (brightness <= 10)
                    {
                        nearBlack++;
                    }

                    int bucket = ((pixel.R / 32) << 8) | ((pixel.G / 32) << 4) | (pixel.B / 32);
                    if (buckets.Add(bucket))
                    {
                        uniqueBuckets++;
                    }
                }
            }

            if (samples == 0)
            {
                return true;
            }

            double blackRatio = nearBlack / (double)samples;
            return blackRatio >= 0.92 || uniqueBuckets <= 2;
        }

        public bool TryParseClickPoint(string responseText, MarkedWindowInfo window, bool includeWindowChrome, out ParsedPointerCommand? command, out string normalizedJson, out string errorMessage)
        {
            command = null;
            normalizedJson = string.Empty;
            errorMessage = string.Empty;
            Rectangle referenceBounds = this.GetReferenceBounds(window, includeWindowChrome);

            foreach (string candidate in this.EnumerateJsonCandidates(responseText))
            {
                try
                {
                    using var document = JsonDocument.Parse(candidate, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true
                    });

                    if (TryReadPoint(document.RootElement, out double x, out double y, out string source))
                    {
                        command = new ParsedPointerCommand(
                            ClassifyPoint(x, y, referenceBounds, source),
                            ParsePointerAction(document.RootElement));
                        normalizedJson = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });
                        return true;
                    }
                }
                catch
                {
                }
            }

            if (TryParseRawCoordinates(responseText, out double rawX, out double rawY))
            {
                command = new ParsedPointerCommand(ClassifyPoint(rawX, rawY, referenceBounds, "raw-coordinates"), PointerAction.Click);
                normalizedJson = $"{{\n  \"point_2d\": [{rawX.ToString(CultureInfo.InvariantCulture)}, {rawY.ToString(CultureInfo.InvariantCulture)}]\n}}";
                return true;
            }

            errorMessage = "No point_2d, x/y, bbox_2d, or raw coordinate pair could be parsed from the model response.";
            return false;
        }

        public Point ConvertToScreenPoint(MarkedWindowInfo window, ClickPoint clickPoint, bool includeWindowChrome = false)
        {
            Rectangle referenceBounds = this.GetReferenceBounds(window, includeWindowChrome);
            double x;
            double y;

            switch (clickPoint.CoordinateSpace)
            {
                case CoordinateSpace.Normalized0To1:
                    x = referenceBounds.Left + (clickPoint.X * referenceBounds.Width);
                    y = referenceBounds.Top + (clickPoint.Y * referenceBounds.Height);
                    break;
                case CoordinateSpace.Normalized0To1000:
                    x = referenceBounds.Left + ((clickPoint.X / 1000.0) * referenceBounds.Width);
                    y = referenceBounds.Top + ((clickPoint.Y / 1000.0) * referenceBounds.Height);
                    break;
                case CoordinateSpace.WindowPixels:
                    x = referenceBounds.Left + clickPoint.X;
                    y = referenceBounds.Top + clickPoint.Y;
                    break;
                default:
                    x = clickPoint.X;
                    y = clickPoint.Y;
                    break;
            }

            int screenX = (int)Math.Round(x);
            int screenY = (int)Math.Round(y);
            return new Point(screenX, screenY);
        }

        public bool IsScreenPointInsideWindow(MarkedWindowInfo window, Point screenPoint, bool includeWindowChrome = false, int margin = 0)
        {
            Rectangle bounds = this.GetReferenceBounds(window, includeWindowChrome);
            if (margin > 0)
            {
                bounds = Rectangle.Inflate(bounds, -margin, -margin);
            }

            return bounds.Contains(screenPoint);
        }

        public bool TryClickPoint(MarkedWindowInfo window, ClickPoint clickPoint, out Point screenPoint, bool activateWindow = true, bool includeWindowChrome = false, bool useBackgroundClick = false)
        {
            return this.TryExecutePointerAction(window, clickPoint, PointerAction.Click, out screenPoint, activateWindow, includeWindowChrome, useBackgroundClick);
        }

        public bool TryExecutePointerAction(MarkedWindowInfo window, ClickPoint clickPoint, PointerAction action, out Point screenPoint, bool activateWindow = true, bool includeWindowChrome = false, bool useBackgroundClick = false)
        {
            screenPoint = Point.Empty;

            MarkedWindowInfo? refreshedWindow = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (this.TryGetWindowInfo(window.Handle, out refreshedWindow) && refreshedWindow != null)
                {
                    break;
                }

                if (attempt < 2)
                {
                    Thread.Sleep(80);
                }
            }

            if (refreshedWindow == null)
            {
                return false;
            }

            if (useBackgroundClick)
            {
                if (activateWindow && (action == PointerAction.Click || action == PointerAction.DoubleClick))
                {
                    return this.TryForegroundPointerActionWithCursorRestore(refreshedWindow, clickPoint, action, includeWindowChrome, out screenPoint);
                }

                return this.TryBackgroundPointerAction(refreshedWindow, clickPoint, action, includeWindowChrome, out screenPoint);
            }

            if (activateWindow)
            {
                this.TryActivateWindow(refreshedWindow);
                Thread.Sleep(120);
            }

            screenPoint = this.ConvertToScreenPoint(refreshedWindow, clickPoint, includeWindowChrome);
            if (screenPoint.X < 0 || screenPoint.Y < 0)
            {
                return false;
            }

            if (!TryMoveCursorForClick(screenPoint))
            {
                return false;
            }

            bool success = this.TryForegroundPointerAction(refreshedWindow, action, screenPoint);
            if (success)
            {
                this.lastHeldScreenPoint = screenPoint;
            }

            return success;
        }

        public bool TryExecuteKeyboardCommand(MarkedWindowInfo window, ParsedKeyboardCommand command, bool activateWindow = true, bool useBackgroundInput = false)
        {
            if (!this.TryGetWindowInfo(window.Handle, out var refreshedWindow) || refreshedWindow == null)
            {
                return false;
            }

            if (activateWindow)
            {
                this.TryActivateWindow(refreshedWindow);
                Thread.Sleep(80);
            }

            if (command.Action == KeyboardAction.Type)
            {
                string text = command.Text ?? string.Empty;
                if (string.IsNullOrEmpty(text))
                {
                    return true;
                }

                foreach (char c in text)
                {
                    if (useBackgroundInput)
                    {
                        _ = PostMessage(refreshedWindow.Handle, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                    }
                    else
                    {
                        short vk = VkKeyScan(c);
                        if (vk == -1)
                        {
                            continue;
                        }

                        byte keyCode = (byte)(vk & 0xFF);
                        byte shiftState = (byte)((vk >> 8) & 0xFF);
                        if ((shiftState & 1) != 0) keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                        if ((shiftState & 2) != 0) keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                        if ((shiftState & 4) != 0) keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);

                        keybd_event(keyCode, 0, 0, UIntPtr.Zero);
                        keybd_event(keyCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                        if ((shiftState & 4) != 0) keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        if ((shiftState & 2) != 0) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        if ((shiftState & 1) != 0) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                }

                return true;
            }

            var keys = new List<byte>();
            foreach (string keyName in command.Keys)
            {
                if (!TryMapVirtualKey(keyName, out byte keyCode))
                {
                    return false;
                }

                if (!keys.Contains(keyCode))
                {
                    keys.Add(keyCode);
                }
            }

            if (keys.Count == 0)
            {
                return false;
            }

            if (command.Action == KeyboardAction.Down)
            {
                foreach (byte key in keys)
                {
                    if (useBackgroundInput)
                    {
                        _ = PostMessage(refreshedWindow.Handle, WM_KEYDOWN, (IntPtr)key, IntPtr.Zero);
                    }
                    else
                    {
                        keybd_event(key, 0, 0, UIntPtr.Zero);
                    }

                    this.heldKeyboardKeys.Add(key);
                }

                this.heldKeyboardKeysBackground = useBackgroundInput;
                this.heldKeyboardWindowHandle = refreshedWindow.Handle;
                return true;
            }

            if (command.Action == KeyboardAction.Up)
            {
                for (int i = keys.Count - 1; i >= 0; i--)
                {
                    byte key = keys[i];
                    if (useBackgroundInput)
                    {
                        _ = PostMessage(refreshedWindow.Handle, WM_KEYUP, (IntPtr)key, IntPtr.Zero);
                    }
                    else
                    {
                        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }

                    this.heldKeyboardKeys.Remove(key);
                }

                if (this.heldKeyboardKeys.Count == 0)
                {
                    this.heldKeyboardKeysBackground = false;
                    this.heldKeyboardWindowHandle = null;
                }

                return true;
            }

            foreach (byte key in keys)
            {
                if (useBackgroundInput)
                {
                    _ = PostMessage(refreshedWindow.Handle, WM_KEYDOWN, (IntPtr)key, IntPtr.Zero);
                }
                else
                {
                    keybd_event(key, 0, 0, UIntPtr.Zero);
                }
            }

            for (int i = keys.Count - 1; i >= 0; i--)
            {
                byte key = keys[i];
                if (useBackgroundInput)
                {
                    _ = PostMessage(refreshedWindow.Handle, WM_KEYUP, (IntPtr)key, IntPtr.Zero);
                }
                else
                {
                    keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
            }

            return true;
        }

        private bool TryForegroundPointerActionWithCursorRestore(MarkedWindowInfo window, ClickPoint clickPoint, PointerAction action, bool includeWindowChrome, out Point screenPoint)
        {
            screenPoint = this.ConvertToScreenPoint(window, clickPoint, includeWindowChrome);
            if (screenPoint.X < 0 || screenPoint.Y < 0)
            {
                return false;
            }

            bool hadOriginalCursor = GetCursorPos(out POINT originalCursor);

            this.TryActivateWindow(window);
            Thread.Sleep(150);

            bool foregroundSuccess = false;
            try
            {
                if (TryMoveCursorForClick(screenPoint))
                {
                    foregroundSuccess = this.TryForegroundPointerAction(window, action, screenPoint);
                }
            }
            catch
            {
            }
            finally
            {
                Thread.Sleep(90);
                if (hadOriginalCursor)
                {
                    try { SetCursorPos(originalCursor.X, originalCursor.Y); } catch { }
                }
            }

            if (foregroundSuccess)
            {
                return true;
            }

            // Foreground approach failed (cursor couldn't be positioned or click didn't land).
            // Fall back to PostMessage-based background click which doesn't require cursor or foreground.
            return this.TryBackgroundPointerAction(window, clickPoint, action, includeWindowChrome, out screenPoint);
        }

        public void ReleaseHeldPointer()
        {
            try
            {
                if (this.leftButtonHeld)
                {
                    if (this.leftButtonHeldBackground && this.leftButtonHeldWindowHandle.HasValue)
                    {
                        if (this.TryGetWindowInfo(this.leftButtonHeldWindowHandle.Value, out var refreshedWindow) && refreshedWindow != null)
                        {
                            Point releasePoint = this.lastHeldScreenPoint ?? refreshedWindow.ClientBounds.Location;
                            POINT clientPoint = new() { X = releasePoint.X, Y = releasePoint.Y };
                            if (ScreenToClient(refreshedWindow.Handle, ref clientPoint))
                            {
                                IntPtr lParam = MakeLParam(clientPoint.X, clientPoint.Y);
                                _ = PostMessage(refreshedWindow.Handle, WM_MOUSEMOVE, IntPtr.Zero, lParam);
                                _ = PostMessage(refreshedWindow.Handle, WM_LBUTTONUP, IntPtr.Zero, lParam);
                            }
                        }
                    }
                    else
                    {
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                this.leftButtonHeld = false;
                this.leftButtonHeldBackground = false;
                this.leftButtonHeldWindowHandle = null;
                this.lastHeldScreenPoint = null;
            }
        }

        public void ReleaseHeldKeyboard()
        {
            if (this.heldKeyboardKeys.Count == 0)
            {
                return;
            }

            try
            {
                var keys = new List<byte>(this.heldKeyboardKeys);
                for (int i = keys.Count - 1; i >= 0; i--)
                {
                    byte key = keys[i];
                    if (this.heldKeyboardKeysBackground && this.heldKeyboardWindowHandle.HasValue)
                    {
                        _ = PostMessage(this.heldKeyboardWindowHandle.Value, WM_KEYUP, (IntPtr)key, IntPtr.Zero);
                    }
                    else
                    {
                        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                this.heldKeyboardKeys.Clear();
                this.heldKeyboardKeysBackground = false;
                this.heldKeyboardWindowHandle = null;
            }
        }

        public void ReleaseHeldInputs()
        {
            this.ReleaseHeldPointer();
            this.ReleaseHeldKeyboard();
        }

        public async Task<Point?> WaitForNextLeftClickAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var started = DateTime.UtcNow;
            bool wasDown = IsLeftButtonDown();

            while (DateTime.UtcNow - started < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool isDown = IsLeftButtonDown();
                if (wasDown && !isDown && GetCursorPos(out POINT point))
                {
                    return new Point(point.X, point.Y);
                }

                wasDown = isDown;
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        private static bool TryMapVirtualKey(string rawKey, out byte virtualKey)
        {
            virtualKey = 0;
            string key = rawKey?.Trim().ToLowerInvariant() ?? string.Empty;
            if (key.Length == 1)
            {
                char ch = key[0];
                if (ch >= 'a' && ch <= 'z')
                {
                    virtualKey = (byte)char.ToUpperInvariant(ch);
                    return true;
                }

                if (ch >= '0' && ch <= '9')
                {
                    virtualKey = (byte)ch;
                    return true;
                }
            }

            if (key.StartsWith('f') && key.Length <= 3 && int.TryParse(key[1..], out int fKey) && fKey >= 1 && fKey <= 24)
            {
                virtualKey = (byte)(0x6F + fKey);
                return true;
            }

            return key switch
            {
                "ctrl" or "control" => AssignVirtualKey(VK_CONTROL, out virtualKey),
                "shift" => AssignVirtualKey(VK_SHIFT, out virtualKey),
                "alt" => AssignVirtualKey(VK_MENU, out virtualKey),
                "win" or "windows" => AssignVirtualKey(VK_LWIN, out virtualKey),
                "enter" or "return" => AssignVirtualKey(VK_RETURN, out virtualKey),
                "tab" => AssignVirtualKey(VK_TAB, out virtualKey),
                "esc" or "escape" => AssignVirtualKey(VK_ESCAPE, out virtualKey),
                "space" => AssignVirtualKey(VK_SPACE, out virtualKey),
                "backspace" => AssignVirtualKey(VK_BACK, out virtualKey),
                "delete" or "del" => AssignVirtualKey(VK_DELETE, out virtualKey),
                "insert" or "ins" => AssignVirtualKey(VK_INSERT, out virtualKey),
                "home" => AssignVirtualKey(VK_HOME, out virtualKey),
                "end" => AssignVirtualKey(VK_END, out virtualKey),
                "pageup" or "pgup" => AssignVirtualKey(VK_PRIOR, out virtualKey),
                "pagedown" or "pgdn" => AssignVirtualKey(VK_NEXT, out virtualKey),
                "up" or "arrowup" => AssignVirtualKey(VK_UP, out virtualKey),
                "down" or "arrowdown" => AssignVirtualKey(VK_DOWN, out virtualKey),
                "left" or "arrowleft" => AssignVirtualKey(VK_LEFT, out virtualKey),
                "right" or "arrowright" => AssignVirtualKey(VK_RIGHT, out virtualKey),
                "+" or "plus" => AssignVirtualKey(VK_ADD, out virtualKey),
                _ => false
            };
        }

        private static bool AssignVirtualKey(byte key, out byte virtualKey)
        {
            virtualKey = key;
            return true;
        }

        private static bool TryExecutePrintableKeyPress(MarkedWindowInfo window, string rawKey, bool useBackgroundInput)
        {
            char? printable = rawKey?.Trim() switch
            {
                "+" or "plus" => '+',
                "-" or "minus" => '-',
                _ => null
            };

            if (!printable.HasValue)
            {
                return false;
            }

            if (useBackgroundInput)
            {
                _ = PostMessage(window.Handle, WM_CHAR, (IntPtr)printable.Value, IntPtr.Zero);
                return true;
            }

            short vk = VkKeyScan(printable.Value);
            if (vk == -1)
            {
                return false;
            }

            byte keyCode = (byte)(vk & 0xFF);
            byte shiftState = (byte)((vk >> 8) & 0xFF);
            if ((shiftState & 1) != 0) keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
            if ((shiftState & 2) != 0) keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            if ((shiftState & 4) != 0) keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);

            keybd_event(keyCode, 0, 0, UIntPtr.Zero);
            keybd_event(keyCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            if ((shiftState & 4) != 0) keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if ((shiftState & 2) != 0) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if ((shiftState & 1) != 0) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            return true;
        }

        private static bool IsLeftButtonDown()
        {
            return (GetAsyncKeyState(VK_LBUTTON_MOUSE) & 0x8000) != 0;
        }

        private bool TryForegroundPointerAction(MarkedWindowInfo window, PointerAction action, Point screenPoint)
        {
            switch (action)
            {
                case PointerAction.Down:
                    if (!this.leftButtonHeld)
                    {
                        if (!TrySendForegroundMouseAction(screenPoint, PointerAction.Down))
                        {
                            return false;
                        }

                        this.leftButtonHeld = true;
                    }

                    this.leftButtonHeldBackground = false;
                    this.leftButtonHeldWindowHandle = window.Handle;
                    return true;

                case PointerAction.Up:
                    if (this.leftButtonHeldBackground)
                    {
                        this.ReleaseHeldPointer();
                    }

                    if (!TrySendForegroundMouseAction(screenPoint, PointerAction.Up))
                    {
                        return false;
                    }

                    this.leftButtonHeld = false;
                    this.leftButtonHeldBackground = false;
                    this.leftButtonHeldWindowHandle = null;
                    return true;

                case PointerAction.DoubleClick:
                    if (this.leftButtonHeld)
                    {
                        this.ReleaseHeldPointer();
                        if (!TryMoveCursorForClick(screenPoint))
                        {
                            return false;
                        }
                    }

                    if (!TrySendForegroundMouseAction(screenPoint, PointerAction.DoubleClick))
                    {
                        return false;
                    }

                    this.leftButtonHeld = false;
                    this.leftButtonHeldBackground = false;
                    this.leftButtonHeldWindowHandle = null;
                    return true;

                default:
                    if (this.leftButtonHeld)
                    {
                        this.ReleaseHeldPointer();
                        if (!TryMoveCursorForClick(screenPoint))
                        {
                            return false;
                        }
                    }

                    if (!TrySendForegroundMouseAction(screenPoint, PointerAction.Click))
                    {
                        return false;
                    }

                    this.leftButtonHeld = false;
                    this.leftButtonHeldBackground = false;
                    this.leftButtonHeldWindowHandle = null;
                    return true;
            }
        }

        private static bool TrySendForegroundMouseAction(Point screenPoint, PointerAction action)
        {
            if (!TryMoveCursorForClick(screenPoint))
            {
                return false;
            }

            switch (action)
            {
                case PointerAction.Down:
                    return TrySendMouseButtonInput(MOUSEEVENTF_LEFTDOWN);

                case PointerAction.Up:
                    return TrySendMouseButtonInput(MOUSEEVENTF_LEFTUP);

                case PointerAction.DoubleClick:
                    if (!TrySendMouseButtonInput(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP))
                    {
                        return false;
                    }

                    Thread.Sleep(Math.Max(40, Math.Min(120, (int)GetDoubleClickTime() / 3)));
                    return TrySendMouseButtonInput(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);

                default:
                    return TrySendMouseButtonInput(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
            }
        }

        private static bool TryMoveCursorForClick(Point screenPoint)
        {
            int virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int virtualWidth = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
            int virtualHeight = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

            int absoluteX = (int)Math.Round(((screenPoint.X - virtualLeft) * 65535.0) / Math.Max(1, virtualWidth - 1));
            int absoluteY = (int)Math.Round(((screenPoint.Y - virtualTop) * 65535.0) / Math.Max(1, virtualHeight - 1));

            for (int attempt = 0; attempt < 3; attempt++)
            {
                _ = SetCursorPos(screenPoint.X, screenPoint.Y);

                var input = new INPUT
                {
                    type = INPUT_MOUSE,
                    U = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = absoluteX,
                            dy = absoluteY,
                            mouseData = 0,
                            dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };

                _ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
                Thread.Sleep(attempt == 0 ? 50 : 80);

                if (!GetCursorPos(out POINT actual))
                {
                    return true;
                }

                if (Math.Abs(actual.X - screenPoint.X) <= 4 && Math.Abs(actual.Y - screenPoint.Y) <= 4)
                {
                    return true;
                }
            }

            // Final fallback: accept cursor even if slightly off — the click target area is usually larger than a few pixels
            if (GetCursorPos(out POINT finalPos))
            {
                return Math.Abs(finalPos.X - screenPoint.X) <= 12 && Math.Abs(finalPos.Y - screenPoint.Y) <= 12;
            }

            return false;
        }

        private static bool TrySendMouseButtonInput(params uint[] flags)
        {
            if (flags == null || flags.Length == 0)
            {
                return false;
            }

            var inputs = new INPUT[flags.Length];
            for (int i = 0; i < flags.Length; i++)
            {
                inputs[i] = new INPUT
                {
                    type = INPUT_MOUSE,
                    U = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = 0,
                            dy = 0,
                            mouseData = 0,
                            dwFlags = flags[i],
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };
            }

            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent == inputs.Length)
            {
                Thread.Sleep(40);
                return true;
            }

            foreach (uint flag in flags)
            {
                mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
            }

            return true;
        }

        private bool TryBackgroundPointerAction(MarkedWindowInfo window, ClickPoint clickPoint, PointerAction action, bool includeWindowChrome, out Point screenPoint)
        {
            screenPoint = this.ConvertToScreenPoint(window, clickPoint, includeWindowChrome);

            POINT clientPoint = new() { X = screenPoint.X, Y = screenPoint.Y };
            if (!ScreenToClient(window.Handle, ref clientPoint))
            {
                return false;
            }

            Rectangle clientBounds = window.ClientBounds;
            if (clientBounds.Width <= 1 || clientBounds.Height <= 1)
            {
                return false;
            }

            int clientX = clientPoint.X;
            int clientY = clientPoint.Y;
            int maxX = Math.Max(0, clientBounds.Width - 1);
            int maxY = Math.Max(0, clientBounds.Height - 1);
            if (clientX < 0 || clientY < 0 || clientX > maxX || clientY > maxY)
            {
                return false;
            }

            IntPtr lParam = MakeLParam(clientX, clientY);
            _ = PostMessage(window.Handle, WM_MOUSEMOVE, IntPtr.Zero, lParam);

            switch (action)
            {
                case PointerAction.Down:
                    if (!this.leftButtonHeld)
                    {
                        _ = PostMessage(window.Handle, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
                    }

                    this.leftButtonHeld = true;
                    this.leftButtonHeldBackground = true;
                    this.leftButtonHeldWindowHandle = window.Handle;
                    this.lastHeldScreenPoint = screenPoint;
                    return true;

                case PointerAction.Up:
                    _ = PostMessage(window.Handle, WM_LBUTTONUP, IntPtr.Zero, lParam);
                    this.leftButtonHeld = false;
                    this.leftButtonHeldBackground = false;
                    this.leftButtonHeldWindowHandle = null;
                    this.lastHeldScreenPoint = null;
                    return true;

                case PointerAction.DoubleClick:
                    if (this.leftButtonHeld)
                    {
                        this.ReleaseHeldPointer();
                    }

                    _ = PostMessage(window.Handle, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
                    _ = PostMessage(window.Handle, WM_LBUTTONUP, IntPtr.Zero, lParam);
                    _ = PostMessage(window.Handle, WM_LBUTTONDBLCLK, (IntPtr)MK_LBUTTON, lParam);
                    _ = PostMessage(window.Handle, WM_LBUTTONUP, IntPtr.Zero, lParam);
                    this.leftButtonHeld = false;
                    this.leftButtonHeldBackground = false;
                    this.leftButtonHeldWindowHandle = null;
                    this.lastHeldScreenPoint = null;
                    return true;

                default:
                    if (this.leftButtonHeld)
                    {
                        this.ReleaseHeldPointer();
                        _ = PostMessage(window.Handle, WM_MOUSEMOVE, IntPtr.Zero, lParam);
                    }

                    _ = PostMessage(window.Handle, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
                    _ = PostMessage(window.Handle, WM_LBUTTONUP, IntPtr.Zero, lParam);
                    this.leftButtonHeld = false;
                    this.leftButtonHeldBackground = false;
                    this.leftButtonHeldWindowHandle = null;
                    this.lastHeldScreenPoint = null;
                    return true;
            }
        }

        private bool TryGetWindowInfo(nint hwnd, out MarkedWindowInfo? window)
        {
            window = null;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return false;
            }

            if (!GetWindowRect(hwnd, out RECT rect))
            {
                return false;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 1 || height <= 1)
            {
                return false;
            }

            string title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"Window 0x{hwnd.ToInt64():X}";
            }

            Rectangle clientBounds = GetClientBoundsOrWindowBounds(hwnd, rect.Left, rect.Top, width, height);
            window = new MarkedWindowInfo(hwnd, title, new Rectangle(rect.Left, rect.Top, width, height), clientBounds);
            return true;
        }

        private static Rectangle GetClientBoundsOrWindowBounds(nint hwnd, int windowLeft, int windowTop, int windowWidth, int windowHeight)
        {
            try
            {
                if (!GetClientRect(hwnd, out RECT clientRect))
                {
                    return new Rectangle(windowLeft, windowTop, windowWidth, windowHeight);
                }

                POINT topLeft = new() { X = clientRect.Left, Y = clientRect.Top };
                POINT bottomRight = new() { X = clientRect.Right, Y = clientRect.Bottom };
                if (!ClientToScreen(hwnd, ref topLeft) || !ClientToScreen(hwnd, ref bottomRight))
                {
                    return new Rectangle(windowLeft, windowTop, windowWidth, windowHeight);
                }

                int width = Math.Max(1, bottomRight.X - topLeft.X);
                int height = Math.Max(1, bottomRight.Y - topLeft.Y);
                return new Rectangle(topLeft.X, topLeft.Y, width, height);
            }
            catch
            {
                return new Rectangle(windowLeft, windowTop, windowWidth, windowHeight);
            }
        }

        private static string GetWindowTitle(nint hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            var sb = new StringBuilder(Math.Max(256, length + 1));
            _ = GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString().Trim();
        }

        private static string GetWindowClassName(nint hwnd)
        {
            var sb = new StringBuilder(256);
            int len = GetClassName(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        private IEnumerable<string> EnumerateJsonCandidates(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            string trimmed = text.Trim();
            yield return trimmed;

            string withoutCodeFences = RemoveCodeFences(trimmed);
            if (!string.Equals(withoutCodeFences, trimmed, StringComparison.Ordinal))
            {
                yield return withoutCodeFences;
            }

            foreach (string extracted in ExtractBalancedJsonSegments(withoutCodeFences))
            {
                yield return extracted;
            }
        }

        private static string RemoveCodeFences(string text)
        {
            string trimmed = text.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                return trimmed;
            }

            int firstLineEnd = trimmed.IndexOf('\n');
            if (firstLineEnd < 0)
            {
                return trimmed.Trim('`').Trim();
            }

            string body = trimmed[(firstLineEnd + 1)..];
            int closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                body = body[..closingFence];
            }

            return body.Trim();
        }

        private static IEnumerable<string> ExtractBalancedJsonSegments(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch != '{' && ch != '[')
                {
                    continue;
                }

                if (TryExtractBalancedJson(text, i, out string? candidate, out int endIndex) && !string.IsNullOrWhiteSpace(candidate))
                {
                    yield return candidate;
                    i = endIndex;
                }
            }
        }

        private static bool TryExtractBalancedJson(string text, int startIndex, out string? candidate, out int endIndex)
        {
            candidate = null;
            endIndex = -1;

            char open = text[startIndex];
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = startIndex; i < text.Length; i++)
            {
                char current = text[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == open)
                {
                    depth++;
                }
                else if (current == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        endIndex = i;
                        candidate = text[startIndex..(i + 1)].Trim();
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryReadPoint(JsonElement element, out double x, out double y, out string source)
        {
            x = 0;
            y = 0;
            source = string.Empty;

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (TryGetArrayPoint(element, "point_2d", out x, out y))
                    {
                        source = "point_2d";
                        return true;
                    }

                    if (TryGetArrayPoint(element, "point", out x, out y))
                    {
                        source = "point";
                        return true;
                    }

                    if (TryGetBboxCenter(element, out x, out y))
                    {
                        source = "bbox_2d-center";
                        return true;
                    }

                    if (TryGetNumericProperty(element, "x", out x) && TryGetNumericProperty(element, "y", out y))
                    {
                        source = "x/y";
                        return true;
                    }

                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (TryReadPoint(property.Value, out x, out y, out source))
                        {
                            return true;
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    if (TryGetTwoElementArray(element, out x, out y))
                    {
                        source = "array";
                        return true;
                    }

                    foreach (JsonElement child in element.EnumerateArray())
                    {
                        if (TryReadPoint(child, out x, out y, out source))
                        {
                            return true;
                        }
                    }
                    break;
            }

            return false;
        }

        private static bool TryGetArrayPoint(JsonElement element, string propertyName, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (!element.TryGetProperty(propertyName, out JsonElement point))
            {
                return false;
            }

            return TryGetTwoElementArray(point, out x, out y);
        }

        private static bool TryGetBboxCenter(JsonElement element, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (!element.TryGetProperty("bbox_2d", out JsonElement bbox) || bbox.ValueKind != JsonValueKind.Array || bbox.GetArrayLength() < 4)
            {
                return false;
            }

            if (!TryReadDouble(bbox[0], out double x1) || !TryReadDouble(bbox[1], out double y1) || !TryReadDouble(bbox[2], out double x2) || !TryReadDouble(bbox[3], out double y2))
            {
                return false;
            }

            x = (x1 + x2) / 2.0;
            y = (y1 + y2) / 2.0;
            return true;
        }

        private static bool TryGetTwoElementArray(JsonElement element, out double x, out double y)
        {
            x = 0;
            y = 0;

            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < 2)
            {
                return false;
            }

            return TryReadDouble(element[0], out x) && TryReadDouble(element[1], out y);
        }

        private static bool TryGetNumericProperty(JsonElement element, string propertyName, out double value)
        {
            value = 0;
            if (!element.TryGetProperty(propertyName, out JsonElement property))
            {
                return false;
            }

            return TryReadDouble(property, out value);
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
                return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }

            return false;
        }

        private static bool TryParseRawCoordinates(string text, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (Match match in Regex.Matches(text, @"(?ix)
                (?:point_2d|point|coords?|coordinates?|click|target)?
                \s*[:=]?\s*
                [\[(]?\s*
                (?<x>[-+]?\d+(?:[\.,]\d+)?)
                \s*(?:,|;|x|×|\s)\s*
                (?<y>[-+]?\d+(?:[\.,]\d+)?)
                \s*[\])]?") )
            {
                if (TryParseNumber(match.Groups["x"].Value.AsSpan(), out x)
                    && TryParseNumber(match.Groups["y"].Value.AsSpan(), out y))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseNumber(ReadOnlySpan<char> text, out double value)
        {
            string candidate = text.ToString().Trim();
            if (candidate.Length == 0)
            {
                value = 0;
                return false;
            }

            if (candidate.Contains(',') && !candidate.Contains('.'))
            {
                candidate = candidate.Replace(',', '.');
            }

            return double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static ClickPoint ClassifyPoint(double x, double y, Rectangle referenceBounds, string source)
        {
            if (x >= 0 && y >= 0 && x <= 1.0 && y <= 1.0)
            {
                return new ClickPoint(x, y, CoordinateSpace.Normalized0To1, source);
            }

            bool preferWindowPixels = source.Contains("raw", StringComparison.OrdinalIgnoreCase)
                || source.Contains("x/y", StringComparison.OrdinalIgnoreCase)
                || source.Contains("array", StringComparison.OrdinalIgnoreCase);

            if (preferWindowPixels && x >= 0 && y >= 0 && x <= referenceBounds.Width && y <= referenceBounds.Height)
            {
                return new ClickPoint(x, y, CoordinateSpace.WindowPixels, source);
            }

            if (x >= -1 && y >= -1 && x <= 1000 && y <= 1000)
            {
                return new ClickPoint(x, y, CoordinateSpace.Normalized0To1000, source);
            }

            if (x >= 0 && y >= 0 && x <= referenceBounds.Width && y <= referenceBounds.Height)
            {
                return new ClickPoint(x, y, CoordinateSpace.WindowPixels, source);
            }

            return new ClickPoint(x, y, CoordinateSpace.ScreenPixels, source);
        }

        private static PointerAction ParsePointerAction(JsonElement element)
        {
            if (!TryReadActionRecursive(element, out string? actionText) || string.IsNullOrWhiteSpace(actionText))
            {
                return PointerAction.Click;
            }

            return actionText.Trim().ToLowerInvariant() switch
            {
                "down" or "mousedown" or "mouse-down" or "hold" => PointerAction.Down,
                "up" or "mouseup" or "mouse-up" or "release" => PointerAction.Up,
                "doubleclick" or "double-click" or "dblclick" or "dbl-click" or "double" => PointerAction.DoubleClick,
                "click" or "leftclick" or "left-click" or "tap" or "" => PointerAction.Click,
                _ => PointerAction.Click
            };
        }

        private static bool TryReadActionRecursive(JsonElement element, out string? action)
        {
            action = null;

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (string name in new[] { "action", "event", "gesture", "mouse_action", "mouseAction" })
                    {
                        if (element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
                        {
                            action = property.GetString();
                            return !string.IsNullOrWhiteSpace(action);
                        }
                    }

                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (TryReadActionRecursive(property.Value, out action))
                        {
                            return true;
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement child in element.EnumerateArray())
                    {
                        if (TryReadActionRecursive(child, out action))
                        {
                            return true;
                        }
                    }
                    break;
            }

            return false;
        }

        private const int SW_RESTORE = 9;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_CHAR = 0x0102;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int MK_LBUTTON = 0x0001;
        private const byte VK_BACK = 0x08;
        private const byte VK_TAB = 0x09;
        private const byte VK_RETURN = 0x0D;
        private const byte VK_SHIFT = 0x10;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12;
        private const byte VK_ESCAPE = 0x1B;
        private const byte VK_SPACE = 0x20;
        private const byte VK_ADD = 0x6B;
        private const byte VK_PRIOR = 0x21;
        private const byte VK_NEXT = 0x22;
        private const byte VK_END = 0x23;
        private const byte VK_HOME = 0x24;
        private const byte VK_LEFT = 0x25;
        private const byte VK_UP = 0x26;
        private const byte VK_RIGHT = 0x27;
        private const byte VK_DOWN = 0x28;
        private const byte VK_INSERT = 0x2D;
        private const byte VK_DELETE = 0x2E;
        private const byte VK_LWIN = 0x5B;
        private const int VK_LBUTTON_MOUSE = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static IntPtr MakeLParam(int low, int high)
        {
            return (IntPtr) (((high & 0xFFFF) << 16) | (low & 0xFFFF));
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr PostMessage(nint hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(nint hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
