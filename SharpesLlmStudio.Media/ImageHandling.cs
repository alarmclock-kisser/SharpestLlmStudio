using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using System.Text.Json;
using SharpestLlmStudio.Shared;
using System.Runtime.Versioning;

namespace SharpesLlmStudio.Media
{
    public class ImageHandling
    {


        [SupportedOSPlatform("windows")]
        public static async Task<string?> DrawJsonRectanglesOnImageFileAsync(string imageFilePath, JsonDocument jsonDocument, string rectanglesColorHex = "#FF0000", int borderThickness = 2)
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

                using var pen = new Pen(ColorTranslator.FromHtml(rectanglesColorHex), borderThickness);

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
                    }
                    else if (element.TryGetProperty("point_2d", out var point) && point.GetArrayLength() == 2)
                    {
                        int x = (int)Math.Round(point[0].GetDouble() * imageWidth / 1000.0);
                        int y = (int)Math.Round(point[1].GetDouble() * imageHeight / 1000.0);
                        graphics.DrawEllipse(pen, x - borderThickness, y - borderThickness, borderThickness * 2, borderThickness * 2);
                    }
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
