using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using System.IO;
using System.Drawing.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpestLlmStudio.Shared;
using System.Runtime.Versioning;

namespace SharpesLlmStudio.Media
{
    public class ImageHandling
    {


        private static readonly Regex RawPoint2dRegex = new(@"point_2d[""']?\s*:\s*\[\s*(?<x>-?\d+(?:[.,]\d+)?)\s*,\s*(?<y>-?\d+(?:[.,]\d+)?)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RawBbox2dRegex = new(@"bbox_2d[""']?\s*:\s*\[\s*(?<x1>-?\d+(?:[.,]\d+)?)\s*,\s*(?<y1>-?\d+(?:[.,]\d+)?)\s*,\s*(?<x2>-?\d+(?:[.,]\d+)?)\s*,\s*(?<y2>-?\d+(?:[.,]\d+)?)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [SupportedOSPlatform("windows")]
        public static async Task<string?> DrawJsonRectanglesOnImageFileAsync(string imageFilePath, JsonDocument? jsonDocument, string rawText = "", string rectanglesColorHex = "#FF0000", int borderThickness = 2, bool renderLabels = false)
        {
            if (!File.Exists(imageFilePath))
            {
                await StaticLogger.LogAsync($"Image file not found: {imageFilePath}");
                return null;
            }

            try
            {
                using var image = new Bitmap(imageFilePath);
                int imageWidth = image.Width;
                int imageHeight = image.Height;

                using var graphics = Graphics.FromImage(image);
                using var pen = new Pen(ColorTranslator.FromHtml(rectanglesColorHex), Math.Max(1, borderThickness));
                using var fillBrush = new SolidBrush(ColorTranslator.FromHtml(rectanglesColorHex));

                Font? font = null;
                SolidBrush? textBrush = null;
                SolidBrush? bgBrush = null;
                int renderedShapeCount = 0;
                try
                {
                    if (renderLabels)
                    {
                        // Font size proportional to border thickness but clamped to reasonable range
                        float fontSize = Math.Clamp(borderThickness * 6f, 10f, 48f);
                        try
                        {
                            font = new Font("Arial", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                        }
                        catch
                        {
                            font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                        }

                        textBrush = new SolidBrush(Color.White);
                        bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    }

                    IEnumerable<JsonElement> drawableElements = jsonDocument != null
                        ? EnumerateDrawableElements(jsonDocument.RootElement)
                        : Array.Empty<JsonElement>();

                    foreach (JsonElement element in drawableElements)
                    {
                        if (element.TryGetProperty("bbox_2d", out var bbox)
                            && bbox.ValueKind == JsonValueKind.Array
                            && bbox.GetArrayLength() == 4
                            && TryConvertBoxToPixels(bbox, imageWidth, imageHeight, out int x1, out int y1, out int x2, out int y2))
                        {
                            int rectWidth = Math.Max(1, x2 - x1);
                            int rectHeight = Math.Max(1, y2 - y1);

                            graphics.DrawRectangle(pen, x1, y1, rectWidth, rectHeight);
                            renderedShapeCount++;

                            if (renderLabels)
                            {
                                string? label = null;
                                if (element.TryGetProperty("label", out var p) && p.ValueKind == JsonValueKind.String)
                                    label = p.GetString();
                                else if (element.TryGetProperty("name", out p) && p.ValueKind == JsonValueKind.String)
                                    label = p.GetString();
                                else if (element.TryGetProperty("id", out p) && p.ValueKind == JsonValueKind.String)
                                    label = p.GetString();

                                if (!string.IsNullOrWhiteSpace(label) && font != null && textBrush != null && bgBrush != null)
                                {
                                    var textSize = graphics.MeasureString(label, font);
                                    float px = x1;
                                    float py = y1 - textSize.Height - 4f;
                                    // if not enough space above, place inside rectangle at top
                                    if (py < 0)
                                        py = y1 + 2f;

                                    // ensure within bounds horizontally
                                    if (px + textSize.Width + 4f > imageWidth)
                                        px = Math.Max(2f, imageWidth - textSize.Width - 4f);

                                    var bgRect = new RectangleF(px - 2f, py - 2f, textSize.Width + 4f, textSize.Height + 4f);
                                    graphics.FillRectangle(bgBrush, bgRect);
                                    graphics.DrawString(label, font, textBrush, new PointF(px, py));
                                }
                            }
                        }
                        else if (element.TryGetProperty("point_2d", out var point)
                            && point.ValueKind == JsonValueKind.Array
                            && point.GetArrayLength() == 2
                            && TryConvertPointToPixels(point, imageWidth, imageHeight, out int x, out int y))
                        {
                            int markerRadius = Math.Max(8, borderThickness * 3);
                            int crosshairRadius = Math.Max(12, borderThickness * 4);

                            graphics.FillEllipse(fillBrush, x - markerRadius, y - markerRadius, markerRadius * 2, markerRadius * 2);
                            graphics.DrawEllipse(pen, x - markerRadius, y - markerRadius, markerRadius * 2, markerRadius * 2);
                            graphics.DrawLine(pen, x - crosshairRadius, y, x + crosshairRadius, y);
                            graphics.DrawLine(pen, x, y - crosshairRadius, x, y + crosshairRadius);
                            renderedShapeCount++;

                            if (renderLabels)
                            {
                                string? label = null;
                                if (element.TryGetProperty("label", out var p) && p.ValueKind == JsonValueKind.String)
                                    label = p.GetString();
                                else if (element.TryGetProperty("name", out p) && p.ValueKind == JsonValueKind.String)
                                    label = p.GetString();

                                if (!string.IsNullOrWhiteSpace(label) && font != null && textBrush != null && bgBrush != null)
                                {
                                    var textSize = graphics.MeasureString(label, font);
                                    float px = x + borderThickness + 4f;
                                    float py = y - textSize.Height / 2f;
                                    if (px + textSize.Width + 4f > imageWidth) px = Math.Max(2f, imageWidth - textSize.Width - 4f);
                                    if (py < 0) py = 2f;

                                    var bgRect = new RectangleF(px - 2f, py - 2f, textSize.Width + 4f, textSize.Height + 4f);
                                    graphics.FillRectangle(bgBrush, bgRect);
                                    graphics.DrawString(label, font, textBrush, new PointF(px, py));
                                }
                            }
                        }
                    }

                    // Regex fallback: scan raw text for point_2d / bbox_2d when JSON gave nothing
                    if (renderedShapeCount == 0 && !string.IsNullOrWhiteSpace(rawText))
                    {
                        foreach (Match m in RawBbox2dRegex.Matches(rawText))
                        {
                            if (double.TryParse(m.Groups["x1"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rx1)
                                && double.TryParse(m.Groups["y1"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ry1)
                                && double.TryParse(m.Groups["x2"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rx2)
                                && double.TryParse(m.Groups["y2"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ry2))
                            {
                                var (px1, py1) = ConvertPointToPixels(rx1, ry1, imageWidth, imageHeight);
                                var (px2, py2) = ConvertPointToPixels(rx2, ry2, imageWidth, imageHeight);
                                if (px2 < px1) (px1, px2) = (px2, px1);
                                if (py2 < py1) (py1, py2) = (py2, py1);
                                graphics.DrawRectangle(pen, px1, py1, Math.Max(1, px2 - px1), Math.Max(1, py2 - py1));
                                renderedShapeCount++;
                            }
                        }

                        foreach (Match m in RawPoint2dRegex.Matches(rawText))
                        {
                            if (double.TryParse(m.Groups["x"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rx)
                                && double.TryParse(m.Groups["y"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ry))
                            {
                                var (px, py) = ConvertPointToPixels(rx, ry, imageWidth, imageHeight);
                                int markerRadius = Math.Max(8, borderThickness * 3);
                                int crosshairRadius = Math.Max(12, borderThickness * 4);
                                graphics.FillEllipse(fillBrush, px - markerRadius, py - markerRadius, markerRadius * 2, markerRadius * 2);
                                graphics.DrawEllipse(pen, px - markerRadius, py - markerRadius, markerRadius * 2, markerRadius * 2);
                                graphics.DrawLine(pen, px - crosshairRadius, py, px + crosshairRadius, py);
                                graphics.DrawLine(pen, px, py - crosshairRadius, px, py + crosshairRadius);
                                renderedShapeCount++;
                            }
                        }
                    }

                    if (renderedShapeCount == 0)
                    {
                        await StaticLogger.LogAsync("No supported JSON points or boxes were rendered on the image.");
                        return null;
                    }
                }
                finally
                {
                    font?.Dispose();
                    textBrush?.Dispose();
                    bgBrush?.Dispose();
                }

                using var ms = new MemoryStream();
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Error processing image '{imageFilePath}': {ex.Message}");
                await StaticLogger.LogAsync(ex);
                return null;
            }
        }

        private static IEnumerable<JsonElement> EnumerateDrawableElements(JsonElement root)
        {
            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    if (root.TryGetProperty("bbox_2d", out _) || root.TryGetProperty("point_2d", out _))
                    {
                        yield return root;
                    }

                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        if (property.NameEquals("bbox_2d") || property.NameEquals("point_2d"))
                        {
                            continue;
                        }

                        if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            foreach (JsonElement child in EnumerateDrawableElements(property.Value))
                            {
                                yield return child;
                            }
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement item in root.EnumerateArray())
                    {
                        foreach (JsonElement child in EnumerateDrawableElements(item))
                        {
                            yield return child;
                        }
                    }
                    break;
            }
        }

        private static bool TryConvertPointToPixels(JsonElement point, int imageWidth, int imageHeight, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (!TryGetArrayDouble(point, 0, out double rawX) || !TryGetArrayDouble(point, 1, out double rawY))
            {
                return false;
            }

            (x, y) = ConvertPointToPixels(rawX, rawY, imageWidth, imageHeight);
            return true;
        }

        private static bool TryConvertBoxToPixels(JsonElement bbox, int imageWidth, int imageHeight, out int x1, out int y1, out int x2, out int y2)
        {
            x1 = 0;
            y1 = 0;
            x2 = 0;
            y2 = 0;
            if (!TryGetArrayDouble(bbox, 0, out double rawX1)
                || !TryGetArrayDouble(bbox, 1, out double rawY1)
                || !TryGetArrayDouble(bbox, 2, out double rawX2)
                || !TryGetArrayDouble(bbox, 3, out double rawY2))
            {
                return false;
            }

            (x1, y1) = ConvertPointToPixels(rawX1, rawY1, imageWidth, imageHeight);
            (x2, y2) = ConvertPointToPixels(rawX2, rawY2, imageWidth, imageHeight);

            if (x2 < x1)
            {
                (x1, x2) = (x2, x1);
            }

            if (y2 < y1)
            {
                (y1, y2) = (y2, y1);
            }

            return true;
        }

        private static bool TryGetArrayDouble(JsonElement array, int index, out double value)
        {
            value = 0;
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() <= index)
            {
                return false;
            }

            JsonElement item = array[index];
            if (item.ValueKind == JsonValueKind.Number)
            {
                return item.TryGetDouble(out value);
            }

            if (item.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(item.GetString(), out value);
            }

            return false;
        }

        private static (int X, int Y) ConvertPointToPixels(double rawX, double rawY, int imageWidth, int imageHeight)
        {
            double x = rawX * imageWidth / 1000.0;
            double y = rawY * imageHeight / 1000.0;

            int px = Math.Clamp((int)Math.Round(x), 0, Math.Max(0, imageWidth - 1));
            int py = Math.Clamp((int)Math.Round(y), 0, Math.Max(0, imageHeight - 1));
            return (px, py);
        }


    }
}
