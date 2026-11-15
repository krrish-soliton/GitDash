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
    [StructLayout(LayoutKind.Sequential)] private struct CgRect { public double X, Y, Width, Height; }

    [DllImport("/usr/lib/libobjc.dylib")] private static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern void objc_registerClassPair(nint cls);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern byte class_addMethod(nint cls, nint name, nint imp, string types);

    private delegate nint ForwardingTargetDelegate(nint self, nint cmd, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern nint MsgSendInitPanel(nint receiver, nint selector, CgRect rect, nint style, nint backing, byte defer);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern void MsgSendFrame(nint receiver, nint selector, CgRect rect, byte display);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern byte MsgSendByte(nint receiver, nint selector);
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
            if (OperatingSystem.IsMacOS()) handle = MacHandle(window, handle);

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
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } native || native == 0) return;
            var handle = MacHandle(window, native);
            MsgSendLong(handle, sel_registerName("setCollectionBehavior:"), (nint)(CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary | CanJoinAllApplications));
            MsgSendLong(handle, sel_registerName("setLevel:"), (nint)StatusWindowLevel);
            MsgSendBool(handle, sel_registerName("setHidesOnDeactivate:"), false);
            if (!_panels.ContainsKey(window)) MakeNonActivating(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // The overlay stays on the desktop it was opened on.
        }
    }

    private const long NonactivatingPanelStyle = 1 << 7;

    private sealed class PanelState
    {
        public nint Handle;
        public double X = double.NaN, Y, Width, Height;
        public bool Shown;
    }

    private static readonly Dictionary<Window, PanelState> _panels = new();
    private static readonly Dictionary<nint, nint> _standIns = new();
    private static readonly ForwardingTargetDelegate _forwardingTarget = ForwardingTarget; // kept alive for the native side
    private static nint _panelClass;

    /// <summary>Avalonia's views ask their window for things only its own window class has; hand those questions to the stand-in.</summary>
    private static nint ForwardingTarget(nint self, nint cmd, nint selector) => _standIns.TryGetValue(self, out var standIn) ? standIn : 0;

    /// <summary>An NSPanel subclass that passes every message it does not know to the Avalonia window it stands in for.</summary>
    private static nint PanelClass()
    {
        if (_panelClass != 0) return _panelClass;
        var cls = objc_allocateClassPair(GetClass("NSPanel"), "GitDashOverlayPanel", 0);
        if (cls == 0) return GetClass("GitDashOverlayPanel"); // already registered
        class_addMethod(cls, sel_registerName("forwardingTargetForSelector:"),
            Marshal.GetFunctionPointerForDelegate(_forwardingTarget), "@@::");
        objc_registerClassPair(cls);
        return _panelClass = cls;
    }

    /// <summary>When on, overlay windows opened afterwards are hosted in a native non-activating panel (macOS only).</summary>
    public static bool UsePanels { get; set; }

    /// <summary>The panel that hosts the window's content, or the window itself.</summary>
    private static nint MacHandle(Window window, nint native) => _panels.TryGetValue(window, out var panel) ? panel.Handle : native;

    public static bool HasPanel(Window window) => _panels.ContainsKey(window);

    /// <summary>
    /// Moves the window's content into a real NSPanel (macOS only). Full-screen apps only let non-activating panels, not
    /// ordinary windows, float over their Space. The Avalonia window stays as an empty, invisible stand-in that keeps
    /// the layout and position logic running; <see cref="SyncPanel"/> keeps the panel on top of it. Returns whether the
    /// panel is in use; on any failure the window is left exactly as it was.
    /// </summary>
    public static bool AttachPanel(Window window)
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || !UsePanels) return false;
            if (_panels.ContainsKey(window)) return true;
            if (Environment.GetEnvironmentVariable("GITDASH_NO_PANEL") == "1") return false;
            if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0) return false;

            var view = MsgSendHandle(handle, sel_registerName("contentView"));
            if (view == 0) return false;
            var width = double.IsNaN(window.Width) ? Math.Max(window.Bounds.Width, 1) : window.Width;
            var height = double.IsNaN(window.Height) ? Math.Max(window.Bounds.Height, 1) : window.Height;
            var panel = MsgSendInitPanel(MsgSendHandle(PanelClass(), sel_registerName("alloc")),
                sel_registerName("initWithContentRect:styleMask:backing:defer:"),
                new CgRect { X = 0, Y = 0, Width = width, Height = height }, (nint)NonactivatingPanelStyle, 2, 0);
            if (panel == 0) return false;

            _standIns[panel] = handle;
            MsgSendHandle(view, sel_registerName("retain"));
            var stand = MsgSendHandle(MsgSendHandle(GetClass("NSView"), sel_registerName("alloc")), sel_registerName("init"));
            MsgSendLong(handle, sel_registerName("setContentView:"), stand);
            MsgSendLong(panel, sel_registerName("setContentView:"), view);
            MsgSendHandle(view, sel_registerName("release"));

            MsgSendBool(panel, sel_registerName("setOpaque:"), false);
            MsgSendLong(panel, sel_registerName("setBackgroundColor:"), MsgSendHandle(GetClass("NSColor"), sel_registerName("clearColor")));
            MsgSendBool(panel, sel_registerName("setHasShadow:"), false);
            MsgSendBool(panel, sel_registerName("setFloatingPanel:"), true);
            MsgSendBool(panel, sel_registerName("setBecomesKeyOnlyIfNeeded:"), true);
            MsgSendBool(panel, sel_registerName("setReleasedWhenClosed:"), false);
            _panels[window] = new PanelState { Handle = panel };
            OverlayLog.Write($"panel attached to {window.GetType().Name}: {Describe(window)}");
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return false;
        }
    }

    /// <summary>Makes the panel follow the stand-in window's place, size and visibility. Cheap when nothing changed.</summary>
    public static void SyncPanel(Window window)
    {
        try
        {
            if (!_panels.TryGetValue(window, out var state)) return;
            var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
            var scale = screen is null || screen.Scaling <= 0 ? 1 : screen.Scaling;
            var screenHeight = screen?.Bounds.Height ?? 0;
            var width = double.IsNaN(window.Width) ? window.Bounds.Width : window.Width;
            var height = double.IsNaN(window.Height) ? window.Bounds.Height : window.Height;
            var x = window.Position.X / scale;
            var y = (screenHeight - window.Position.Y) / scale - height; // Cocoa counts up from the bottom of the main screen.
            if (width > 0 && height > 0 && (x != state.X || y != state.Y || width != state.Width || height != state.Height))
            {
                state.X = x; state.Y = y; state.Width = width; state.Height = height;
                MsgSendFrame(state.Handle, sel_registerName("setFrame:display:"), new CgRect { X = x, Y = y, Width = width, Height = height }, 1);
            }

            var wantShown = window.IsVisible;
            if (wantShown == state.Shown) return;
            state.Shown = wantShown;
            if (wantShown) MsgSendHandle(state.Handle, sel_registerName("orderFrontRegardless"));
            else MsgSendLong(state.Handle, sel_registerName("orderOut:"), 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // The panel stays where it was.
        }
    }

    /// <summary>Takes the panel off the screen when its window closes.</summary>
    public static void DetachPanel(Window window)
    {
        try
        {
            if (!_panels.Remove(window, out var state)) return;
            _standIns.Remove(state.Handle);
            MsgSendLong(state.Handle, sel_registerName("orderOut:"), 0);
            MsgSendLong(state.Handle, sel_registerName("setContentView:"), 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // Nothing more to release.
        }
    }

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
        var available = MsgSendRespondsTo(handle, sel_registerName("respondsToSelector:"), preventsActivation) != 0;
        if (available) MsgSendBool(handle, preventsActivation, true);
        OverlayLog.Write($"non-activating panel style: bit kept={(MsgSendHandle(handle, sel_registerName("styleMask")) & NonactivatingPanelStyle) != 0}, " +
                         $"_setPreventsActivation available={available}");
    }

    /// <summary>What macOS currently says about the window and the app (for the diagnostics log). Empty off macOS.</summary>
    public static string Describe(Window window)
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } native || native == 0) return string.Empty;
            var handle = MacHandle(window, native);
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
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } native || native == 0) return null;
            var handle = MacHandle(window, native);
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
            if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } native || native == 0) return;
            var handle = MacHandle(window, native);
            MsgSendLong(handle, sel_registerName("orderOut:"), 0);
            MsgSendHandle(handle, sel_registerName("orderFrontRegardless"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // The window stays where it is.
        }
    }
}
