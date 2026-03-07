using System;
using System.Collections.Generic;
using System.Text; 
using System.Drawing;
using System.Collections.Concurrent;
using SharpestLlmStudio.Shared;
using System.Runtime.Versioning;
using System.Drawing.Imaging;

namespace SharpestLlmStudio.Runtime
{
    public partial class LlamaCppClient
    {
        [SupportedOSPlatform("windows")]
        internal async Task<string[]> LoadAndSerializeImagesAsync(string[] imageFiles, int? maxWidth = null, int? maxHeight = null, string format = "jpg")
        {
            var images = await this.LoadImagesFromFilesAsync(imageFiles, maxWidth, maxHeight);
            var toSerialize = images;
            await StaticLogger.LogAsync($"Loaded {images.Length} images from {imageFiles.Length} files. Starting processing and serialization...");

            // If maxWidth is 0 or null, do not downsize (send full-size images)
            if (maxWidth.HasValue && maxWidth.Value > 0)
            {
                toSerialize = await this.DownSizeImagesAsync(images, maxWidth.Value, maxHeight);
            }

            var base64Strings = await this.SerializeImagesToBase64Async(toSerialize, format);
            
            // Dispose all loaded images
            foreach (var img in images)
            {
                try { img.Dispose(); } catch { }
            }
            return base64Strings;
        }


        [SupportedOSPlatform("windows")]
        internal async Task<Image[]> LoadImagesFromFilesAsync(string[] imageFiles, int? downsizeMaxWidth = null, int? downsizeMaxHeight = null)
        {
            imageFiles = imageFiles.Where(f => File.Exists(f)).ToArray();

            ConcurrentBag<Image> images = new ConcurrentBag<Image>();

            // Determine a safe upper bound for image token usage based on current context size.
            // Reserve a safety margin for generation and conversation tokens (1024) and ensure at least 128 tokens available for images.
            int maxImageTokens = Math.Max(128, this.CurrentContextSize - 1024);
            int totalEstimatedTokens = 0;
            object tokenLock = new object();

            // Define async load tasks. For multi-frame inputs (e.g. TIFF) load every frame and add each frame as a separate Image.
            // Stop adding frames once the estimated token usage for images would exceed the computed safe bound.
            var loadTasks = imageFiles.Select(file => Task.Run(async () =>
            {
                try
                {
                    using var image = Image.FromFile(file);
                    string fileName = Path.GetFileName(file);

                    // Determine frame dimension (if any) and frame count
                    int frameCount = 1;
                    if (image.FrameDimensionsList != null && image.FrameDimensionsList.Length > 0)
                    {
                        var dimension = new FrameDimension(image.FrameDimensionsList[0]);
                        frameCount = image.GetFrameCount(dimension);
                    }

                    if (frameCount > 1)
                    {
                        for (int f = 0; f < frameCount; f++)
                        {
                            try
                            {
                                var dimension = new FrameDimension(image.FrameDimensionsList![0]!);
                                image.SelectActiveFrame(dimension, f);

                                // Estimate tokens for this frame based on the post-downsize dimensions (if downsize is configured).
                                var (estW, estH) = SimulateDownsizeDimensions(image.Width, image.Height, downsizeMaxWidth, downsizeMaxHeight);
                                int perFrameTokens = EstimateTokensForImageDimensions(estW, estH);
                                bool accept;
                                lock (tokenLock)
                                {
                                    if (totalEstimatedTokens + perFrameTokens > maxImageTokens)
                                    {
                                        accept = false;
                                    }
                                    else
                                    {
                                        totalEstimatedTokens += perFrameTokens;
                                        accept = true;
                                    }
                                }

                                if (!accept)
                                {
                                    await StaticLogger.LogAsync($"Skipping frame {f + 1}/{frameCount} of {fileName} to avoid exceeding image token budget ({totalEstimatedTokens}/{maxImageTokens}).");
                                    break;
                                }

                                images.Add((Image)image.Clone());
                                await StaticLogger.LogAsync($"Loaded frame {f + 1}/{frameCount} of multi-frame image file {fileName} with dimensions {image.Width}x{image.Height} and added to the list (est. {perFrameTokens} tokens)");
                            }
                            catch (Exception exFrame)
                            {
                                await StaticLogger.LogAsync(exFrame, $"Error loading frame {f} from file: {file}");
                            }
                        }
                    }
                    else
                    {
                        images.Add((Image)image.Clone());
                        await StaticLogger.LogAsync($"Loaded image file {fileName} with dimensions {image.Width}x{image.Height} and added to the list");
                    }
                }
                catch (Exception ex)
                {
                    await StaticLogger.LogAsync(ex, $"Error loading image file: {file}");
                }
            }));

            await Task.WhenAll(loadTasks);
            return images.ToArray();
        }

        [SupportedOSPlatform("windows")]
        internal async Task<Image[]> DownSizeImagesAsync(Image[] images, int maxWidth = 720, int? maxHeight = null)
        {
            maxHeight ??= maxWidth;

            // Downsize every image (keep aspect ratio) to fit within maxWidth and maxHeight.
            // If image is already within bounds, leave it untouched.
            var tasks = new List<Task>(images.Length);

            for (int i = 0; i < images.Length; i++)
            {
                int idx = i;
                var img = images[idx];
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (img == null)
                        {
                            return;
                        }

                        int width = img.Width;
                        int height = img.Height;

                        if (width <= 0 || height <= 0)
                        {
                            return;
                        }

                        // Determine scale factors only when exceeding limits
                        double scaleW = width > maxWidth ? (double)maxWidth / width : 1.0;
                        double scaleH = height > maxHeight ? (double)maxHeight.Value / height : 1.0;

                        double scale = Math.Min(scaleW, scaleH);

                        if (scale < 1.0)
                        {
                            await StaticLogger.LogAsync($"Downsizing image at index {idx} from {width}x{height} to fit within {maxWidth}x{maxHeight} with scale factor {scale:F5}");
                            int newWidth = Math.Max(1, (int)Math.Round(width * scale));
                            int newHeight = Math.Max(1, (int)Math.Round(height * scale));

                            var resized = new Bitmap(newWidth, newHeight);
                            using (var g = System.Drawing.Graphics.FromImage(resized))
                            {
                                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                                g.DrawImage(img, 0, 0, newWidth, newHeight);
                            }

                            // Replace array element and dispose old image
                            lock (images)
                            {
                                images[idx] = resized;
                            }
                            try { img.Dispose(); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        await StaticLogger.LogAsync(ex, $"Error down sizing image at index: {idx}");
                    }
                }));
            }

            await Task.WhenAll(tasks);

            return images;
        }

        [SupportedOSPlatform("windows")]
        internal async Task<string[]> SerializeImagesToBase64Async(Image[] images, string format = "jpg")
        {
            var base64Strings = new string[images.Length];
            var imageFormat = format.ToLower() switch
            {
                "png" => ImageFormat.Png,
                "bmp" => ImageFormat.Bmp,
                "gif" => ImageFormat.Gif,
                _ => ImageFormat.Jpeg,
            };

            var tasks = images.Select(async (image, index) =>
            {
                using var ms = new MemoryStream();
                image.Save(ms, imageFormat);
                string base64 = Convert.ToBase64String(ms.ToArray());
                base64Strings[index] = base64;
                await StaticLogger.LogAsync($"Serialized image at index {index} to base64 string of length {base64.Length} with format {format}");
            });

            await Task.WhenAll(tasks);
            return base64Strings;
        }

        /// <summary>
        /// Simulates what the final image dimensions would be after downsizing, without actually resizing.
        /// Returns original dimensions when no downsize limits are configured or the image already fits.
        /// </summary>
        private static (int Width, int Height) SimulateDownsizeDimensions(int width, int height, int? maxWidth, int? maxHeight)
        {
            if (width <= 0 || height <= 0)
            {
                return (width, height);
            }

            // No downsize configured — return original
            if (!maxWidth.HasValue || maxWidth.Value <= 0)
            {
                return (width, height);
            }

            int mw = maxWidth.Value;
            int mh = maxHeight.HasValue && maxHeight.Value > 0 ? maxHeight.Value : mw;

            double scaleW = width > mw ? (double)mw / width : 1.0;
            double scaleH = height > mh ? (double)mh / height : 1.0;
            double scale = Math.Min(scaleW, scaleH);

            if (scale >= 1.0)
            {
                return (width, height);
            }

            return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }
    }
}
