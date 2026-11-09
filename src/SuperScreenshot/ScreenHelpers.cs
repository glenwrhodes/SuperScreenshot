using System.Runtime.InteropServices;

namespace SuperScreenshot;

internal static class ScreenHelpers
{
    /// <summary>Marks the mouse events we inject ourselves, so our own hook lets them through.</summary>
    public static readonly IntPtr InjectedTag = 0x53535348;

    /// <summary>
    /// The top-level window under a point, clipped to its monitor: the area that scrolls.
    /// Falls back to the whole monitor (desktop, odd windows).
    /// </summary>
    public static Rectangle ViewportAt(Point p, IntPtr ignore)
    {
        var monitor = Screen.FromPoint(p).Bounds;
        IntPtr window = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT(p.X, p.Y)), Native.GA_ROOT);
        if (window == IntPtr.Zero || window == ignore) return monitor;

        Native.RECT r;
        if (Native.DwmGetWindowAttribute(window, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<Native.RECT>()) != 0
            && !Native.GetWindowRect(window, out r))
        {
            return monitor;
        }
        var bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        bounds.Intersect(monitor);
        return bounds.Width >= 64 && bounds.Height >= 64 && bounds.Contains(p) ? bounds : monitor;
    }

    /// <summary>Display scale of the monitor at a point (1.0 = 96 DPI).</summary>
    public static float DpiScale(Point p)
    {
        IntPtr monitor = Native.MonitorFromPoint(new Native.POINT(p.X, p.Y), Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96f : 1f;
    }

    /// <summary>
    /// Turns the mouse wheel over <paramref name="anchor"/> without visibly moving the cursor:
    /// it hops there, the wheel turns, and it hops back, in one SendInput call so no other
    /// input can get in between. Negative delta scrolls down.
    /// </summary>
    public static void WheelAt(Point anchor, int delta)
    {
        var desktop = SystemInformation.VirtualScreen;
        Native.GetCursorPos(out var now);
        var inputs = new[]
        {
            MoveTo(desktop, anchor.X, anchor.Y),
            new Native.INPUT
            {
                type = Native.INPUT_MOUSE,
                mi = new Native.MOUSEINPUT { mouseData = (uint)delta, dwFlags = Native.MOUSEEVENTF_WHEEL, dwExtraInfo = InjectedTag },
            },
            MoveTo(desktop, now.X, now.Y),
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    static Native.INPUT MoveTo(Rectangle desktop, int x, int y) => new()
    {
        type = Native.INPUT_MOUSE,
        mi = new Native.MOUSEINPUT
        {
            dx = (int)Math.Round((x - desktop.Left) * 65535.0 / Math.Max(1, desktop.Width - 1)),
            dy = (int)Math.Round((y - desktop.Top) * 65535.0 / Math.Max(1, desktop.Height - 1)),
            dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
            dwExtraInfo = InjectedTag,
        },
    };
}
