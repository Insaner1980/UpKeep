using UpKeep_App.Platform;

namespace UpKeep.Tests;

public class TrayNotificationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneMouseClickTogglesOnlyOnce(bool version4)
    {
        bool visible = false;
        foreach (uint notification in new uint[] { 0x0201, 0x0202, 0x0400 })
        {
            if (TrayNotification.IsActivation(notification, version4))
            {
                visible = !visible;
            }
        }

        Assert.True(visible);
    }

    [Fact]
    public void KeyboardSelectionOpensWithoutTreatingHoverOrRightClickAsActivation()
    {
        Assert.True(TrayNotification.IsActivation(0x0401, true));
        foreach (uint notification in new uint[] { 0x0200, 0x007b, 0x0205, 0x0406, 0x0407 })
        {
            Assert.False(TrayNotification.IsActivation(notification, true));
        }
    }
}
