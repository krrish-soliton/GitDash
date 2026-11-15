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
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern byte MsgSendRespondsTo(nint receiver, nint selector, nint arg);
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
    private const long CanJoinAllSpaces = 1 << 0, Stationary = 1 << 4, IgnoresCycle = 1 << 6, FullScreenAuxiliary = 1 << 8, CanJoinAllApplications = 1 << 18;
    // Full-screen apps cover everything below the screen-saver level (status-bar level 25 is not enough).
    private const long StatusWindowLevel = 1000;

    /// <summary>
    /// Shows an overlay on every desktop (Space) and above full-screen apps, and keeps it out of Cmd+Tab and Mission Control.
    /// Does nothing on other systems.
    /// </summary>
    public static void ShowOnAllDesktops(Window window)
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return;
            MsgSendLong(handle, sel_registerName("setCollectionBehavior:"), (nint)(CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary | CanJoinAllApplications));
            MsgSendLong(handle, sel_registerName("setLevel:"), (nint)StatusWindowLevel);
            MsgSendBool(handle, sel_registerName("setHidesOnDeactivate:"), false);
            MakeNonActivating(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // The overlay stays on the desktop it was opened on.
        }
    }

    private const long NonactivatingPanelStyle = 1 << 7;

    /// <summary>
    /// Makes the window behave like a non-activating panel (macOS): showing or clicking it never activates GitDash, which is
    /// what lets such windows sit on top of other apps' full-screen Spaces. Avalonia's windows are plain NSWindows, so the
    /// panel style bit is set on the window and, where AppKit has it, its internal "prevents activation" switch is turned on.
    /// </summary>
    private static void MakeNonActivating(nint handle)
    {
        var style = MsgSendHandle(handle, sel_registerName("styleMask"));
        MsgSendLong(handle, sel_registerName("setStyleMask:"), style | (nint)NonactivatingPanelStyle);
        var preventsActivation = sel_registerName("_setPreventsActivation:");
        if (MsgSendRespondsTo(handle, sel_registerName("respondsToSelector:"), preventsActivation) != 0)
            MsgSendBool(handle, preventsActivation, true);
    }

    /// <summary>What macOS currently says about the window and the app (for the diagnostics log). Empty off macOS.</summary>
    public static string Describe(Window window)
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return string.Empty;
            var app = MsgSendHandle(GetClass("NSApplication"), sel_registerName("sharedApplication"));
            var policy = app == 0 ? -1 : MsgSendHandle(app, sel_registerName("activationPolicy"));
            var behavior = MsgSendHandle(handle, sel_registerName("collectionBehavior"));
            var level = MsgSendHandle(handle, sel_registerName("level"));
            var style = MsgSendHandle(handle, sel_registerName("styleMask"));
            return $"policy={policy} collectionBehavior={behavior} level={level} styleMask={style}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return "describe failed: " + ex.GetType().Name;
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
    public static bool SetDockIconVisible(bool visible)
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) return false;
            var app = MsgSendHandle(GetClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app == 0) return false;
            var wanted = visible ? 0 : 1; // regular : accessory
            // Asking first keeps repeated calls harmless (no focus stealing) and tells whether the framework changed it back.
            if (MsgSendHandle(app, sel_registerName("activationPolicy")) == wanted) return false;
            MsgSendLong(app, sel_registerName("setActivationPolicy:"), (nint)wanted);
            if (visible) MsgSendBool(app, sel_registerName("activateIgnoringOtherApps:"), true);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // The Dock icon stays as it is.
            return false;
        }
    }

    /// <summary>
    /// Takes the window off the screen and puts it straight back in front, without activating the app (macOS). The window
    /// server decides which Spaces a window belongs to when it is ordered in, so a window shown while the app still had a
    /// Dock icon has to be ordered in again after the app became an "accessory" before it follows full-screen apps.
    /// </summary>
    public static void OrderAgain(Window window)
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return;
            MsgSendLong(handle, sel_registerName("orderOut:"), 0);
            MsgSendHandle(handle, sel_registerName("orderFrontRegardless"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // The window stays where it is.
        }
    }
}
