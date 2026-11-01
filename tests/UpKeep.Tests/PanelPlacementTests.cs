using UpKeep.Core.Platform;

namespace UpKeep.Tests;

public class PanelPlacementTests
{
    [Fact]
    public void PanelFitsMonitorWorkArea()
    {
        var panel = PanelPlacement.Calculate(new(0, 0, 1920, 1040), new(1800, 1040, 30, 40), 96);
        Assert.Equal(420, panel.Width);
        Assert.Equal(566, panel.Height);
        Assert.InRange(panel.X, 0, 1500);
        Assert.InRange(panel.Y, 0, 474);
    }

    [Fact]
    public void MixedDpiUsesCorrectPixelConversion()
    {
        var panel = PanelPlacement.Calculate(new(-2560, 0, 2560, 1400), null, 144);
        Assert.Equal(630, panel.Width);
        Assert.Equal(849, panel.Height);
        Assert.InRange(panel.X, -2560, -630);
    }

    [Fact]
    public void MissingTrayRectangleUsesFallback()
    {
        var panel = PanelPlacement.Calculate(new(0, 0, 1920, 1040), null, 96);
        Assert.Equal(new PixelRect(1492, 466, 420, 566), panel);
    }

    [Fact]
    public void ShortWorkAreaKeepsFooterVisible()
    {
        var panel = PanelPlacement.Calculate(new(0, 0, 600, 420), null, 192);
        Assert.InRange(panel.Height, 1, 420);
        Assert.InRange(panel.Width, 1, 600);
        Assert.InRange(panel.Y + panel.Height, 1, 420);
    }
}
