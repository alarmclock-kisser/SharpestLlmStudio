using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        private static readonly Regex WebSearchTagRegex = new("<websearch>(?<query>[\\s\\S]*?)</websearch>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WebSearchCodeFenceRegex = new("```(?:websearch|search|http)\\s*(?<query>[\\s\\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WebSearchLineRegex = new("^(?:WEBSEARCH|SEARCH|URL)\\s*:\\s*(?<query>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        public bool TryExtractWebSearchRequest(string assistantOutput, out LlamaWebSearchRequest? request)
        {
            request = null;
            if (string.IsNullOrWhiteSpace(assistantOutput))
            {
                return false;
            }

            string extracted = ExtractWebSearchInput(assistantOutput, out string sourceSnippet).Trim();
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return false;
            }

            bool isUrl = Uri.TryCreate(extracted, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

            request = new LlamaWebSearchRequest
            {
                Query = isUrl ? string.Empty : extracted,
                Url = isUrl ? extracted : null,
                IsDirectUrl = isUrl,
                SourceSnippet = sourceSnippet
            };

            return true;
        }

        public async Task<LlamaWebSearchResult> ExecuteWebSearchAsync(LlamaWebSearchRequest request, CancellationToken cancellationToken = default)
        {
            var started = DateTime.UtcNow;
            if (request == null)
            {
                return new LlamaWebSearchResult
                {
                    Success = false,
                    ErrorMessage = "Web search request is empty.",
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }

            string targetUrl = BuildSearchUrl(request);
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                return new LlamaWebSearchResult
                {
                    Success = false,
                    ErrorMessage = "No valid query or URL provided.",
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }

            try
            {
                using var response = await this._httpClient.GetAsync(targetUrl, cancellationToken);
                string html = await response.Content.ReadAsStringAsync(cancellationToken);
                string text = ExtractReadableText(html);

                var result = new LlamaWebSearchResult
                {
                    Success = response.IsSuccessStatusCode,
                    EffectiveUrl = targetUrl,
                    Query = request.IsDirectUrl ? request.Url ?? string.Empty : request.Query,
                    StatusCode = (int)response.StatusCode,
                    HtmlPreview = TrimForToolFeedback(html, 12000),
                    TextPreview = TrimForToolFeedback(text, 8000),
                    ErrorMessage = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})",
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };

                await StaticLogger.LogAsync($"[LlamaCpp][WebSearch] {(result.Success ? "OK" : "FAIL")} {targetUrl} (HTTP {result.StatusCode})");
                return result;
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[LlamaCpp][WebSearch] Request failed");
                return new LlamaWebSearchResult
                {
                    Success = false,
                    EffectiveUrl = targetUrl,
                    Query = request.IsDirectUrl ? request.Url ?? string.Empty : request.Query,
                    ErrorMessage = ex.Message,
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }
        }

        public string BuildWebSearchResultInjectionPrompt(LlamaWebSearchResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Tool result: web search / fetch");
            sb.AppendLine($"URL: {result.EffectiveUrl}");
            sb.AppendLine($"Query: {result.Query}");
            sb.AppendLine($"Success: {result.Success}");
            sb.AppendLine($"StatusCode: {result.StatusCode}");
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                sb.AppendLine($"Error: {result.ErrorMessage}");
            }

            if (!string.IsNullOrWhiteSpace(result.TextPreview))
            {
                sb.AppendLine("TextPreview:");
                sb.AppendLine(result.TextPreview);
            }
            else if (!string.IsNullOrWhiteSpace(result.HtmlPreview))
            {
                sb.AppendLine("HtmlPreview:");
                sb.AppendLine(result.HtmlPreview);
            }

            sb.AppendLine("Use this retrieved web content for the next answer.");
            return sb.ToString().Trim();
        }

        private static string ExtractWebSearchInput(string assistantOutput, out string sourceSnippet)
        {
            sourceSnippet = string.Empty;

            var tagMatch = WebSearchTagRegex.Match(assistantOutput);
            if (tagMatch.Success)
            {
                sourceSnippet = tagMatch.Value;
                return tagMatch.Groups["query"].Value;
            }

            var fenceMatch = WebSearchCodeFenceRegex.Match(assistantOutput);
            if (fenceMatch.Success)
            {
                sourceSnippet = fenceMatch.Value;
                return fenceMatch.Groups["query"].Value;
            }

            var lineMatch = WebSearchLineRegex.Match(assistantOutput);
            if (lineMatch.Success)
            {
                sourceSnippet = lineMatch.Value;
                return lineMatch.Groups["query"].Value;
            }

            return string.Empty;
        }

        private static string BuildSearchUrl(LlamaWebSearchRequest request)
        {
            if (request.IsDirectUrl)
            {
                return request.Url?.Trim() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return string.Empty;
            }

            string q = WebUtility.UrlEncode(request.Query.Trim());
            return $"https://duckduckgo.com/html/?q={q}";
        }

        private static string ExtractReadableText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            string withoutScripts = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
            string withoutStyles = Regex.Replace(withoutScripts, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
            string stripped = Regex.Replace(withoutStyles, "<[^>]+>", " ", RegexOptions.IgnoreCase);
            string decoded = WebUtility.HtmlDecode(stripped);
            string normalizedWhitespace = Regex.Replace(decoded, "\\s+", " ").Trim();
            return normalizedWhitespace;
        }
    }
}
