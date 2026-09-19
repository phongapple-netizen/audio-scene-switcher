using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingColor = System.Drawing.Color;
using DrawingLinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;
using DrawingPen = System.Drawing.Pen;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingSolidBrush = System.Drawing.SolidBrush;

namespace AudioPanel.Services;

/// <summary>
/// Keeps the window and tray icon visually consistent without shipping a
/// third-party image asset. The mark combines a waveform with a circular
/// switching arrow so it remains legible at the 16px tray size.
/// </summary>
public static class AppIconFactory
{
    private const int CanvasSize = 64;

    public static Icon CreateTrayIcon()
    {
        using var bitmap = CreateBitmap(CanvasSize);
        var handle = bitmap.GetHicon();
        try
        {
            using var source = Icon.FromHandle(handle);
            return (Icon)source.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static ImageSource CreateWindowIcon()
    {
        using var icon = CreateTrayIcon();
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));
        source.Freeze();
        return source;
    }

    private static Bitmap CreateBitmap(int size)
    {
        var bitmap = new Bitmap(size, size, DrawingPixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var scale = size / (float)CanvasSize;
        graphics.ScaleTransform(scale, scale);
        graphics.Clear(DrawingColor.Transparent);

        using var backgroundPath = RoundedRectangle(2, 2, 60, 60, 15);
        using var background = new DrawingLinearGradientBrush(
            new RectangleF(2, 2, 60, 60),
            DrawingColor.FromArgb(255, 24, 42, 70),
            DrawingColor.FromArgb(255, 39, 67, 112),
            135f);
        graphics.FillPath(background, backgroundPath);

        using var borderPen = new DrawingPen(DrawingColor.FromArgb(150, 126, 190, 255), 1.5f);
        graphics.DrawPath(borderPen, backgroundPath);

        using var ringPen = new DrawingPen(DrawingColor.FromArgb(255, 111, 201, 255), 3.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(ringPen, new RectangleF(12, 12, 39, 39), 42, 278);

        using var wavePen = new DrawingPen(DrawingColor.White, 4.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(wavePen, 22, 27, 22, 37);
        graphics.DrawLine(wavePen, 31, 21, 31, 43);
        graphics.DrawLine(wavePen, 40, 25, 40, 39);

        using var arrowBrush = new DrawingSolidBrush(DrawingColor.FromArgb(255, 111, 201, 255));
        var arrow = new PointF[]
        {
            new(51, 14),
            new(58, 17),
            new(52, 22)
        };
        graphics.FillPolygon(arrowBrush, arrow);

        return bitmap;
    }

    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
