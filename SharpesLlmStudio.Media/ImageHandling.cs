using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using System.IO;
using System.Drawing.Text;
using System.Text.Json;
using SharpestLlmStudio.Shared;
using System.Runtime.Versioning;

namespace SharpesLlmStudio.Media
{
    public class ImageHandling
    {


        [SupportedOSPlatform("windows")]
        public static async Task<string?> DrawJsonRectanglesOnImageFileAsync(string imageFilePath, JsonDocument jsonDocument, string rectanglesColorHex = "#FF0000", int borderThickness = 2, bool renderLabels = false)
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

                Font? font = null;
                SolidBrush? textBrush = null;
                SolidBrush? bgBrush = null;
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

                    foreach (var element in jsonDocument.RootElement.EnumerateArray())
                    {
                        if (element.TryGetProperty("bbox_2d", out var bbox) && bbox.GetArrayLength() == 4)
                        {
                            int x1 = (int)Math.Round(bbox[0].GetDouble() * imageWidth / 1000.0);
                            int y1 = (int)Math.Round(bbox[1].GetDouble() * imageHeight / 1000.0);
                            int x2 = (int)Math.Round(bbox[2].GetDouble() * imageWidth / 1000.0);
                            int y2 = (int)Math.Round(bbox[3].GetDouble() * imageHeight / 1000.0);

                            int rectWidth = x2 - x1;
                            int rectHeight = y2 - y1;

                            graphics.DrawRectangle(pen, x1, y1, rectWidth, rectHeight);

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
                        else if (element.TryGetProperty("point_2d", out var point) && point.GetArrayLength() == 2)
                        {
                            int x = (int)Math.Round(point[0].GetDouble() * imageWidth / 1000.0);
                            int y = (int)Math.Round(point[1].GetDouble() * imageHeight / 1000.0);
                            graphics.DrawEllipse(pen, x - borderThickness, y - borderThickness, borderThickness * 2, borderThickness * 2);

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


    }
}
