namespace UpKeep_App.Platform;

internal static class TrayNotification
{
    public static bool IsActivation(uint notification, bool version4) =>
        version4 ? notification is 0x0400 or 0x0401 : notification == 0x0202;
}
