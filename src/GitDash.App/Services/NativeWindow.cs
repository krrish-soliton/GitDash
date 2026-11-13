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
}
