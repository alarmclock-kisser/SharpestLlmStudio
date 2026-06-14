using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpestLlmStudio.Shared;

namespace SharpestLlmStudio.Runtime
{
    public sealed class LlamaCppUpdateCheckResult
    {
        public bool Success { get; set; }
        public bool ExecutableFound { get; set; }
        public bool UpdateAvailable { get; set; }
        public bool HasCudaRuntimeBinaries { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string StatusMessage { get; set; } = string.Empty;
        public string CudaRuntimeSummary { get; set; } = string.Empty;
        public string ResolvedExecutablePath { get; set; } = string.Empty;
        public string InstallDirectory { get; set; } = string.Empty;
        public string Backend { get; set; } = string.Empty;
        public string BackendAssetHint { get; set; } = string.Empty;
        public string InstalledVersion { get; set; } = string.Empty;
        public DateTime? InstalledFileDateUtc { get; set; }
        public string LatestTag { get; set; } = string.Empty;
        public DateTime? LatestPublishedAtUtc { get; set; }
        public string LatestReleaseNotes { get; set; } = string.Empty;
        public string MatchedAssetName { get; set; } = string.Empty;
        public string MatchedAssetDownloadUrl { get; set; } = string.Empty;
    }

    public sealed class LlamaCppUpdateProgress
    {
        public string Stage { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int Percent { get; set; }
        public long BytesReceived { get; set; }
        public long? TotalBytes { get; set; }
    }

    public sealed class LlamaCppUpdateResult
    {
        public bool Success { get; set; }
        public bool UpToDate { get; set; }
        public string Message { get; set; } = string.Empty;
        public LlamaCppUpdateCheckResult? UpdateInfo { get; set; }
    }

    [SupportedOSPlatform("windows")]
    public partial class LlamaCppClient
    {
        private const string LlamaCppGitHubReleasesApiUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=8";

        public async Task<LlamaCppUpdateCheckResult> CheckForLlamaCppUpdateAsync(CancellationToken cancellationToken = default)
        {
            var result = new LlamaCppUpdateCheckResult();

            try
            {
                string executablePath = ResolveExecutablePath(this._settings.ServerExecutablePath);
                result.ResolvedExecutablePath = executablePath;
                result.InstallDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    result.Success = true;
                    result.ExecutableFound = false;
                    result.StatusMessage = "No local llama-server.exe installation was found.";
                    return result;
                }

                result.ExecutableFound = true;
                result.InstalledFileDateUtc = File.GetLastWriteTimeUtc(executablePath);

                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                result.InstalledVersion = FirstNonEmpty(
                    versionInfo.ProductVersion,
                    versionInfo.FileVersion,
                    versionInfo.FileDescription);

                List<string> installFileNames = GetInstallFileNames(executablePath, result.InstallDirectory);
                (result.Backend, result.BackendAssetHint) = DetectInstalledBackend(installFileNames);
                result.HasCudaRuntimeBinaries = TryDetectCudaRuntimeBinaries(installFileNames, out string cudaRuntimeSummary);
                result.CudaRuntimeSummary = cudaRuntimeSummary;

                await StaticLogger.LogAsync($"[LlamaCpp][Update] Checking updates for '{executablePath}' (backend={result.Backend}, hint={result.BackendAssetHint}, installedVersion={result.InstalledVersion}, fileDateUtc={result.InstalledFileDateUtc:O}).");

                using var client = CreateGitHubHttpClient();
                List<GitHubReleaseInfo> releases = await FetchGitHubReleasesAsync(client, cancellationToken);
                GitHubReleaseInfo? matchedRelease = null;
                GitHubAssetInfo? matchedAsset = null;

                foreach (GitHubReleaseInfo release in releases)
                {
                    GitHubAssetInfo? asset = SelectBestWindowsBinaryAsset(release.Assets, result.Backend, result.BackendAssetHint);
                    if (asset != null)
                    {
                        matchedRelease = release;
                        matchedAsset = asset;
                        break;
                    }
                }

                if (matchedRelease == null || matchedAsset == null)
                {
                    result.Success = true;
                    result.StatusMessage = $"No matching Windows {result.Backend.ToUpperInvariant()} release asset was found on GitHub.";
                    return result;
                }

                result.LatestTag = matchedRelease.TagName;
                result.LatestPublishedAtUtc = matchedRelease.PublishedAtUtc;
                result.LatestReleaseNotes = matchedRelease.Body;
                result.MatchedAssetName = matchedAsset.Name;
                result.MatchedAssetDownloadUrl = matchedAsset.DownloadUrl;
                result.UpdateAvailable = IsNewerBuildAvailable(result);
                result.Success = true;
                result.StatusMessage = result.UpdateAvailable
                    ? $"A newer llama.cpp build is available: {result.LatestTag}."
                    : $"No newer llama.cpp build is available. Installed build date: {FormatUtcDate(result.InstalledFileDateUtc)}.";

                return result;
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[LlamaCpp][Update] Update check failed");
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.StatusMessage = $"Could not check llama.cpp updates: {ex.Message}";
                return result;
            }
        }

        public async Task<LlamaCppUpdateResult> UpdateLlamaCppAsync(IProgress<LlamaCppUpdateProgress>? progress = null, bool includeCudaRuntimeBinaries = false, CancellationToken cancellationToken = default)
        {
            LlamaCppUpdateCheckResult check = await this.CheckForLlamaCppUpdateAsync(cancellationToken);
            if (!check.Success)
            {
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = check.StatusMessage,
                    UpdateInfo = check
                };
            }

            if (!check.ExecutableFound)
            {
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = check.StatusMessage,
                    UpdateInfo = check
                };
            }

            if (!check.UpdateAvailable)
            {
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    UpToDate = true,
                    Message = check.StatusMessage,
                    UpdateInfo = check
                };
            }

            List<int> runningProcessIds = GetRunningProcessIdsUsingExecutable(check.ResolvedExecutablePath);
            if (runningProcessIds.Count > 0)
            {
                string processList = string.Join(", ", runningProcessIds);
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = $"Stop the running llama-server.exe instance(s) first: {processList}.",
                    UpdateInfo = check
                };
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "SharpestLlmStudio", "llama-update", Guid.NewGuid().ToString("N"));
            string downloadPath = Path.Combine(tempRoot, check.MatchedAssetName);
            string extractPath = Path.Combine(tempRoot, "extract");
            string backupPath = Path.Combine(tempRoot, "backup");
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(extractPath);
            Directory.CreateDirectory(backupPath);

            try
            {
                progress?.Report(new LlamaCppUpdateProgress
                {
                    Stage = "download",
                    Message = $"Downloading {check.MatchedAssetName}...",
                    Percent = 0
                });

                await StaticLogger.LogAsync($"[LlamaCpp][Update] Downloading asset '{check.MatchedAssetName}' from {check.MatchedAssetDownloadUrl}");
                using (var httpClient = CreateGitHubHttpClient())
                {
                    await DownloadFileWithProgressAsync(httpClient, check.MatchedAssetDownloadUrl, downloadPath, progress, cancellationToken);
                }

                progress?.Report(new LlamaCppUpdateProgress
                {
                    Stage = "extract",
                    Message = "Extracting llama.cpp update package...",
                    Percent = 75
                });

                ZipFile.ExtractToDirectory(downloadPath, extractPath, overwriteFiles: true);
                string? extractedBinaryDirectory = FindExtractedBinaryDirectory(extractPath);
                if (string.IsNullOrWhiteSpace(extractedBinaryDirectory))
                {
                    return new LlamaCppUpdateResult
                    {
                        Success = false,
                        Message = "The downloaded package did not contain a llama-server.exe binary.",
                        UpdateInfo = check
                    };
                }

                progress?.Report(new LlamaCppUpdateProgress
                {
                    Stage = "install",
                    Message = "Installing updated llama.cpp binaries...",
                    Percent = 80
                });

                CopyUpdateFilesWithRollback(extractedBinaryDirectory, check.InstallDirectory, backupPath, progress, cancellationToken, includeCudaRuntimeBinaries, onlyCudaRuntimeBinaries: false);

                progress?.Report(new LlamaCppUpdateProgress
                {
                    Stage = "complete",
                    Message = includeCudaRuntimeBinaries
                        ? $"Updated llama.cpp to {check.LatestTag}, including CUDA runtime binaries."
                        : $"Updated llama.cpp to {check.LatestTag}.",
                    Percent = 100
                });

                await StaticLogger.LogAsync($"[LlamaCpp][Update] Successfully updated '{check.InstallDirectory}' using asset '{check.MatchedAssetName}'.");

                return new LlamaCppUpdateResult
                {
                    Success = true,
                    Message = includeCudaRuntimeBinaries
                        ? $"llama.cpp was updated to {check.LatestTag}, including CUDA runtime binaries."
                        : $"llama.cpp was updated to {check.LatestTag}.",
                    UpdateInfo = check
                };
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[LlamaCpp][Update] Update failed");
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = $"llama.cpp update failed: {ex.Message}",
                    UpdateInfo = check
                };
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        public async Task<LlamaCppUpdateResult> ReDownloadCudaRuntimeBinariesAsync(IProgress<LlamaCppUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            LlamaCppUpdateCheckResult check = await this.CheckForLlamaCppUpdateAsync(cancellationToken);
            if (!check.Success)
            {
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = check.StatusMessage,
                    UpdateInfo = check
                };
            }

            if (!check.ExecutableFound)
            {
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = check.StatusMessage,
                    UpdateInfo = check
                };
            }

            if (!check.HasCudaRuntimeBinaries)
            {
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = "No CUDA runtime binaries were detected in the llama.cpp directory.",
                    UpdateInfo = check
                };
            }

            List<int> runningProcessIds = GetRunningProcessIdsUsingExecutable(check.ResolvedExecutablePath);
            if (runningProcessIds.Count > 0)
            {
                string processList = string.Join(", ", runningProcessIds);
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = $"Stop the running llama-server.exe instance(s) first: {processList}.",
                    UpdateInfo = check
                };
            }

            using var client = CreateGitHubHttpClient();
            List<GitHubReleaseInfo> releases = await FetchGitHubReleasesAsync(client, cancellationToken);
            GitHubReleaseInfo? matchedRuntimeRelease = null;
            GitHubAssetInfo? matchedRuntimeAsset = null;

            foreach (GitHubReleaseInfo release in releases)
            {
                GitHubAssetInfo? asset = SelectBestWindowsCudaRuntimeAsset(release.Assets, check.Backend, check.BackendAssetHint);
                if (asset != null)
                {
                    matchedRuntimeRelease = release;
                    matchedRuntimeAsset = asset;
                    break;
                }
            }

            if (matchedRuntimeRelease == null || matchedRuntimeAsset == null)
            {
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = "No matching Windows CUDA runtime package was found on GitHub.",
                    UpdateInfo = check
                };
            }

            GitHubReleaseInfo runtimeRelease = matchedRuntimeRelease;
            GitHubAssetInfo runtimeAsset = matchedRuntimeAsset;

            string tempRoot = Path.Combine(Path.GetTempPath(), "SharpestLlmStudio", "llama-update", Guid.NewGuid().ToString("N"));
            string downloadPath = Path.Combine(tempRoot, runtimeAsset.Name);
            string extractPath = Path.Combine(tempRoot, "extract");
            string backupPath = Path.Combine(tempRoot, "backup");
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(extractPath);
            Directory.CreateDirectory(backupPath);

            try
            {
                progress?.Report(new LlamaCppUpdateProgress
                {
                    Stage = "download",
                    Message = $"Downloading CUDA runtime binaries from {runtimeAsset.Name}...",
                    Percent = 0
                });

                await StaticLogger.LogAsync($"[LlamaCpp][Update] Re-downloading CUDA runtime binaries from asset '{runtimeAsset.Name}' ({check.CudaRuntimeSummary}).");
                await DownloadFileWithProgressAsync(client, runtimeAsset.DownloadUrl, downloadPath, progress, cancellationToken);

                progress?.Report(new LlamaCppUpdateProgress
                {
                    Stage = "extract",
                    Message = "Extracting CUDA runtime binaries...",
                    Percent = 75
                });

                ZipFile.ExtractToDirectory(downloadPath, extractPath, overwriteFiles: true);
                string? extractedRuntimeDirectory = FindExtractedCudaRuntimeDirectory(extractPath);
                if (string.IsNullOrWhiteSpace(extractedRuntimeDirectory))
                {
                    return new LlamaCppUpdateResult
                    {
                        Success = false,
                        Message = "The downloaded package did not contain CUDA runtime DLLs.",
                        UpdateInfo = check
                    };
                }

                progress?.Report(new LlamaCppUpdateProgress
                {
                    Stage = "install",
                    Message = "Installing CUDA runtime binaries only...",
                    Percent = 80
                });

                if (!CopyUpdateFilesWithRollback(extractedRuntimeDirectory, check.InstallDirectory, backupPath, progress, cancellationToken, includeCudaRuntimeBinaries: true, onlyCudaRuntimeBinaries: true))
                {
                    return new LlamaCppUpdateResult
                    {
                        Success = false,
                        Message = "No CUDA runtime binaries were found in the downloaded package.",
                        UpdateInfo = check
                    };
                }

                progress?.Report(new LlamaCppUpdateProgress
                {
                    Stage = "complete",
                    Message = $"CUDA runtime binaries were refreshed from {runtimeRelease.TagName}.",
                    Percent = 100
                });

                await StaticLogger.LogAsync($"[LlamaCpp][Update] Successfully refreshed CUDA runtime binaries in '{check.InstallDirectory}'.");

                return new LlamaCppUpdateResult
                {
                    Success = true,
                    Message = $"CUDA runtime binaries were refreshed from {runtimeRelease.TagName}. This is usually not necessary unless your local CUDA DLLs are outdated or broken.",
                    UpdateInfo = check
                };
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex, "[LlamaCpp][Update] CUDA runtime re-download failed");
                return new LlamaCppUpdateResult
                {
                    Success = false,
                    Message = $"CUDA runtime binary refresh failed: {ex.Message}",
                    UpdateInfo = check
                };
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static HttpClient CreateGitHubHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SharpestLlmStudio", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        private static async Task<List<GitHubReleaseInfo>> FetchGitHubReleasesAsync(HttpClient client, CancellationToken cancellationToken)
        {
            using var response = await client.GetAsync(LlamaCppGitHubReleasesApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var releases = new List<GitHubReleaseInfo>();

            foreach (JsonElement releaseElement in document.RootElement.EnumerateArray())
            {
                bool draft = releaseElement.TryGetProperty("draft", out JsonElement draftElement) && draftElement.ValueKind == JsonValueKind.True;
                bool prerelease = releaseElement.TryGetProperty("prerelease", out JsonElement prereleaseElement) && prereleaseElement.ValueKind == JsonValueKind.True;
                if (draft || prerelease)
                {
                    continue;
                }

                string tagName = releaseElement.TryGetProperty("tag_name", out JsonElement tagElement)
                    ? tagElement.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(tagName))
                {
                    continue;
                }

                DateTime? publishedAtUtc = releaseElement.TryGetProperty("published_at", out JsonElement publishedElement)
                    && publishedElement.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(publishedElement.GetString(), out DateTime parsedPublished)
                    ? parsedPublished.ToUniversalTime()
                    : null;

                string body = releaseElement.TryGetProperty("body", out JsonElement bodyElement)
                    && bodyElement.ValueKind == JsonValueKind.String
                    ? bodyElement.GetString() ?? string.Empty
                    : string.Empty;

                var assets = new List<GitHubAssetInfo>();
                if (releaseElement.TryGetProperty("assets", out JsonElement assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement assetElement in assetsElement.EnumerateArray())
                    {
                        string name = assetElement.TryGetProperty("name", out JsonElement nameElement)
                            ? nameElement.GetString() ?? string.Empty
                            : string.Empty;
                        string downloadUrl = assetElement.TryGetProperty("browser_download_url", out JsonElement urlElement)
                            ? urlElement.GetString() ?? string.Empty
                            : string.Empty;
                        long size = assetElement.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize)
                            ? parsedSize
                            : 0L;

                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(downloadUrl))
                        {
                            assets.Add(new GitHubAssetInfo(name, downloadUrl, size));
                        }
                    }
                }

                releases.Add(new GitHubReleaseInfo(tagName, publishedAtUtc, body, assets));
            }

            return releases;
        }

        private static GitHubAssetInfo? SelectBestWindowsBinaryAsset(IEnumerable<GitHubAssetInfo> assets, string backend, string backendHint)
            => SelectBestWindowsAsset(assets, backend, backendHint, preferCudaRuntimeAssets: false);

        private static GitHubAssetInfo? SelectBestWindowsCudaRuntimeAsset(IEnumerable<GitHubAssetInfo> assets, string backend, string backendHint)
        {
            if (!string.Equals((backend ?? string.Empty).Trim(), "cuda", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return SelectBestWindowsAsset(assets, backend, backendHint, preferCudaRuntimeAssets: true);
        }

        private static GitHubAssetInfo? SelectBestWindowsAsset(IEnumerable<GitHubAssetInfo> assets, string backend, string backendHint, bool preferCudaRuntimeAssets)
        {
            GitHubAssetInfo? best = null;
            int bestScore = int.MinValue;

            foreach (GitHubAssetInfo asset in assets)
            {
                int score = ScoreWindowsAsset(asset.Name, backend, backendHint, preferCudaRuntimeAssets);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = asset;
                }
            }

            return bestScore > 0 ? best : null;
        }

        private static int ScoreWindowsAsset(string assetName, string backend, string backendHint, bool preferCudaRuntimeAssets)
        {
            string name = assetName.Trim().ToLowerInvariant();
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return int.MinValue;
            }

            bool isCudaRuntimeAsset = IsCudaRuntimeAssetName(name);
            if (preferCudaRuntimeAssets)
            {
                if (!isCudaRuntimeAsset)
                {
                    return int.MinValue;
                }
            }
            else if (isCudaRuntimeAsset)
            {
                return int.MinValue;
            }

            int score = 0;
            if (name.Contains("win", StringComparison.Ordinal)) score += 60;
            if (name.Contains("x64", StringComparison.Ordinal) || name.Contains("amd64", StringComparison.Ordinal)) score += 35;
            if (name.Contains("bin", StringComparison.Ordinal)) score += 20;
            if (name.Contains("llama", StringComparison.Ordinal)) score += 15;
            if (name.Contains("arm64", StringComparison.Ordinal)) score -= 120;
            if (name.Contains("text", StringComparison.Ordinal) || name.Contains("source", StringComparison.Ordinal)) score -= 100;
            if (isCudaRuntimeAsset) score += 35;

            bool containsCuda = name.Contains("cuda", StringComparison.Ordinal);
            bool containsVulkan = name.Contains("vulkan", StringComparison.Ordinal);
            bool containsHip = name.Contains("hip", StringComparison.Ordinal) || name.Contains("rocm", StringComparison.Ordinal);
            bool containsMetal = name.Contains("metal", StringComparison.Ordinal);
            bool containsSycl = name.Contains("sycl", StringComparison.Ordinal);
            bool containsOpenCl = name.Contains("opencl", StringComparison.Ordinal);

            switch ((backend ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "cuda":
                    score += containsCuda ? 130 : -120;
                    score += !string.IsNullOrWhiteSpace(backendHint) && name.Contains(backendHint, StringComparison.Ordinal) ? 35 : 0;
                    score += containsVulkan || containsHip || containsMetal || containsSycl || containsOpenCl ? -90 : 0;
                    break;

                case "vulkan":
                    score += containsVulkan ? 130 : -120;
                    score += containsCuda || containsHip || containsMetal || containsSycl || containsOpenCl ? -90 : 0;
                    break;

                case "hip":
                    score += containsHip ? 130 : -120;
                    score += containsCuda || containsVulkan || containsMetal || containsSycl || containsOpenCl ? -90 : 0;
                    break;

                default:
                    score += containsCuda || containsVulkan || containsHip || containsMetal || containsSycl || containsOpenCl ? -80 : 90;
                    break;
            }

            return score;
        }

        private static List<string> GetInstallFileNames(string executablePath, string installDirectory)
        {
            var fileNames = new List<string>
            {
                Path.GetFileName(executablePath).ToLowerInvariant()
            };

            if (Directory.Exists(installDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(installDirectory, "*.dll", SearchOption.TopDirectoryOnly).Take(200))
                {
                    fileNames.Add(Path.GetFileName(file).ToLowerInvariant());
                }

                foreach (string file in Directory.EnumerateFiles(installDirectory, "*.exe", SearchOption.TopDirectoryOnly).Take(50))
                {
                    fileNames.Add(Path.GetFileName(file).ToLowerInvariant());
                }
            }

            return fileNames;
        }

        private static (string Backend, string BackendHint) DetectInstalledBackend(IEnumerable<string> fileNames)
        {
            List<string> names = fileNames.ToList();
            string cudaHint = DetectCudaHint(names);
            if (!string.IsNullOrWhiteSpace(cudaHint)
                || names.Any(name => name.StartsWith("ggml-cuda", StringComparison.OrdinalIgnoreCase)))
            {
                return ("cuda", cudaHint);
            }

            if (names.Any(name => name.StartsWith("ggml-vulkan", StringComparison.OrdinalIgnoreCase))
                || names.Any(name => name.Contains("vulkan", StringComparison.OrdinalIgnoreCase)))
            {
                return ("vulkan", string.Empty);
            }

            if (names.Any(name => name.StartsWith("ggml-hip", StringComparison.OrdinalIgnoreCase))
                || names.Any(name => name.Contains("rocm", StringComparison.OrdinalIgnoreCase))
                || names.Any(name => name.Contains("hip", StringComparison.OrdinalIgnoreCase)))
            {
                return ("hip", string.Empty);
            }

            return ("cpu", string.Empty);
        }

        private static string DetectCudaHint(IEnumerable<string> fileNames)
        {
            int bestCudaMajor = -1;

            foreach (string fileName in fileNames)
            {
                string name = fileName.Trim();

                Match runtimeMatch = Regex.Match(name, @"^(?:cublas64|cublaslt64|cudart64)_(?<major>\d+)\.dll$", RegexOptions.IgnoreCase);
                if (runtimeMatch.Success && int.TryParse(runtimeMatch.Groups["major"].Value, out int runtimeMajor))
                {
                    bestCudaMajor = Math.Max(bestCudaMajor, runtimeMajor);
                    continue;
                }

                Match assetHintMatch = Regex.Match(name, @"cu(?<major>\d{2})(?:\.\d+)?", RegexOptions.IgnoreCase);
                if (assetHintMatch.Success && int.TryParse(assetHintMatch.Groups["major"].Value, out int assetMajor))
                {
                    bestCudaMajor = Math.Max(bestCudaMajor, assetMajor);
                }
            }

            return bestCudaMajor >= 0 ? $"cu{bestCudaMajor}" : string.Empty;
        }

        private static bool TryDetectCudaRuntimeBinaries(IEnumerable<string> fileNames, out string summary)
        {
            List<string> matches = fileNames
                .Where(IsCudaRuntimeDependencyFileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            summary = matches.Count > 0
                ? $"Detected CUDA runtime DLLs: {string.Join(", ", matches)}"
                : string.Empty;

            return matches.Count > 0;
        }

        private static bool IsNewerBuildAvailable(LlamaCppUpdateCheckResult check)
        {
            string buildToken = ExtractBuildToken(check.LatestTag, check.MatchedAssetName);
            if (!string.IsNullOrWhiteSpace(buildToken)
                && !string.IsNullOrWhiteSpace(check.InstalledVersion)
                && check.InstalledVersion.Contains(buildToken, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (check.InstalledFileDateUtc.HasValue && check.LatestPublishedAtUtc.HasValue)
            {
                return check.LatestPublishedAtUtc.Value > check.InstalledFileDateUtc.Value.AddHours(6);
            }

            return true;
        }

        private static string ExtractBuildToken(string latestTag, string assetName)
        {
            foreach (string source in new[] { latestTag, assetName })
            {
                Match match = Regex.Match(source ?? string.Empty, @"b\d{3,}", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Value;
                }
            }

            return string.Empty;
        }

        private static async Task DownloadFileWithProgressAsync(HttpClient client, string url, string destinationPath, IProgress<LlamaCppUpdateProgress>? progress, CancellationToken cancellationToken)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);

            byte[] buffer = new byte[1024 * 1024];
            long totalRead = 0;
            int lastPercent = -1;
            long lastReportedBytes = 0;
            long reportStepBytes = totalBytes.HasValue && totalBytes.Value > 0
                ? Math.Max(1024L * 1024L, totalBytes.Value / 40L)
                : 2L * 1024L * 1024L;
            long lastReportTick = Environment.TickCount64;
            int read;
            while ((read = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                int percent = totalBytes.HasValue && totalBytes.Value > 0
                    ? Math.Clamp((int)Math.Round((totalRead / (double)totalBytes.Value) * 70.0), 0, 70)
                    : 0;

                long nowTick = Environment.TickCount64;
                bool shouldReport = progress != null
                    && (lastPercent < 0
                        || totalRead == totalBytes
                        || percent >= lastPercent + 1
                        || totalRead - lastReportedBytes >= reportStepBytes
                        || nowTick - lastReportTick >= 250);

                if (!shouldReport)
                {
                    continue;
                }

                lastPercent = percent;
                lastReportedBytes = totalRead;
                lastReportTick = nowTick;

                progress.Report(new LlamaCppUpdateProgress
                {
                    Stage = "download",
                    Message = totalBytes.HasValue
                        ? $"Downloading {FormatBytes(totalRead)} of {FormatBytes(totalBytes.Value)}..."
                        : $"Downloading {FormatBytes(totalRead)}...",
                    Percent = percent,
                    BytesReceived = totalRead,
                    TotalBytes = totalBytes
                });
            }
        }

        private static string? FindExtractedBinaryDirectory(string extractRoot)
        {
            string[] candidates = Directory.GetFiles(extractRoot, "llama-server.exe", SearchOption.AllDirectories);
            if (candidates.Length == 0)
            {
                return null;
            }

            return candidates
                .Select(path => Path.GetDirectoryName(path) ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar))
                .FirstOrDefault();
        }

        private static string? FindExtractedCudaRuntimeDirectory(string extractRoot)
        {
            string[] candidates = Directory.GetFiles(extractRoot, "*.dll", SearchOption.AllDirectories)
                .Where(path => IsCudaRuntimeDependencyFileName(Path.GetFileName(path)))
                .ToArray();
            if (candidates.Length == 0)
            {
                return null;
            }

            return candidates
                .Select(path => Path.GetDirectoryName(path) ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar))
                .FirstOrDefault();
        }

        private static bool CopyUpdateFilesWithRollback(string sourceDirectory, string targetDirectory, string backupDirectory, IProgress<LlamaCppUpdateProgress>? progress, CancellationToken cancellationToken, bool includeCudaRuntimeBinaries, bool onlyCudaRuntimeBinaries)
        {
            string[] files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                .Where(file => ShouldCopyUpdateFile(Path.GetRelativePath(sourceDirectory, file), includeCudaRuntimeBinaries, onlyCudaRuntimeBinaries))
                .ToArray();
            var states = new List<CopiedFileState>(files.Length);

            if (files.Length == 0)
            {
                return false;
            }

            try
            {
                int totalFiles = Math.Max(1, files.Length);
                for (int i = 0; i < files.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string sourceFile = files[i];
                    string relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
                    string targetFile = Path.Combine(targetDirectory, relativePath);
                    string backupFile = Path.Combine(backupDirectory, relativePath);
                    bool hadOriginal = File.Exists(targetFile);

                    if (hadOriginal)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backupFile) ?? backupDirectory);
                        File.Copy(targetFile, backupFile, overwrite: true);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile) ?? targetDirectory);
                    File.Copy(sourceFile, targetFile, overwrite: true);
                    states.Add(new CopiedFileState(targetFile, backupFile, hadOriginal));

                    int percent = 80 + Math.Clamp((int)Math.Round(((i + 1) / (double)totalFiles) * 20.0), 0, 20);
                    progress?.Report(new LlamaCppUpdateProgress
                    {
                        Stage = "install",
                        Message = $"Installing file {i + 1} of {totalFiles}: {relativePath}",
                        Percent = percent
                    });
                }
            }
            catch
            {
                for (int i = states.Count - 1; i >= 0; i--)
                {
                    CopiedFileState state = states[i];
                    try
                    {
                        if (state.HadOriginal)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(state.TargetPath) ?? targetDirectory);
                            File.Copy(state.BackupPath, state.TargetPath, overwrite: true);
                        }
                        else if (File.Exists(state.TargetPath))
                        {
                            File.Delete(state.TargetPath);
                        }
                    }
                    catch
                    {
                    }
                }

                throw;
            }

            return true;
        }

        private static bool ShouldCopyUpdateFile(string relativePath, bool includeCudaRuntimeBinaries, bool onlyCudaRuntimeBinaries)
        {
            string fileName = Path.GetFileName(relativePath);
            bool isCudaRuntimeFile = IsCudaRuntimeDependencyFileName(fileName);

            if (onlyCudaRuntimeBinaries)
            {
                return isCudaRuntimeFile;
            }

            if (!includeCudaRuntimeBinaries && isCudaRuntimeFile)
            {
                return false;
            }

            return true;
        }

        private static bool IsCudaRuntimeDependencyFileName(string fileName)
        {
            string name = fileName.Trim();
            return Regex.IsMatch(name, @"^(?:cublas64|cublaslt64|cudart64)_\d+\.dll$", RegexOptions.IgnoreCase);
        }

        private static bool IsCudaRuntimeAssetName(string assetName)
        {
            string name = assetName.Trim();
            return name.StartsWith("cudart-", StringComparison.OrdinalIgnoreCase)
                || name.Contains("-cudart-", StringComparison.OrdinalIgnoreCase)
                || name.Contains("cudart-llama", StringComparison.OrdinalIgnoreCase);
        }

        private static List<int> GetRunningProcessIdsUsingExecutable(string executablePath)
        {
            var processIds = new List<int>();
            string normalizedExecutablePath = Path.GetFullPath(executablePath);
            string processName = Path.GetFileNameWithoutExtension(executablePath);

            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? mainModulePath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(mainModulePath)
                        && string.Equals(Path.GetFullPath(mainModulePath), normalizedExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add(process.Id);
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            return processIds;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static string FormatUtcDate(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "unknown";
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return $"{value:0.##} {units[unitIndex]}";
        }

        private sealed record GitHubReleaseInfo(string TagName, DateTime? PublishedAtUtc, string Body, List<GitHubAssetInfo> Assets);
        private sealed record GitHubAssetInfo(string Name, string DownloadUrl, long Size);
        private sealed record CopiedFileState(string TargetPath, string BackupPath, bool HadOriginal);
    }
}
