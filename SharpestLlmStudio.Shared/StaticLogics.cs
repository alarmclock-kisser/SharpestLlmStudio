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






        // ── Display / rendering helpers ──

        public static string GetBaseKnowledgeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return key ?? string.Empty;
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
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;
            return content.Length <= 200 ? content : content.Substring(0, 200) + "...";
        }

        public static string GetDisplayContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return content;

            const string knowledgeMarker = "Nutze die folgenden Wissenskontexte für die Antwort";
            const string userPromptMarker = "User Prompt:";

            int kIdx = content.IndexOf(knowledgeMarker, StringComparison.OrdinalIgnoreCase);
            int uIdx = content.IndexOf(userPromptMarker, StringComparison.OrdinalIgnoreCase);

            if (kIdx >= 0 && uIdx >= 0)
            {
                return content.Substring(uIdx + userPromptMarker.Length).TrimStart();
            }

            return content;
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
            bool inList = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');

                if (line.TrimStart().StartsWith("```"))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
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

                if (line.StartsWith("#### "))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append("<h6 class=\"md-h\">").Append(InlineMarkdown(line[5..])).Append("</h6>");
                    continue;
                }
                if (line.StartsWith("### "))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append("<h5 class=\"md-h\">").Append(InlineMarkdown(line[4..])).Append("</h5>");
                    continue;
                }
                if (line.StartsWith("## "))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append("<h4 class=\"md-h\">").Append(InlineMarkdown(line[3..])).Append("</h4>");
                    continue;
                }
                if (line.StartsWith("# "))
                {
                    if (inList) { sb.Append("</ul>"); inList = false; }
                    sb.Append("<h4 class=\"md-h\">").Append(InlineMarkdown(line[2..])).Append("</h4>");
                    continue;
                }

                if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
                {
                    if (!inList) { sb.Append("<ul class=\"md-list\">"); inList = true; }
                    var itemText = line.TrimStart()[2..];
                    sb.Append("<li>").Append(InlineMarkdown(itemText)).Append("</li>");
                    continue;
                }

                if (Regex.IsMatch(line.TrimStart(), @"^\d+\.\s"))
                {
                    if (!inList) { sb.Append("<ol class=\"md-list\">"); inList = true; }
                    var match = Regex.Match(line.TrimStart(), @"^\d+\.\s(.*)");
                    sb.Append("<li>").Append(InlineMarkdown(match.Groups[1].Value)).Append("</li>");
                    continue;
                }

                if (inList) { sb.Append("</ul>"); inList = false; }

                if (string.IsNullOrWhiteSpace(line))
                {
                    sb.Append("<br/>");
                    continue;
                }

                sb.Append("<p class=\"md-p\">").Append(InlineMarkdown(line)).Append("</p>");
            }

            if (inList) sb.Append("</ul>");
            if (inCodeBlock) sb.Append("</code></pre>");

            return sb.ToString();
        }

        public static bool TryFormatJson(string text, out string formattedJson)
        {
            formattedJson = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
            {
                return false;
            }

            try
            {
                using var jsonDoc = JsonDocument.Parse(trimmed);
                formattedJson = JsonSerializer.Serialize(jsonDoc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string RenderMarkdownOrJson(string text)
        {
            if (TryFormatJson(text, out var formattedJson))
            {
                return $"<pre class=\"md-code-block\"><code>{WebUtility.HtmlEncode(formattedJson)}</code></pre>";
            }

            return RenderMarkdown(text);
        }

        public static string InlineMarkdown(string text)
        {
            var encoded = WebUtility.HtmlEncode(text);
            encoded = Regex.Replace(encoded, @"`([^`]+)`", "<code class=\"md-inline-code\">$1</code>");
            encoded = Regex.Replace(encoded, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            encoded = Regex.Replace(encoded, @"\*(.+?)\*", "<em>$1</em>");
            return encoded;
        }



        // Drawing etc.
        public static string GetSparklineSvg(IEnumerable<double> valuesInput, int width, int height, string lineColor, string fillColor, string label)
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
