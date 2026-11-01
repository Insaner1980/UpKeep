using System.Runtime.InteropServices;
using System.Security.Principal;

namespace UpKeep_App.Platform;

internal sealed partial class SingleInstanceService : IDisposable
{
    private readonly Mutex mutex;
    public static readonly uint ActivateMessage = RegisterWindowMessage("UpKeep.Activate." + WindowsIdentity.GetCurrent().User?.Value);
    public bool IsPrimary
    {
        get;
    }
    public SingleInstanceService()
    {
        mutex = new Mutex(true, "Local\\UpKeep." + WindowsIdentity.GetCurrent().User?.Value, out var created);
        IsPrimary = created;
    }
    public static void ActivateExisting() => PostMessage(new nint(0xffff), ActivateMessage, 0, 0);
    public void Dispose()
    {
        if (IsPrimary)
        {
            mutex.ReleaseMutex();
        }

        mutex.Dispose();
    }
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "RegisterWindowMessageW")] private static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
}
