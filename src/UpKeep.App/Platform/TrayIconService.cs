using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using UpKeep.Core.Platform;
using Windows.Graphics;

namespace UpKeep_App.Platform;

internal sealed partial class TrayIconService : IDisposable
{
    private const uint Callback = 0x8001;
    private readonly Window window;
    private readonly nint hwnd;
    private readonly Action toggle;
    private readonly Action exit;
    private readonly SubclassProc callback;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData icon;
    private bool version4;

    public TrayIconService(Window window, Action toggle, Action exit)
    {
        this.window = window;
        this.toggle = toggle;
        this.exit = exit;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        callback = WindowProc;
        if (!SetWindowSubclass(hwnd, callback, 1, 0))
        {
            throw new Win32Exception();
        }

        icon = new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = hwnd,
            Id = 1,
            Flags = 1 | 2 | 4,
            CallbackMessage = Callback,
            Icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), 1, 32, 32, 0x10),
        };
        const string tooltip = "UpKeep";
        for (int index = 0; index < tooltip.Length; index++)
        {
            icon.Tip[index] = tooltip[index];
        }

        AddIcon();
    }
    private void AddIcon()
    {
        if (!Shell_NotifyIcon(0, ref icon))
        {
            return;
        }

        icon.Version = 4;
        version4 = Shell_NotifyIcon(4, ref icon);
    }
    private nint WindowProc(nint h, uint message, nuint w, nint l, nuint id, nuint data)
    {
        if (message == taskbarCreated)
        {
            AddIcon();
        }

        if (message == SingleInstanceService.ActivateMessage)
        {
            ShowPanel();
            return 0;
        }
        if (message is 0x007e or 0x02e0)
        {
            window.DispatcherQueue.TryEnqueue(PlacePanel);
        }

        if (message == Callback)
        {
            var code = (uint)l & 0xffff;
            if (TrayNotification.IsActivation(code, version4))
            {
                toggle();
            }

            if (version4 ? code == 0x007b : code == 0x0205)
            {
                ShowContextMenu();
            }
            return 0;
        }
        return DefSubclassProc(h, message, w, l);
    }
    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, "Open UpKeep");
            AppendMenu(menu, 0, 2, "Exit");
            GetCursorPos(out var point);
            SetForegroundWindow(hwnd);
            var selected = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, hwnd, 0);
            if (selected == 1)
            {
                ShowPanel();
            }

            if (selected == 2)
            {
                exit();
            }
        }
        finally { DestroyMenu(menu); }
    }
    public void ShowPanel()
    {
        PlacePanel();
        window.AppWindow.Show();
        SetForegroundWindow(hwnd);
        window.Activate();
    }
    public void PlacePanel()
    {
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = hwnd, Id = 1 };
        nint monitor;
        if (Shell_NotifyIconGetRect(ref identifier, out var rect) == 0)
        {
            monitor = MonitorFromRect(ref rect, 2);
        }
        else
        {
            monitor = MonitorFromWindow(hwnd, 2);
        }

        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            throw new Win32Exception();
        }

        uint dpi = GetDpiForWindow(hwnd);
        if (GetDpiForMonitor(monitor, 0, out var monitorDpi, out _) == 0)
        {
            dpi = monitorDpi;
        }

        var work = info.Work;
        // Calendar-like panels align to the work area's right edge, including when the icon is in overflow.
        var anchor = new PixelRect(work.Right, work.Bottom, 0, 0);
        var placement = PanelPlacement.Calculate(new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), anchor, dpi);
        window.AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));
    }
    public void Dispose()
    {
        Shell_NotifyIcon(2, ref icon);
        RemoveWindowSubclass(hwnd, callback, 1);
        if (icon.Icon != 0)
        {
            DestroyIcon(icon.Icon);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size; public Rect Monitor, Work; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier
    {
        public uint Size; public nint Window; public uint Id; public Guid Guid;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        public TipBuffer Tip;
        public uint State, StateMask;
        public InfoBuffer Info;
        public uint Version;
        public TitleBuffer InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [System.Runtime.CompilerServices.InlineArray(128)]
    private struct TipBuffer
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1144", Justification = "Required backing element of the native inline array; accessed through compiler-generated indexing.")] private ushort element;
    }
    [System.Runtime.CompilerServices.InlineArray(256)]
    private struct InfoBuffer
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1144", Justification = "Required backing element of the native inline array; accessed through compiler-generated indexing.")] private ushort element;
    }
    [System.Runtime.CompilerServices.InlineArray(64)]
    private struct TitleBuffer
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1144", Justification = "Required backing element of the native inline array; accessed through compiler-generated indexing.")] private ushort element;
    }
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint data);
    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [LibraryImport("comctl32.dll")] private static partial nint DefSubclassProc(nint hwnd, uint msg, nuint w, nint l);
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [LibraryImport("shell32.dll")] private static partial int Shell_NotifyIconGetRect(ref IconIdentifier icon, out Rect rect);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "LoadImageW")] private static partial nint LoadImage(nint instance, string path, uint type, int x, int y, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "RegisterWindowMessageW")] private static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromRect(ref Rect rect, uint flags);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [LibraryImport("user32.dll")] private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);
}
