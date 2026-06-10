//
//  OverlayRenderTest.cs
//  Clicky for Windows
//
//  Renders the overlay's visual elements (triangle cursor, speech bubble,
//  waveform, spinner) to a PNG so their actual pixels can be eyeballed
//  without relying on screen capture — the live overlay sets
//  WDA_EXCLUDEFROMCAPTURE, which hides it from screenshots by design.
//
//    Clicky.exe --rendertest   →   %TEMP%\clicky-overlay-render.png
//

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;

namespace Clicky;

public static class OverlayRenderTest
{
    public static int Run()
    {
        string outputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clicky-overlay-render.png");
        int exitCode = 0;

        var thread = new Thread(() =>
        {
            try
            {
                var canvas = BuildShowcaseCanvas();

                // A dark backdrop so the blue/white elements read clearly,
                // approximating a real desktop behind the buddy.
                var root = new Border
                {
                    Width = 360,
                    Height = 220,
                    Background = new SolidColorBrush(Color.FromRgb(24, 26, 27)),
                    Child = canvas,
                };

                root.Measure(new Size(360, 220));
                root.Arrange(new Rect(0, 0, 360, 220));
                root.UpdateLayout();

                var bitmap = new RenderTargetBitmap(360, 220, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var fileStream = File.Create(outputPath);
                encoder.Save(fileStream);

                Console.WriteLine($"rendered overlay showcase to {outputPath}");
            }
            catch (Exception renderError)
            {
                Console.WriteLine($"render test failed: {renderError}");
                File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clicky-rendertest-error.txt"), renderError.ToString());
                exitCode = 1;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static Canvas BuildShowcaseCanvas()
    {
        var canvas = new Canvas { Width = 360, Height = 220 };

        // ── Triangle cursor + welcome bubble (top-left) ──────────────
        var triangle = MakeTriangle();
        Canvas.SetLeft(triangle, 40);
        Canvas.SetTop(triangle, 40);
        canvas.Children.Add(triangle);

        var welcomeBubble = MakeBubble("hey! i'm clicky");
        Canvas.SetLeft(welcomeBubble, 60);
        Canvas.SetTop(welcomeBubble, 36);
        canvas.Children.Add(welcomeBubble);

        // ── Waveform (mid-left) ──────────────────────────────────────
        var waveform = MakeWaveform();
        Canvas.SetLeft(waveform, 44);
        Canvas.SetTop(waveform, 110);
        canvas.Children.Add(waveform);
        canvas.Children.Add(MakeCaption("listening", 70, 108));

        // ── Spinner (mid-left, lower) ────────────────────────────────
        var spinner = MakeSpinner();
        Canvas.SetLeft(spinner, 44);
        Canvas.SetTop(spinner, 150);
        canvas.Children.Add(spinner);
        canvas.Children.Add(MakeCaption("processing", 70, 152));

        // ── Pointing bubble (right) ──────────────────────────────────
        var pointingTriangle = MakeTriangle();
        var rotate = new RotateTransform(20, 8, 8);
        pointingTriangle.RenderTransform = rotate;
        Canvas.SetLeft(pointingTriangle, 210);
        Canvas.SetTop(pointingTriangle, 120);
        canvas.Children.Add(pointingTriangle);

        var pointingBubble = MakeBubble("right here!");
        Canvas.SetLeft(pointingBubble, 230);
        Canvas.SetTop(pointingBubble, 116);
        canvas.Children.Add(pointingBubble);

        return canvas;
    }

    private static ShapePath MakeTriangle()
    {
        const double size = 16;
        double height = size * Math.Sqrt(3.0) / 2.0;
        double midX = size / 2.0, midY = size / 2.0;

        var figure = new PathFigure { StartPoint = new Point(midX, midY - height / 1.5), IsClosed = true };
        figure.Segments.Add(new LineSegment(new Point(midX - size / 2, midY + height / 3), true));
        figure.Segments.Add(new LineSegment(new Point(midX + size / 2, midY + height / 3), true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new ShapePath
        {
            Data = geometry,
            Fill = new SolidColorBrush(DS.Colors.OverlayCursorBlue),
            Width = 16,
            Height = 16,
            Effect = new DropShadowEffect { Color = DS.Colors.OverlayCursorBlue, ShadowDepth = 0, BlurRadius = 8 },
            RenderTransform = new RotateTransform(-35, 8, 8),
        };
    }

    private static Border MakeBubble(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(DS.Colors.OverlayCursorBlue),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Effect = new DropShadowEffect { Color = DS.Colors.OverlayCursorBlue, ShadowDepth = 0, BlurRadius = 6, Opacity = 0.5 },
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
            },
        };
    }

    private static Canvas MakeWaveform()
    {
        var canvas = new Canvas
        {
            Width = 18,
            Height = 16,
            Effect = new DropShadowEffect { Color = DS.Colors.OverlayCursorBlue, ShadowDepth = 0, BlurRadius = 6, Opacity = 0.6 },
        };
        double[] heights = { 5, 9, 13, 9, 5 };
        for (int i = 0; i < 5; i++)
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = heights[i],
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(DS.Colors.OverlayCursorBlue),
            };
            Canvas.SetLeft(bar, i * 4);
            Canvas.SetTop(bar, (16 - heights[i]) / 2);
            canvas.Children.Add(bar);
        }
        return canvas;
    }

    private static Canvas MakeSpinner()
    {
        var canvas = new Canvas { Width = 14, Height = 14 };
        const double radius = 5.75, cx = 7, cy = 7;
        const int segments = 24;
        for (int i = 0; i < segments; i++)
        {
            double fa = 0.15 + 0.7 * i / segments;
            double fb = 0.15 + 0.7 * (i + 1) / segments;
            double aa = fa * 2 * Math.PI - Math.PI / 2;
            double ab = fb * 2 * Math.PI - Math.PI / 2;
            canvas.Children.Add(new Line
            {
                X1 = cx + radius * Math.Cos(aa),
                Y1 = cy + radius * Math.Sin(aa),
                X2 = cx + radius * Math.Cos(ab),
                Y2 = cy + radius * Math.Sin(ab),
                StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = new SolidColorBrush(DS.Colors.OverlayCursorBlue) { Opacity = (double)(i + 1) / segments },
            });
        }
        return canvas;
    }

    private static TextBlock MakeCaption(string text, double left, double top)
    {
        var caption = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
            FontFamily = new FontFamily("Segoe UI"),
        };
        Canvas.SetLeft(caption, left);
        Canvas.SetTop(caption, top);
        return caption;
    }
}
