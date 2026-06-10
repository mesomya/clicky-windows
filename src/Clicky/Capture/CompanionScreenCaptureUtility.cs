//
//  CompanionScreenCaptureUtility.cs
//  Clicky for Windows
//
//  Multi-monitor screenshot capture for the companion voice flow — the
//  Windows equivalent of the original's ScreenCaptureKit utility. Uses GDI
//  (Graphics.CopyFromScreen) which needs no user permission on Windows.
//
//  Clicky's own overlay and panel windows carry WDA_EXCLUDEFROMCAPTURE, so
//  they never appear in these captures — same effect as the original
//  excluding its own windows from the SCContentFilter.
//

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Clicky.Native;

namespace Clicky.Capture;

public record CompanionScreenCapture(
    byte[] ImageData,
    string Label,
    bool IsCursorScreen,
    Rectangle DisplayBoundsInPixels,
    int ScreenshotWidthInPixels,
    int ScreenshotHeightInPixels);

public static class CompanionScreenCaptureUtility
{
    /// Captures all connected displays as JPEG data, labeling each with
    /// whether the user's cursor is on that screen. The cursor screen is
    /// always first so screen numbering matches what the AI is told.
    public static List<CompanionScreenCapture> CaptureAllScreensAsJpeg()
    {
        NativeMethods.GetCursorPos(out var cursorPosition);
        var cursorPoint = new Point(cursorPosition.X, cursorPosition.Y);

        // Sort displays so the cursor screen is always first — its index
        // becomes "screen 1", matching the labels sent to the AI.
        var sortedScreens = Screen.AllScreens
            .OrderByDescending(screen => screen.Bounds.Contains(cursorPoint))
            .ToList();

        if (sortedScreens.Count == 0)
        {
            throw new InvalidOperationException("No display available for capture");
        }

        var capturedScreens = new List<CompanionScreenCapture>();

        for (int displayIndex = 0; displayIndex < sortedScreens.Count; displayIndex++)
        {
            var screen = sortedScreens[displayIndex];
            var bounds = screen.Bounds;
            bool isCursorScreen = bounds.Contains(cursorPoint);

            // Capture at native resolution, then downscale so the longest
            // edge is at most 1280px — same budget the original uses, keeping
            // the payload small while leaving UI text readable for the AI.
            const int maxDimension = 1280;
            double scaleFactor = Math.Min(1.0, (double)maxDimension / Math.Max(bounds.Width, bounds.Height));
            int screenshotWidth = Math.Max(1, (int)Math.Round(bounds.Width * scaleFactor));
            int screenshotHeight = Math.Max(1, (int)Math.Round(bounds.Height * scaleFactor));

            using var fullResolutionBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(fullResolutionBitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            using var scaledBitmap = new Bitmap(screenshotWidth, screenshotHeight, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(scaledBitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(fullResolutionBitmap, 0, 0, screenshotWidth, screenshotHeight);
            }

            byte[] jpegData = EncodeAsJpeg(scaledBitmap, quality: 80);

            string screenLabel;
            if (sortedScreens.Count == 1)
            {
                screenLabel = "user's screen (cursor is here)";
            }
            else if (isCursorScreen)
            {
                screenLabel = $"screen {displayIndex + 1} of {sortedScreens.Count} — cursor is on this screen (primary focus)";
            }
            else
            {
                screenLabel = $"screen {displayIndex + 1} of {sortedScreens.Count} — secondary screen";
            }

            capturedScreens.Add(new CompanionScreenCapture(
                ImageData: jpegData,
                Label: screenLabel,
                IsCursorScreen: isCursorScreen,
                DisplayBoundsInPixels: bounds,
                ScreenshotWidthInPixels: screenshotWidth,
                ScreenshotHeightInPixels: screenshotHeight));
        }

        return capturedScreens;
    }

    private static byte[] EncodeAsJpeg(Bitmap bitmap, long quality)
    {
        var jpegEncoder = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);

        using var outputStream = new MemoryStream();
        bitmap.Save(outputStream, jpegEncoder, encoderParameters);
        return outputStream.ToArray();
    }
}
