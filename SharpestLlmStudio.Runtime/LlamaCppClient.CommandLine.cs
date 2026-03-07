using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        private static readonly Regex CommandTagRegex = new("<\\s*/?\\s*cmd_start\\s*>\\s*(?<cmd>[\\s\\S]*?)\\s*<\\s*/?\\s*cmd_end\\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CommandTagFallbackRegex = new("<\\s*/?\\s*cmd_start\\s*>\\s*(?<cmd>[\\s\\S]*?)\\s*<\\s*/\\s*cmd_start\\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public bool TryExtractCommandRequest(string assistantOutput, out LlamaCommandRequest? request)
        {
            request = null;
            if (string.IsNullOrWhiteSpace(assistantOutput))
            {
                return false;
            }

            string extracted = ExtractCommand(assistantOutput, out string sourceSnippet);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return false;
            }

            request = new LlamaCommandRequest
            {
                Command = extracted.Trim(),
                SourceSnippet = sourceSnippet
            };

            return true;
        }

        public LlamaCommandSafetyAssessment EvaluateCommandSafety(string command)
        {
            string normalized = (command ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new LlamaCommandSafetyAssessment
                {
                    SafetyLevel = "Blocked",
                    IsBlocked = true,
                    Reason = "Command is empty."
                };
            }

            string[] blockedFragments =
            [
                "format ",
                "shutdown",
                "reboot",
                "poweroff",
                "del /f",
                "del /s",
                "erase /s",
                "rmdir /s",
                "rd /s",
                "reg delete",
                "diskpart",
                "cipher /w",
                "bcdedit",
                "takeown",
                "icacls /grant",
                "net user",
                "powershell -encodedcommand",
                "powershell -enc",
                "curl http",
                "wget http"
            ];

            foreach (var fragment in blockedFragments)
            {
                if (normalized.Contains(fragment, StringComparison.Ordinal))
                {
                    return new LlamaCommandSafetyAssessment
                    {
                        SafetyLevel = "Blocked",
                        IsBlocked = true,
                        Reason = $"Blocked potentially dangerous command fragment: '{fragment}'."
                    };
                }
            }

            string[] elevatedIndicators = ["&&", "||", "|", ">", "<", "copy ", "move ", "mkdir ", "md ", "ren ", "attrib ", "setx "];
            foreach (var token in elevatedIndicators)
            {
                if (normalized.Contains(token, StringComparison.Ordinal))
                {
                    return new LlamaCommandSafetyAssessment
                    {
                        SafetyLevel = "Elevated",
                        RequiresAdditionalConfirmation = true,
                        Reason = $"Command contains elevated indicator '{token}'."
                    };
                }
            }

            string[] readOnlyPrefixes =
            [
                "dir",
                "tree",
                "type",
                "more",
                "find ",
                "findstr",
                "where",
                "whoami",
                "hostname",
                "ver",
                "echo",
                "cd",
                "chdir",
                "ipconfig",
                "systeminfo",
                "tasklist",
                "git status",
                "git log",
                "git show",
                "git diff",
                "git branch",
                "git remote",
                "dotnet --info",
                "dotnet --list"
            ];

            foreach (var prefix in readOnlyPrefixes)
            {
                if (normalized == prefix || normalized.StartsWith(prefix + " ", StringComparison.Ordinal))
                {
                    return new LlamaCommandSafetyAssessment
                    {
                        SafetyLevel = "ReadOnly",
                        RequiresAdditionalConfirmation = false,
                        Reason = "Read-only command detected."
                    };
                }
            }

            return new LlamaCommandSafetyAssessment
            {
                SafetyLevel = "Elevated",
                RequiresAdditionalConfirmation = true,
                Reason = "Command is not in read-only allowlist."
            };
        }

        [SupportedOSPlatform("windows")]
        public async Task<LlamaCommandExecutionResult> ExecuteCommandAsync(LlamaCommandRequest request, bool allowElevated = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var started = DateTime.UtcNow;
            if (request == null || string.IsNullOrWhiteSpace(request.Command))
            {
                return new LlamaCommandExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Command request is empty.",
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }

            string command = request.Command.Trim();
            string workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Environment.CurrentDirectory
                : request.WorkingDirectory.Trim();

            if (!Directory.Exists(workingDirectory))
            {
                workingDirectory = Environment.CurrentDirectory;
            }

            var safety = this.EvaluateCommandSafety(command);
            if (safety.IsBlocked)
            {
                await StaticLogger.LogAsync($"[LlamaCpp][CMD] Blocked command: {command}. Reason: {safety.Reason}");
                return new LlamaCommandExecutionResult
                {
                    Success = false,
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    ErrorMessage = safety.Reason,
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }

            if (this._settings.AgentCommandReadOnlyMode && safety.RequiresAdditionalConfirmation && !allowElevated)
            {
                return new LlamaCommandExecutionResult
                {
                    Success = false,
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    ErrorMessage = "Elevated command requires explicit additional confirmation.",
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }

            if (safety.RequiresAdditionalConfirmation && !this._settings.AgentAllowElevatedCommands)
            {
                return new LlamaCommandExecutionResult
                {
                    Success = false,
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    ErrorMessage = "Elevated commands are disabled by configuration.",
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }

            timeout ??= TimeSpan.FromSeconds(30);
            bool showWindow = request.ShowWindow || this._settings.AgentShowCommandWindow;

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                WorkingDirectory = workingDirectory,
                CreateNoWindow = !showWindow,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            await StaticLogger.LogAsync($"[LlamaCpp][CMD] Executing ({safety.SafetyLevel}): {command} (showWindow={showWindow})");

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    lock (stdoutBuilder)
                    {
                        stdoutBuilder.AppendLine(e.Data);
                    }
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    lock (stderrBuilder)
                    {
                        stderrBuilder.AppendLine(e.Data);
                    }
                }
            };

            try
            {
                if (!process.Start())
                {
                    return new LlamaCommandExecutionResult
                    {
                        Success = false,
                        Command = command,
                        WorkingDirectory = workingDirectory,
                        ErrorMessage = "Process could not be started.",
                        StartedAtUtc = started,
                        FinishedAtUtc = DateTime.UtcNow
                    };
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var timeoutCts = new CancellationTokenSource(timeout.Value);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

                await process.WaitForExitAsync(linkedCts.Token);

                string stdout;
                string stderr;
                lock (stdoutBuilder) { stdout = stdoutBuilder.ToString(); }
                lock (stderrBuilder) { stderr = stderrBuilder.ToString(); }

                int exitCode = process.ExitCode;
                bool success = exitCode == 0;

                await StaticLogger.LogAsync($"[LlamaCpp][CMD] Finished: exit={exitCode}");

                return new LlamaCommandExecutionResult
                {
                    Success = success,
                    ExitCode = exitCode,
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    StandardOutput = TrimForToolFeedback(stdout, 12000),
                    StandardError = TrimForToolFeedback(stderr, 12000),
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch
                {
                }

                return new LlamaCommandExecutionResult
                {
                    Success = false,
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    ErrorMessage = "Command timed out or was canceled.",
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[LlamaCpp][CMD] Execution failed");
                return new LlamaCommandExecutionResult
                {
                    Success = false,
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    ErrorMessage = ex.Message,
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }
        }

        public string BuildCommandResultInjectionPrompt(LlamaCommandExecutionResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Tool result: command execution");
            sb.AppendLine($"Command: {result.Command}");
            sb.AppendLine($"WorkingDirectory: {result.WorkingDirectory}");
            sb.AppendLine($"Success: {result.Success}");
            sb.AppendLine($"ExitCode: {result.ExitCode}");
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                sb.AppendLine($"Error: {result.ErrorMessage}");
            }

            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                sb.AppendLine("STDOUT:");
                sb.AppendLine(result.StandardOutput);
            }

            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                sb.AppendLine("STDERR:");
                sb.AppendLine(result.StandardError);
            }

            sb.AppendLine("Use this tool result for the next answer.");
            return sb.ToString().Trim();
        }

        private static string ExtractCommand(string assistantOutput, out string sourceSnippet)
        {
            sourceSnippet = string.Empty;

            string normalizedOutput = NormalizeAssistantOutputForTagParsing(assistantOutput);
            string parseableOutput = RemoveMarkedUpContentForToolTagParsing(normalizedOutput);

            var tagMatch = CommandTagRegex.Match(parseableOutput);
            if (tagMatch.Success)
            {
                sourceSnippet = tagMatch.Value;
                return tagMatch.Groups["cmd"].Value;
            }

            var fallbackTagMatch = CommandTagFallbackRegex.Match(parseableOutput);
            if (fallbackTagMatch.Success)
            {
                sourceSnippet = fallbackTagMatch.Value;
                return fallbackTagMatch.Groups["cmd"].Value;
            }

            return string.Empty;
        }

        private static string NormalizeAssistantOutputForTagParsing(string assistantOutput)
        {
            if (string.IsNullOrEmpty(assistantOutput))
            {
                return string.Empty;
            }

            string decoded = WebUtility.HtmlDecode(assistantOutput);
            return decoded
                .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
                .Replace("\u200B", string.Empty, StringComparison.Ordinal)
                .Trim();
        }

        private static string RemoveMarkedUpContentForToolTagParsing(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string withoutFencedCode = Regex.Replace(text, "```[\\s\\S]*?```", " ", RegexOptions.Multiline);
            string withoutInlineCode = Regex.Replace(withoutFencedCode, "`[^`\\r\\n]*`", " ");
            string withoutCodeElements = Regex.Replace(withoutInlineCode, "<code[\\s\\S]*?</code>", " ", RegexOptions.IgnoreCase);
            return Regex.Replace(withoutCodeElements, "<pre[\\s\\S]*?</pre>", " ", RegexOptions.IgnoreCase);
        }

        private static string TrimForToolFeedback(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string text = value.Trim();
            if (text.Length <= maxChars)
            {
                return text;
            }

            int head = Math.Max(256, maxChars / 2);
            int tail = Math.Max(256, maxChars - head);
            return text[..head] + "\n... [truncated] ...\n" + text[^tail..];
        }
    }
}
