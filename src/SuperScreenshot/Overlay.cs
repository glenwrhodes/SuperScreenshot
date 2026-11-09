using System.Drawing.Imaging;

namespace SuperScreenshot;

/// <summary>
/// A full-screen, per-pixel-alpha window that dims the screen and draws the selection.
/// It is click-through (the page under it stays live and scrollable) and excluded from
/// screen capture, so screenshots never include it.
/// </summary>
internal sealed class Overlay : Form
{
    readonly Rectangle area;
    Native.Dib? dib;
    Bitmap? surface;

    public Overlay(Rectangle area)
    {
        this.area = area;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = area;
        Text = "SuperScreenshot selection";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>False if Windows refused to hide the overlay from screen capture.</summary>
    public bool ExcludedFromCapture { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // SUPERSCREENSHOT_SHOW_OVERLAY=1 leaves the overlay visible to screenshots, to check how it
        // looks. Scrolling capture can't work like that, since it would photograph itself.
        if (Environment.GetEnvironmentVariable("SUPERSCREENSHOT_SHOW_OVERLAY") == "1") return;
        ExcludedFromCapture = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) return; // we always cover the whole virtual screen in physical pixels
        if (m.Msg == Native.WM_NCHITTEST) { m.Result = Native.HTTRANSPARENT; return; }
        base.WndProc(ref m);
    }

    /// <summary>Redraws the overlay. The graphics surface is premultiplied ARGB covering the whole area.</summary>
    public void Render(Action<Graphics> draw)
    {
        if (surface == null)
        {
            dib = new Native.Dib(area.Width, area.Height);
            surface = new Bitmap(area.Width, area.Height, area.Width * 4, PixelFormat.Format32bppPArgb, dib.Bits);
        }

        using (var g = Graphics.FromImage(surface))
        {
            draw(g);
            g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        }

        var dst = new Native.POINT(area.X, area.Y);
        var size = new Native.SIZE(area.Width, area.Height);
        var src = new Native.POINT(0, 0);
        var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try
        {
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, dib!.Dc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            surface?.Dispose();
            dib?.Dispose();
            surface = null;
            dib = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Low-level mouse and keyboard hooks on their own thread, so the mouse never lags while the
/// UI thread is busy drawing.
/// </summary>
internal sealed class InputHooks : IDisposable
{
    readonly Thread thread;
    readonly Native.HookProc? mouseProc, keyProc; // kept alive for as long as the hooks are installed
    uint threadId;

    public InputHooks(Native.HookProc? mouse, Native.HookProc? keyboard)
    {
        mouseProc = mouse;
        keyProc = keyboard;
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        thread = new Thread(() =>
        {
            threadId = Native.GetCurrentThreadId();
            Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // make sure this thread has a message queue
            IntPtr module = Native.GetModuleHandle(null);
            IntPtr mouseHook = mouseProc == null ? IntPtr.Zero : Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
            IntPtr keyHook = keyProc == null ? IntPtr.Zero : Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, module, 0);
            if ((mouseProc != null && mouseHook == IntPtr.Zero) || (keyProc != null && keyHook == IntPtr.Zero))
                failure = new InvalidOperationException("Could not install the mouse and keyboard hooks.");
            ready.Set();
            try
            {
                while (failure == null && Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
            }
            finally
            {
                if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
                if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
            }
        })
        {
            IsBackground = true,
            Name = "SuperScreenshot input hooks",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
        ready.Wait();
        if (failure != null) throw failure;
    }

    public void Dispose()
    {
        Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(1000);
    }
}

/// <summary>Shows a crosshair everywhere while selecting, like Snipping Tool, and puts the normal cursors back.</summary>
internal static class SystemCursors
{
    static readonly uint[] Replaced =
    [
        32512, // arrow
        32513, // I-beam
        32649, // hand
        32650, // app starting
        32648, // no
        32642, 32643, 32644, 32645, 32646, // resize arrows
    ];
    const int IDC_CROSS = 32515;
    static int swapped;

    public static void UseCrosshair()
    {
        IntPtr cross = Native.LoadCursor(IntPtr.Zero, (IntPtr)IDC_CROSS);
        if (cross == IntPtr.Zero) return;
        Interlocked.Exchange(ref swapped, 1);
        foreach (uint id in Replaced)
            Native.SetSystemCursor(Native.CopyIcon(cross), id); // SetSystemCursor takes ownership of the copy
    }

    public static void Restore()
    {
        if (Interlocked.Exchange(ref swapped, 0) == 1)
            Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0); // reload the user's cursor scheme
    }
}
