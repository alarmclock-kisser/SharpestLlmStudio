using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpestLlmStudio.Shared
{
    public static class StaticLogics
    {
        // Static Fields
        public static int SparklineHistoryMax = 60;
        private static readonly Regex ThinkOpenTagRegex = new(
            @"(<\s*think\s*>|◁\s*think\s*▷|〈\s*think\s*〉|《\s*think\s*》|＜\s*think\s*＞|⟨\s*think\s*⟩)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ThinkCloseTagRegex = new(
            @"(<\s*/\s*think\s*>|◁\s*/\s*think\s*▷|〈\s*/\s*think\s*〉|《\s*/\s*think\s*》|＜\s*/\s*think\s*＞|⟨\s*/\s*think\s*⟩)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OrderedListRegex = new(@"^\s*\d+[\.)]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex UnorderedListRegex = new(@"^\s*[-*•]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex JsonFenceRegex = new(@"```(?:json|javascript|js|txt)?\s*(?<content>[\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlTagRegex = new(@"</?(?!think\b)[a-z][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MarkdownQuoteRegex = new(@"^\s*>+\s?", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex JsonObjectStartLineRegex = new(@"(?m)(?<=^|\n)\s*\{\s*(?=""|\}|[\r\n])", RegexOptions.Compiled);
        private static readonly Regex JsonArrayStartLineRegex = new(@"(?m)(?<=^|\n)\s*\[\s*(?=\{|\[|\]|[\r\n])", RegexOptions.Compiled);
        private static readonly Regex JsonPropertyNameRegex = new(@"(?<=\{|,)(\s*)(?<name>[A-Za-z_][A-Za-z0-9_\- ]*)(\s*):", RegexOptions.Compiled);
        private static readonly Regex TrailingCommaBeforeCloserRegex = new(@",\s*(?=[}\]])", RegexOptions.Compiled);
        private static readonly Regex MultiCommaRegex = new(@",\s*,+", RegexOptions.Compiled);
        private static readonly Regex Point2dRegex = new(@"(?:""point_2d""|point_2d)\s*:\s*\[\s*(?<x>-?\d+(?:[\.,]\d+)?)\s*,\s*(?<y>-?\d+(?:[\.,]\d+)?)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Bbox2dRegex = new(@"(?:""bbox_2d""|bbox_2d)\s*:\s*\[\s*(?<x1>-?\d+(?:[\.,]\d+)?)\s*,\s*(?<y1>-?\d+(?:[\.,]\d+)?)\s*,\s*(?<x2>-?\d+(?:[\.,]\d+)?)\s*,\s*(?<y2>-?\d+(?:[\.,]\d+)?)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);






        // ── Display / rendering helpers ──

        public static string GetBaseKnowledgeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return key ?? string.Empty;
            }

            int idx = key.IndexOf(" [chunk ", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? key.Substring(0, idx) : key;
        }

        public static string GetChunkSummary(IEnumerable<LlamaKnowledgeEntry> chunks)
        {
            var first = chunks.FirstOrDefault()?.Content ?? string.Empty;
            return first.Length <= 32 ? first : first.Substring(0, 32) + "...";
        }

        public static string GetChunkPreview(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            return content.Length <= 200 ? content : content.Substring(0, 200) + "...";
        }

        public static string GetDisplayContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            content = CollapseKnowledgePayloadsForDisplay(content);
            return SummarizeToolPayloadForDisplay(content);
        }

        private static string CollapseKnowledgePayloadsForDisplay(string content)
        {
            string updated = content;
            updated = CollapsePromptSection(updated, "Evidence Pack (retrieved + reranked):", "User Question:", "Show evidence pack");
            updated = CollapsePromptSection(updated, "Use the following knowledge context for your answer, if relevant:", "User Prompt:", "Show retrieved knowledge context");
            updated = CollapsePromptSection(updated, "Nutze die folgenden Wissenskontexte für die Antwort", "User Prompt:", "Show retrieved knowledge context");
            return updated;
        }

        private static string CollapsePromptSection(string content, string startMarker, string endMarker, string summaryText)
        {
            int startIndex = content.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0)
            {
                return content;
            }

            int endIndex = content.IndexOf(endMarker, startIndex, StringComparison.OrdinalIgnoreCase);
            if (endIndex <= startIndex)
            {
                return content;
            }

            string before = content[..startIndex].TrimEnd();
            string collapsedBlock = content[startIndex..endIndex].Trim();
            string after = content[endIndex..].TrimStart();
            string encodedBlock = WebUtility.HtmlEncode(collapsedBlock);

            string details = $"<details class=\"tool-cmd-details tool-rag-details\"><summary>{WebUtility.HtmlEncode(summaryText)}</summary><pre class=\"tool-raw\"><code>{encodedBlock}</code></pre></details>";

            var sb = new StringBuilder();
            // If there is no 'before' content (e.g. the evidence pack starts the message),
            // show the user's question (the 'after' part) first and then the collapsible
            // evidence/details block. This ensures the original user message appears above
            // the evidence pack in the UI.
            if (string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after))
            {
                sb.AppendLine(after);
                sb.AppendLine(details);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(before))
                {
                    sb.AppendLine(before);
                }

                sb.AppendLine(details);

                if (!string.IsNullOrWhiteSpace(after))
                {
                    sb.Append(after);
                }
            }

            return sb.ToString();
        }

        public static string RenderMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var lines = text.Split('\n');
            var sb = new StringBuilder();
            bool inCodeBlock = false;
            string? currentListTag = null;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');

                if (line.TrimStart().StartsWith("```"))
                {
                    CloseCurrentList(sb, ref currentListTag);
                    if (inCodeBlock)
                    {
                        sb.Append("</code></pre>");
                        inCodeBlock = false;
                    }
                    else
                    {
                        sb.Append("<pre class=\"md-code-block\"><code>");
                        inCodeBlock = true;
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    sb.Append(WebUtility.HtmlEncode(line)).Append('\n');
                    continue;
                }

                if (line.StartsWith("<details class=\"tool-", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("<details class=\"think-block\"", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("</details>", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("<summary>", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("<summary class=\"think-summary\"", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("</summary>", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("<pre class=\"tool-", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("</pre>", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("<code>", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("</code>", StringComparison.OrdinalIgnoreCase))
                {
                    CloseCurrentList(sb, ref currentListTag);
                    sb.Append(line);
                    continue;
                }

                if (line.StartsWith("#### "))
                {
                    CloseCurrentList(sb, ref currentListTag);
                    sb.Append("<h6 class=\"md-h\">").Append(InlineMarkdown(line[5..])).Append("</h6>");
                    continue;
                }
                if (line.StartsWith("### "))
                {
                    CloseCurrentList(sb, ref currentListTag);
                    sb.Append("<h5 class=\"md-h\">").Append(InlineMarkdown(line[4..])).Append("</h5>");
                    continue;
                }
                if (line.StartsWith("## "))
                {
                    CloseCurrentList(sb, ref currentListTag);
                    sb.Append("<h4 class=\"md-h\">").Append(InlineMarkdown(line[3..])).Append("</h4>");
                    continue;
                }
                if (line.StartsWith("# "))
                {
                    CloseCurrentList(sb, ref currentListTag);
                    sb.Append("<h4 class=\"md-h\">").Append(InlineMarkdown(line[2..])).Append("</h4>");
                    continue;
                }

                var unorderedMatch = UnorderedListRegex.Match(line);
                if (unorderedMatch.Success)
                {
                    EnsureList(sb, ref currentListTag, "ul");
                    sb.Append("<li>").Append(InlineMarkdown(unorderedMatch.Groups[1].Value)).Append("</li>");
                    continue;
                }

                var orderedMatch = OrderedListRegex.Match(line);
                if (orderedMatch.Success)
                {
                    EnsureList(sb, ref currentListTag, "ol");
                    sb.Append("<li>").Append(InlineMarkdown(orderedMatch.Groups[1].Value)).Append("</li>");
                    continue;
                }

                CloseCurrentList(sb, ref currentListTag);

                if (string.IsNullOrWhiteSpace(line))
                {
                    sb.Append("<br/>");
                    continue;
                }

                sb.Append("<p class=\"md-p\">").Append(InlineMarkdown(line)).Append("</p>");
            }

            CloseCurrentList(sb, ref currentListTag);

            if (inCodeBlock)
            {
                sb.Append("</code></pre>");
            }

            return sb.ToString();
        }

        public static bool TryFormatJson(string text, out string formattedJson)
        {
            formattedJson = string.Empty;
            return TryParseJsonDocumentRelaxed(text, out _, out formattedJson);
        }

        public static bool TryParseJsonDocumentRelaxed(string text, out JsonDocument? jsonDocument, out string normalizedJson)
        {
            jsonDocument = null;
            normalizedJson = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (string candidate in EnumerateJsonCandidatesRelaxed(text))
            {
                if (TryParseJsonCandidate(candidate, out jsonDocument, out normalizedJson))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseJsonCandidate(string candidate, out JsonDocument? jsonDocument, out string normalizedJson)
        {
            jsonDocument = null;
            normalizedJson = string.Empty;

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            string prepared = StripJsonCodeFences(candidate).Trim();
            if (!LooksLikeJsonStart(prepared))
            {
                return false;
            }

            if (TryParseJsonText(prepared, out jsonDocument, out normalizedJson))
            {
                return true;
            }

            if (TrySynthesizeJsonCandidate(prepared, out string synthesized)
                && !string.Equals(synthesized, prepared, StringComparison.Ordinal)
                && TryParseJsonText(synthesized, out jsonDocument, out normalizedJson))
            {
                return true;
            }

            string repaired = RepairJsonText(prepared);
            if (!string.Equals(repaired, prepared, StringComparison.Ordinal)
                && TryParseJsonText(repaired, out jsonDocument, out normalizedJson))
            {
                return true;
            }

            if (TrySynthesizeJsonCandidate(repaired, out string repairedSynthesized)
                && !string.Equals(repairedSynthesized, repaired, StringComparison.Ordinal)
                && TryParseJsonText(repairedSynthesized, out jsonDocument, out normalizedJson))
            {
                return true;
            }

            if (TryExtractGeometryJsonCandidate(prepared, out string extractedGeometry)
                && TryParseJsonText(extractedGeometry, out jsonDocument, out normalizedJson))
            {
                return true;
            }

            if (!string.Equals(repaired, prepared, StringComparison.Ordinal)
                && TryExtractGeometryJsonCandidate(repaired, out string repairedGeometry)
                && TryParseJsonText(repairedGeometry, out jsonDocument, out normalizedJson))
            {
                return true;
            }

            return false;
        }

        private static bool TryParseJsonText(string text, out JsonDocument? jsonDocument, out string normalizedJson)
        {
            jsonDocument = null;
            normalizedJson = string.Empty;

            try
            {
                jsonDocument = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
                normalizedJson = JsonSerializer.Serialize(jsonDocument.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                return true;
            }
            catch
            {
                jsonDocument?.Dispose();
                jsonDocument = null;
                normalizedJson = string.Empty;
                return false;
            }
        }

        private static IEnumerable<string> EnumerateJsonCandidatesRelaxed(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            string trimmed = text.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                yield return trimmed;
            }

            string withoutThinkBlocks = RemoveThinkBlocks(trimmed).Trim();
            if (!string.IsNullOrWhiteSpace(withoutThinkBlocks) && !string.Equals(withoutThinkBlocks, trimmed, StringComparison.Ordinal))
            {
                yield return withoutThinkBlocks;
            }

            foreach (Match fenceMatch in JsonFenceRegex.Matches(withoutThinkBlocks))
            {
                string fencedContent = fenceMatch.Groups["content"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(fencedContent))
                {
                    yield return fencedContent;
                }
            }

            string withoutFences = StripJsonCodeFences(withoutThinkBlocks).Trim();
            if (!string.IsNullOrWhiteSpace(withoutFences) && !string.Equals(withoutFences, withoutThinkBlocks, StringComparison.Ordinal))
            {
                yield return withoutFences;
            }

            string sanitizedMarkup = SanitizeJsonCarrierText(withoutFences).Trim();
            if (!string.IsNullOrWhiteSpace(sanitizedMarkup)
                && !string.Equals(sanitizedMarkup, withoutFences, StringComparison.Ordinal)
                && !string.Equals(sanitizedMarkup, trimmed, StringComparison.Ordinal))
            {
                yield return sanitizedMarkup;
            }

            if (TryExtractJsonSubstring(withoutFences, out string? extracted)
                && !string.IsNullOrWhiteSpace(extracted)
                && !string.Equals(extracted, trimmed, StringComparison.Ordinal)
                && !string.Equals(extracted, withoutFences, StringComparison.Ordinal))
            {
                yield return extracted;
            }

            if (TryExtractJsonSubstring(sanitizedMarkup, out string? sanitizedExtracted)
                && !string.IsNullOrWhiteSpace(sanitizedExtracted)
                && !string.Equals(sanitizedExtracted, sanitizedMarkup, StringComparison.Ordinal)
                && !string.Equals(sanitizedExtracted, withoutFences, StringComparison.Ordinal))
            {
                yield return sanitizedExtracted;
            }
        }

        private static string StripJsonCodeFences(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

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

            string firstLine = trimmed[..firstLineEnd].Trim();
            if (!firstLine.StartsWith("```", StringComparison.Ordinal))
            {
                return trimmed;
            }

            int lastFenceStart = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFenceStart <= firstLineEnd)
            {
                return trimmed[(firstLineEnd + 1)..].Trim();
            }

            return trimmed[(firstLineEnd + 1)..lastFenceStart].Trim();
        }

        private static string RemoveThinkBlocks(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            int pos = 0;

            while (pos < text.Length)
            {
                Match openMatch = ThinkOpenTagRegex.Match(text, pos);
                if (!openMatch.Success)
                {
                    sb.Append(text[pos..]);
                    break;
                }

                if (openMatch.Index > pos)
                {
                    sb.Append(text, pos, openMatch.Index - pos);
                }

                Match closeMatch = ThinkCloseTagRegex.Match(text, openMatch.Index + openMatch.Length);
                pos = closeMatch.Success ? closeMatch.Index + closeMatch.Length : text.Length;
            }

            return sb.ToString();
        }

        private static string SanitizeJsonCarrierText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string cleaned = RemoveThinkBlocks(text);
            cleaned = JsonFenceRegex.Replace(cleaned, m => m.Groups["content"].Value);
            cleaned = HtmlTagRegex.Replace(cleaned, " ");
            cleaned = MarkdownQuoteRegex.Replace(cleaned, string.Empty);
            cleaned = cleaned.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
            return cleaned.Trim();
        }

        private static bool TryExtractJsonSubstring(string text, out string? candidate)
        {
            candidate = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            int start = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is '{' or '[')
                {
                    start = i;
                    break;
                }
            }

            if (start < 0)
            {
                return false;
            }

            int end = text.Length - 1;
            while (end > start && char.IsWhiteSpace(text[end]))
            {
                end--;
            }

            while (end > start && text[end] is not '}' and not ']')
            {
                end--;
            }

            if (end <= start)
            {
                candidate = text[start..].Trim();
                return !string.IsNullOrWhiteSpace(candidate);
            }

            candidate = text[start..(end + 1)].Trim();
            return !string.IsNullOrWhiteSpace(candidate);
        }

        private static bool LooksLikeJsonStart(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            char first = text.TrimStart()[0];
            return first is '{' or '[';
        }

        private static string RepairJsonText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            text = SanitizeJsonCarrierText(text);
            text = NormalizeCommonJsonMistakes(text);

            var sb = new StringBuilder(text.Length + 16);
            var closers = new Stack<char>();
            bool inString = false;
            bool escaping = false;
            char lastSignificant = '\0';

            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                if (inString)
                {
                    sb.Append(current);
                    if (escaping)
                    {
                        escaping = false;
                    }
                    else if (current == '\\')
                    {
                        escaping = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                        lastSignificant = '"';
                    }

                    continue;
                }

                if (current == '"')
                {
                    if (ShouldInsertComma(lastSignificant, current))
                    {
                        sb.Append(',');
                        lastSignificant = ',';
                    }

                    sb.Append(current);
                    inString = true;
                    continue;
                }

                if (char.IsWhiteSpace(current))
                {
                    sb.Append(current);
                    continue;
                }

                if (current is '{' or '[')
                {
                    if (ShouldInsertComma(lastSignificant, current))
                    {
                        sb.Append(',');
                        lastSignificant = ',';
                    }

                    closers.Push(current == '{' ? '}' : ']');
                    sb.Append(current);
                    lastSignificant = current;
                    continue;
                }

                if (current is '}' or ']')
                {
                    while (closers.Count > 0 && closers.Peek() != current)
                    {
                        char autoClosed = closers.Pop();
                        if (lastSignificant is not ':' and not ',' and not '{' and not '[')
                        {
                            sb.Append(autoClosed);
                            lastSignificant = autoClosed;
                        }
                    }

                    if (closers.Count == 0)
                    {
                        continue;
                    }

                    closers.Pop();
                    sb.Append(current);
                    lastSignificant = current;
                    continue;
                }

                if (ShouldInsertComma(lastSignificant, current))
                {
                    sb.Append(',');
                    lastSignificant = ',';
                }

                sb.Append(current);
                if (!char.IsWhiteSpace(current))
                {
                    lastSignificant = current;
                }
            }

            if (inString)
            {
                sb.Append('"');
            }

            while (closers.Count > 0)
            {
                char closer = closers.Pop();
                if (lastSignificant is ':' or ',')
                {
                    break;
                }

                sb.Append(closer);
                lastSignificant = closer;
            }

            return sb.ToString().Trim();
        }

        private static bool TrySynthesizeJsonCandidate(string text, out string synthesized)
        {
            synthesized = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string cleaned = SanitizeJsonCarrierText(text).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return false;
            }

            List<string> objectFragments = ExtractTopLevelObjectFragments(cleaned);
            if (objectFragments.Count >= 2)
            {
                synthesized = "[\n" + string.Join(",\n", objectFragments.Select(RepairJsonText)) + "\n]";
                return true;
            }

            if (objectFragments.Count == 1 && StartsWithArrayLikeWrapper(cleaned))
            {
                synthesized = "[\n" + RepairJsonText(objectFragments[0]) + "\n]";
                return true;
            }

            return false;
        }

        private static bool TryExtractGeometryJsonCandidate(string text, out string synthesized)
        {
            synthesized = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string cleaned = SanitizeJsonCarrierText(text);
            var items = new List<string>();

            foreach (Match match in Point2dRegex.Matches(cleaned))
            {
                if (TryParseRelaxedNumber(match.Groups["x"].Value, out double x)
                    && TryParseRelaxedNumber(match.Groups["y"].Value, out double y))
                {
                    items.Add($"{{\"point_2d\":[{FormatJsonNumber(x)},{FormatJsonNumber(y)}]}}");
                }
            }

            foreach (Match match in Bbox2dRegex.Matches(cleaned))
            {
                if (TryParseRelaxedNumber(match.Groups["x1"].Value, out double x1)
                    && TryParseRelaxedNumber(match.Groups["y1"].Value, out double y1)
                    && TryParseRelaxedNumber(match.Groups["x2"].Value, out double x2)
                    && TryParseRelaxedNumber(match.Groups["y2"].Value, out double y2))
                {
                    items.Add($"{{\"bbox_2d\":[{FormatJsonNumber(x1)},{FormatJsonNumber(y1)},{FormatJsonNumber(x2)},{FormatJsonNumber(y2)}]}}");
                }
            }

            if (items.Count == 0)
            {
                return false;
            }

            synthesized = "[\n" + string.Join(",\n", items) + "\n]";
            return true;
        }

        private static bool TryParseRelaxedNumber(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = text.Trim().Replace(',', '.');
            return double.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static string FormatJsonNumber(double value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool StartsWithArrayLikeWrapper(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            char first = text.TrimStart()[0];
            return first == '[' || first == '{';
        }

        private static List<string> ExtractTopLevelObjectFragments(string text)
        {
            var fragments = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return fragments;
            }

            int depth = 0;
            int start = -1;
            bool inString = false;
            bool escaping = false;

            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];

                if (inString)
                {
                    if (escaping)
                    {
                        escaping = false;
                    }
                    else if (current == '\\')
                    {
                        escaping = true;
                    }
                    else if (current == '"')
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

                if (current == '{')
                {
                    if (depth == 0)
                    {
                        start = i;
                    }

                    depth++;
                    continue;
                }

                if (current == '}' && depth > 0)
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        string fragment = text[start..(i + 1)].Trim();
                        if (!string.IsNullOrWhiteSpace(fragment))
                        {
                            fragments.Add(fragment);
                        }

                        start = -1;
                    }
                }
            }

            if (depth > 0 && start >= 0)
            {
                string fragment = RepairJsonText(text[start..].Trim());
                if (!string.IsNullOrWhiteSpace(fragment))
                {
                    fragments.Add(fragment);
                }
            }

            return fragments;
        }

        private static string NormalizeCommonJsonMistakes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text.Trim();
            normalized = JsonPropertyNameRegex.Replace(normalized, m => $"{m.Groups[1].Value}\"{m.Groups["name"].Value.Trim()}\"{m.Groups[3].Value}:");
            normalized = JsonArrayStartLineRegex.Replace(normalized, "[");
            normalized = JsonObjectStartLineRegex.Replace(normalized, "{");
            normalized = normalized.Replace("\r\n", "\n");
            normalized = normalized.Replace("}\n{", "},\n{");
            normalized = normalized.Replace("]\n[", "],\n[");
            normalized = normalized.Replace("}\n\"", "},\n\"");
            normalized = normalized.Replace("\"\n\"", "\",\n\"");
            normalized = TrailingCommaBeforeCloserRegex.Replace(normalized, string.Empty);
            normalized = MultiCommaRegex.Replace(normalized, ", ");
            return normalized;
        }

        private static bool ShouldInsertComma(char lastSignificant, char current)
        {
            if (lastSignificant == '\0' || lastSignificant is '{' or '[' or ':' or ',')
            {
                return false;
            }

            return current is '{' or '[' or '"' or '-'
                || char.IsDigit(current)
                || current is 't' or 'f' or 'n';
        }

        public static string RenderMarkdownOrJson(string text)
        {
            if (TryFormatJson(text, out var formattedJson))
            {
                return $"<pre class=\"md-code-block\"><code>{WebUtility.HtmlEncode(formattedJson)}</code></pre>";
            }

            return RenderWithThinkBlocks(text);
        }

        private static string RenderWithThinkBlocks(string text)
        {
            if (string.IsNullOrEmpty(text) || !ContainsThinkOpenTag(text))
            {
                return RenderMarkdown(text);
            }

            var sb = new StringBuilder();
            int pos = 0;

            while (pos < text.Length)
            {
                var openMatch = ThinkOpenTagRegex.Match(text, pos);
                if (!openMatch.Success)
                {
                    sb.Append(RenderMarkdown(text[pos..]));
                    break;
                }

                int thinkStart = openMatch.Index;

                if (thinkStart > pos)
                {
                    sb.Append(RenderMarkdown(text[pos..thinkStart]));
                }

                int contentStart = openMatch.Index + openMatch.Length;
                var closeMatch = ThinkCloseTagRegex.Match(text, contentStart);

                string thinkContent;
                bool isComplete;

                if (closeMatch.Success)
                {
                    thinkContent = text[contentStart..closeMatch.Index].Trim();
                    pos = closeMatch.Index + closeMatch.Length;
                    isComplete = true;
                }
                else
                {
                    thinkContent = text[contentStart..].Trim();
                    pos = text.Length;
                    isComplete = false;
                }

                string lastLine = string.Empty;
                var lines = thinkContent.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var trimmed = lines[i].Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        lastLine = trimmed;
                        break;
                    }
                }

                string statusLabel = isComplete ? "Thought Process" : "Thinking\u2026";
                string preview = string.IsNullOrEmpty(lastLine)
                    ? statusLabel
                    : WebUtility.HtmlEncode(lastLine.Length > 120 ? lastLine[..120] + "\u2026" : lastLine);

                sb.Append($"<details class=\"think-block\" data-think-complete=\"{(isComplete ? "true" : "false")}\">");
                sb.Append($"<summary class=\"think-summary\"><span class=\"think-label\">\U0001f4ad {statusLabel}</span><span class=\"think-preview\">{preview}</span></summary>");
                sb.Append("<div class=\"think-content\">");
                sb.Append(RenderMarkdown(thinkContent));
                sb.Append("</div></details>");
            }

            return sb.ToString();
        }

        private static bool ContainsThinkOpenTag(string text)
        {
            return ThinkOpenTagRegex.IsMatch(text);
        }

        private static void EnsureList(StringBuilder sb, ref string? currentListTag, string targetListTag)
        {
            if (string.Equals(currentListTag, targetListTag, StringComparison.Ordinal))
            {
                return;
            }

            CloseCurrentList(sb, ref currentListTag);
            sb.Append($"<{targetListTag} class=\"md-list\">");
            currentListTag = targetListTag;
        }

        private static void CloseCurrentList(StringBuilder sb, ref string? currentListTag)
        {
            if (string.IsNullOrEmpty(currentListTag))
            {
                return;
            }

            sb.Append($"</{currentListTag}>");
            currentListTag = null;
        }

        public static string InlineMarkdown(string text)
        {
            var encoded = WebUtility.HtmlEncode(text);
            encoded = Regex.Replace(encoded, @"`([^`]+)`", "<code class=\"md-inline-code\">$1</code>");
            encoded = Regex.Replace(encoded, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            encoded = Regex.Replace(encoded, @"\*(.+?)\*", "<em>$1</em>");
            encoded = Regex.Replace(encoded, "(?<![\"'>])(https?://[^\\s<]+)", "<a class=\"md-link\" href=\"$1\" target=\"_blank\" rel=\"noopener noreferrer\">$1</a>", RegexOptions.IgnoreCase);
            return encoded;
        }

        private static string SummarizeToolPayloadForDisplay(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            string updated = Regex.Replace(
                content,
                @"Tool result:\s*command execution[\s\S]*?Use this tool result for the next answer\.",
                match => BuildCommandDisplaySummary(match.Value),
                RegexOptions.IgnoreCase);

            updated = Regex.Replace(
                updated,
                @"Tool result:\s*web search\s*/\s*fetch[\s\S]*?Use this retrieved web content for the next answer\.",
                match => BuildWebSearchDisplaySummary(match.Value),
                RegexOptions.IgnoreCase);

            return updated;
        }

        private static string BuildCommandDisplaySummary(string block)
        {
            string command = ExtractToolLineValue(block, "Command:");
            string success = ExtractToolLineValue(block, "Success:");
            string exitCode = ExtractToolLineValue(block, "ExitCode:");
            string shortCommand = Truncate(command, 120);
            string header = $"[CMD result] {shortCommand} | success={success} | exit={exitCode}";
            string raw = WebUtility.HtmlEncode(block.Trim());
            return header
                + "\n<details class=\"tool-cmd-details\"><summary>Show raw CMD result</summary>"
                + $"<pre class=\"tool-raw\"><code>{raw}</code></pre>"
                + "</details>";
        }

        private static string BuildWebSearchDisplaySummary(string block)
        {
            string url = ExtractToolLineValue(block, "URL:");
            string query = ExtractToolLineValue(block, "Query:");
            string success = ExtractToolLineValue(block, "Success:");
            string statusCode = ExtractToolLineValue(block, "StatusCode:");

            string source = !string.IsNullOrWhiteSpace(query) ? query : url;
            source = Truncate(source, 120);
            string header = $"[WebSearch result] {source} | success={success} | status={statusCode}";
            string raw = WebUtility.HtmlEncode(block.Trim());
            return header
                + "\n<details class=\"tool-websearch-details\"><summary>Show raw WebSearch result</summary>"
                + $"<pre class=\"tool-raw\"><code>{raw}</code></pre>"
                + "</details>";
        }

        private static string ExtractToolLineValue(string block, string prefix)
        {
            var match = Regex.Match(block, $"^{Regex.Escape(prefix)}\\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value[..maxLength] + "...";
        }



        // Drawing etc.
        public static string GetSparklineSvg(IEnumerable<double> valuesInput, int width, int height, string lineColor, string fillColor, string label)
        {
            try
            {
                var valsRaw = valuesInput.Select(v => Math.Clamp(v, 0.0, 100.0)).ToList();
                if (valsRaw.Count == 0)
                {
                    return string.Empty;
                }

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

        public static void AppendHistory(Queue<double> history, double value)
        {
            history.Enqueue(Math.Clamp(value, 0.0, 100.0));
            while (history.Count > SparklineHistoryMax)
            {
                _ = history.Dequeue();
            }
        }

        public static string GetLighterColorGradient(string baseColor, int amount = 92)
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
