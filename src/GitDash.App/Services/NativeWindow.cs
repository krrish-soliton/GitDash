using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace GitDash.App.Services;

/// <summary>
/// The few things Avalonia cannot do for a floating overlay: let mouse clicks fall through to the windows underneath,
/// and read the global cursor position (a click-through window receives no pointer events, so hover is polled).
/// Both are best effort; on unsupported platforms the widget simply stays clickable.
/// </summary>
internal static class NativeWindow
{
    // ----- Windows -----
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExAppWindow = 0x40000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int command);

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    // ----- macOS -----
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern void MsgSendBool(nint receiver, nint selector, bool value);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern nint sel_registerName(string name);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern nint CGEventCreate(nint source);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern CgPoint CGEventGetLocation(nint ev);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint cf);

    [StructLayout(LayoutKind.Sequential)] private struct CgPoint { public double X, Y; }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")] private static extern nint GetClass(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern nint MsgSendHandle(nint receiver, nint selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern CgPoint MsgSendPoint(nint receiver, nint selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern void MsgSendLong(nint receiver, nint selector, nint value);

    /// <summary>
    /// Keeps an overlay out of the taskbar and the Alt+Tab list (a "tool window" that never takes focus).
    /// Windows only needs this; macOS windows never appear in Cmd+Tab on their own.
    /// </summary>
    public static void HideFromSwitcher(Window window)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return;

            // The shell only re-reads the style of a hidden window, so hide, change, show again without activating.
            ShowWindow(handle, 0);
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            style = (style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;
            SetWindowLongPtr(handle, GwlExStyle, (nint)style);
            ShowWindow(handle, 4);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // Nothing else to do; the window just stays in the list.
        }
    }

    /// <summary>Makes the window ignore the mouse (<paramref name="on"/>) or accept it again.</summary>
    public static void SetClickThrough(Window window, bool on)
    {
        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return;

            if (OperatingSystem.IsWindows())
            {
                var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
                style |= WsExLayered | WsExToolWindow | WsExNoActivate;
                style = on ? style | WsExTransparent : style & ~WsExTransparent;
                SetWindowLongPtr(handle, GwlExStyle, (nint)style);
            }
            else if (OperatingSystem.IsMacOS())
            {
                MsgSendBool(handle, sel_registerName("setIgnoresMouseEvents:"), on);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // Overlay stays interactive; nothing else to do.
        }
    }

    /// <summary>Global cursor position in device pixels, or null when the platform cannot tell.</summary>
    public static PixelPoint? CursorPosition(double scaling)
    {
        try
        {
            if (OperatingSystem.IsWindows() && GetCursorPos(out var p)) return new PixelPoint(p.X, p.Y);

            if (OperatingSystem.IsMacOS())
            {
                var ev = CGEventCreate(0);
                if (ev == 0) return null;
                var at = CGEventGetLocation(ev);
                CFRelease(ev);
                return new PixelPoint((int)(at.X * scaling), (int)(at.Y * scaling));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return null;
        }

        return null;
    }

    // ----- macOS only -----
    private const long CanJoinAllSpaces = 1 << 0, Stationary = 1 << 4, IgnoresCycle = 1 << 6, FullScreenAuxiliary = 1 << 8;
    private const long StatusWindowLevel = 25;

    /// <summary>
    /// Shows an overlay on every desktop (Space) and above full-screen apps, and keeps it out of Cmd+Tab and Mission Control.
    /// Does nothing on other systems.
    /// </summary>
    public static void ShowOnAllDesktops(Window window)
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return;
            MsgSendLong(handle, sel_registerName("setCollectionBehavior:"), (nint)(CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary));
            MsgSendLong(handle, sel_registerName("setLevel:"), (nint)StatusWindowLevel);
            MsgSendBool(handle, sel_registerName("setHidesOnDeactivate:"), false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // The overlay stays on the desktop it was opened on.
        }
    }

    /// <summary>
    /// Where the cursor is, in the window's own units from its top-left corner (macOS). Asking the window itself avoids any
    /// conversion between screen coordinate systems, and it works while the window ignores the mouse. Null when unknown.
    /// </summary>
    public static Avalonia.Point? CursorInWindow(Window window)
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return null;
            var at = MsgSendPoint(handle, sel_registerName("mouseLocationOutsideOfEventStream")); // origin bottom-left, in points
            var height = double.IsNaN(window.Height) ? window.Bounds.Height : window.Height;
            return new Avalonia.Point(at.X, height - at.Y);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return null;
        }
    }

    /// <summary>
    /// Shows or hides the Dock icon (and Cmd+Tab entry) of the whole app (macOS): "accessory" apps live only in the menu bar.
    /// Coming back brings the app to the front so the window that was just opened is not left behind other apps.
    /// </summary>
    public static void SetDockIconVisible(bool visible)
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) return;
            var app = MsgSendHandle(GetClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app == 0) return;
            MsgSendLong(app, sel_registerName("setActivationPolicy:"), (nint)(visible ? 0 : 1)); // regular : accessory
            if (visible) MsgSendBool(app, sel_registerName("activateIgnoringOtherApps:"), true);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // The Dock icon stays as it is.
        }
    }
}
