using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using GitDash.Presentation.ViewModels;

namespace GitDash.App.Services;

/// <summary>
/// Shows the number of PRs assigned to me on the application's own taskbar icon: an overlay badge on the Windows
/// taskbar button and the badge label on the macOS Dock tile. Best effort; failures are swallowed.
/// </summary>
public sealed class TaskbarBadge : IDisposable
{
    private readonly Window _window;
    private readonly WidgetViewModel _counts;
    private nint _icon;

    public TaskbarBadge(Window window, WidgetViewModel counts)
    {
        _window = window;
        _counts = counts;
    }

    public void Start()
    {
        _counts.PropertyChanged += OnChanged;
        // The taskbar button only exists while the window is shown, and Windows forgets the overlay when it is recreated.
        _window.Opened += (_, _) => Apply();
        _window.Activated += (_, _) => Apply();
        Apply();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WidgetViewModel.Assigned) or null or "") Dispatcher.UIThread.Post(Apply);
    }

    private void Apply()
    {
        var count = _counts.Assigned;
        try
        {
            if (OperatingSystem.IsWindows()) ApplyWindows(count);
            else if (OperatingSystem.IsMacOS()) ApplyMac(count);
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException
                                       or InvalidCastException or SEHException or InvalidComObjectException)
        {
            // No badge on this system; the counts are still in the window, the widget and the tray.
        }
    }

    // ----- Windows: ITaskbarList3.SetOverlayIcon -----

    [SupportedOSPlatform("windows")]
    private void ApplyWindows(int count)
    {
        if (_window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;

        var next = count > 0 ? CreateIcon(BadgePixels.Render(count)) : 0;
        var taskbar = (ITaskbarList3)new TaskbarList();
        taskbar.HrInit();
        taskbar.SetOverlayIcon(hwnd, next, count > 0 ? $"{count} assigned to me" : string.Empty);
        Marshal.ReleaseComObject(taskbar);

        if (_icon != 0) DestroyIcon(_icon);
        _icon = next;
    }

    private static nint CreateIcon(byte[] bgra, int size = 32)
    {
        var colour = nint.Zero;
        var mask = nint.Zero;
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            colour = CreateBitmap(size, size, 1, 32, handle.AddrOfPinnedObject());
            mask = CreateBitmap(size, size, 1, 1, nint.Zero);
            var info = new IconInfo { IsIcon = true, Mask = mask, Colour = colour };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            handle.Free();
            if (colour != 0) DeleteObject(colour);
            if (mask != 0) DeleteObject(mask);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX, HotspotY;
        public nint Mask, Colour;
    }

    [DllImport("user32.dll")] private static extern nint CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32.dll")] private static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, nint bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList { }

    // Only SetOverlayIcon is used, but the vtable order has to be complete and exact.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, int flags);
        void RegisterTab(nint hwndTab, nint hwndMdi);
        void UnregisterTab(nint hwndTab);
        void SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        void SetTabActive(nint hwndTab, nint hwndMdi, uint reserved);
        void ThumbBarAddButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarSetImageList(nint hwnd, nint imageList);
        void SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        void SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        void SetThumbnailClip(nint hwnd, nint clip);
    }

    // ----- macOS: NSApp.dockTile.badgeLabel -----

    private static void ApplyMac(int count)
    {
        var app = MsgSend(GetClass("NSApplication"), Selector("sharedApplication"));
        var tile = MsgSend(app, Selector("dockTile"));
        var label = count > 0
            ? MsgSendUtf8(GetClass("NSString"), Selector("stringWithUTF8String:"), BadgePixels.Label(count))
            : nint.Zero;
        MsgSendPtr(tile, Selector("setBadgeLabel:"), label);
    }

    private static nint Selector(string name) => sel_registerName(name);
    private static nint GetClass(string name) => objc_getClass(name);

    [DllImport("/usr/lib/libobjc.dylib")] private static extern nint objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern nint sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern nint MsgSend(nint receiver, nint selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern nint MsgSendPtr(nint receiver, nint selector, nint argument);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern nint MsgSendUtf8(nint receiver, nint selector, string argument);

    public void Dispose()
    {
        _counts.PropertyChanged -= OnChanged;
        try
        {
            if (OperatingSystem.IsWindows() && _icon != 0)
            {
                if (_window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != 0)
                {
                    var taskbar = (ITaskbarList3)new TaskbarList();
                    taskbar.HrInit();
                    taskbar.SetOverlayIcon(hwnd, 0, string.Empty);
                    Marshal.ReleaseComObject(taskbar);
                }

                DestroyIcon(_icon);
                _icon = 0;
            }
            else if (OperatingSystem.IsMacOS()) ApplyMac(0);
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException
                                       or InvalidCastException or SEHException or InvalidComObjectException)
        {
        }
    }
}
