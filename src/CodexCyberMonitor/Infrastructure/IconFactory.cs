using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CodexCyberMonitor.Infrastructure;

internal static class IconFactory
{
    public static Icon Create(bool alert)
    {
        return alert
            ? CreateColored(Color.FromArgb(196, 43, 28), Color.FromArgb(114, 18, 13))
            : CreateColored(Color.FromArgb(16, 124, 16), Color.FromArgb(7, 78, 7));
    }

    public static Icon CreateError()
    {
        return CreateColored(Color.FromArgb(255, 140, 0), Color.FromArgb(153, 84, 0));
    }

    private static Icon CreateColored(Color shieldColor, Color outlineColor)
    {
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var points = new[]
        {
            new PointF(32, 3),
            new PointF(56, 12),
            new PointF(53, 38),
            new PointF(44, 52),
            new PointF(32, 61),
            new PointF(20, 52),
            new PointF(11, 38),
            new PointF(8, 12)
        };

        using var fill = new SolidBrush(shieldColor);
        using var outline = new Pen(outlineColor, 3);
        graphics.FillPolygon(fill, points);
        graphics.DrawPolygon(outline, points);

        using var white = new SolidBrush(Color.White);
        graphics.FillRectangle(white, 28, 16, 8, 25);
        graphics.FillEllipse(white, 28, 46, 8, 8);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
