namespace UpKeep.Core.Platform;

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public static class PanelPlacement
{
    public static PixelRect Calculate(PixelRect work, PixelRect? tray, uint dpi)
    {
        if (work.Width < 1 || work.Height < 1 || dpi == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(work));
        }

        var scale = dpi / 96d;
        var gap = (int)Math.Round(8 * scale);
        var width = Math.Min(work.Width, (int)Math.Round(420 * scale));
        var height = Math.Min(work.Height, (int)Math.Round(566 * scale));
        var x = (tray?.X + tray?.Width ?? work.X + work.Width) - width - gap;
        var y = (tray?.Y ?? work.Y + work.Height) - height - gap;
        return new(Math.Clamp(x, work.X, work.X + work.Width - width),
            Math.Clamp(y, work.Y, work.Y + work.Height - height), width, height);
    }
}
