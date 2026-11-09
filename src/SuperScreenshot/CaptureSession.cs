using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SuperScreenshot;

/// <summary>
/// One screenshot: dims the screen, lets the user drag out a region, scrolls the page under it
/// when they drag past the edge or turn the wheel, and hands back the finished image.
///
/// The screen is not frozen. The overlay is click-through and hidden from capture, so the
/// live page underneath can scroll while a <see cref="Tracker"/> stitches what passes by.
/// Clicks and Esc are taken by low-level hooks so they never reach the page.
/// </summary>
internal sealed class CaptureSession
{
    const int Waiting = 0, Dragging = 1, Finished = 2;
    const int WheelNotch = 120;

    static readonly Color Dim = Color.FromArgb(105, 0, 0, 0);
    static readonly Color Accent = Color.FromArgb(255, 43, 136, 255);
    static readonly Color PillBack = Color.FromArgb(235, 24, 26, 30);

    /// <summary>Raised once, on the UI thread. Null when cancelled or failed.</summary>
    public event Action<Bitmap?>? Completed;

    /// <summary>Set when the session ended because of an error.</summary>
    public string? Error { get; private set; }

    readonly Rectangle desktop = SystemInformation.VirtualScreen;
    readonly Overlay overlay;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly ManualResetEventSlim stopTracking = new();
    InputHooks? hooks;

    volatile int phase = Waiting;
    volatile bool dirty = true;
    long cursorPacked;

    // Set when the button goes down.
    Point start;
    Rectangle viewport;  // the window being scrolled, clipped to its monitor
    Point anchor;        // where wheel turns are delivered: on the page, where the drag started
    float scale = 1f;
    Tracker? tracker;
    Thread? trackThread;

    // Auto-scroll state.
    bool armedDown, armedUp;
    int autoDirection;
    long lastScrollAt = -100000;
    long lastHopAt = -100000;
    int framesAtScroll = -1;

    object? lastDrawn;

    public CaptureSession()
    {
        overlay = new Overlay(desktop);
        timer.Tick += (_, _) => Guard(Tick);
    }

    Point Cursor
    {
        get
        {
            long packed = Interlocked.Read(ref cursorPacked);
            return new Point((int)(packed >> 32), (int)(uint)packed);
        }
    }

    void SetCursor(Native.POINT p) => Interlocked.Exchange(ref cursorPacked, ((long)p.X << 32) | (uint)p.Y);

    public void Start()
    {
        try
        {
            Native.GetCursorPos(out var p);
            SetCursor(p);
            overlay.Show();
            Render();
            hooks = new InputHooks(OnMouse, OnKey);
            SystemCursors.UseCrosshair();
            timer.Start();
        }
        catch (Exception ex)
        {
            // Never leave a dimmed screen behind.
            Error = ex.Message;
            Finish(null);
        }
    }

    // ---- Input (these two run on the hook thread and must return quickly) ----

    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && phase != Finished)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != ScreenHelpers.InjectedTag)
            {
                var point = new Point(info.pt.X, info.pt.Y);
                switch ((int)wParam)
                {
                    case Native.WM_MOUSEMOVE:
                        SetCursor(info.pt);
                        dirty = true;
                        break;
                    case Native.WM_LBUTTONDOWN:
                        SetCursor(info.pt);
                        Post(() => BeginSelection(point));
                        return 1;
                    case Native.WM_LBUTTONUP:
                        SetCursor(info.pt);
                        Post(() => EndSelection(point));
                        return 1;
                    case Native.WM_RBUTTONDOWN:
                    case Native.WM_MBUTTONDOWN:
                    case Native.WM_MBUTTONUP:
                        return 1;
                    case Native.WM_RBUTTONUP:
                        Post(Cancel);
                        return 1;
                    case Native.WM_MOUSEWHEEL:
                        // Before the drag starts the wheel just scrolls whatever is under the cursor.
                        // During the drag, send it to the page being captured, wherever the cursor is.
                        if (phase == Dragging)
                        {
                            int delta = (short)(info.mouseData >> 16);
                            Post(() => ScrollPage(delta));
                            return 1;
                        }
                        break;
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && phase != Finished && ((int)wParam == Native.WM_KEYDOWN || (int)wParam == Native.WM_SYSKEYDOWN))
        {
            var key = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (key.vkCode == Native.VK_ESCAPE)
            {
                Post(Cancel);
                return 1;
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    void Post(Action action)
    {
        try
        {
            overlay.BeginInvoke(() => Guard(action));
        }
        catch (InvalidOperationException)
        {
            // The overlay is already gone.
        }
    }

    void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (phase == Finished) return;
            Error = ex.Message;
            StopTracking();
            Finish(null);
        }
    }

    // ---- Selecting ----

    void BeginSelection(Point p)
    {
        if (phase != Waiting) return;

        start = p;
        viewport = ScreenHelpers.ViewportAt(p, overlay.Handle);
        scale = ScreenHelpers.DpiScale(p);
        anchor = new Point(
            Math.Clamp(p.X, viewport.Left + 2, viewport.Right - 3),
            Math.Clamp(Math.Max(p.Y, viewport.Top + viewport.Height / 3), viewport.Top + 2, viewport.Bottom - 3));

        // Auto-scroll only once the cursor has been away from that edge, so starting a
        // selection near the edge doesn't scroll straight away.
        int edge = EdgeZone;
        armedDown = p.Y < viewport.Bottom - edge;
        armedUp = p.Y > viewport.Top + edge;
        autoDirection = 0;
        framesAtScroll = -1;

        var t = new Tracker(viewport);
        tracker = t;
        UpdateHashRange();
        stopTracking.Reset();
        trackThread = new Thread(() => TrackLoop(t))
        {
            IsBackground = true,
            Name = "SuperScreenshot tracker",
        };
        trackThread.Start();

        phase = Dragging;
        dirty = true;
    }

    void EndSelection(Point end)
    {
        if (phase != Dragging || tracker == null) return;
        var t = tracker;
        int offsetAtRelease = t.Offset; // what the user was looking at when they let go
        StopTracking();

        Bitmap? result;
        if (!t.HasScrolled)
        {
            var area = Normalize(start, end);
            area.Intersect(desktop);
            if (area.Width < 4 && area.Height < 4)
            {
                // A click rather than a drag: start over.
                t.Dispose();
                tracker = null;
                phase = Waiting;
                dirty = true;
                return;
            }
            using var grabber = new FrameGrabber();
            int[] pixels = grabber.Grab(area);
            result = Pixels.ToBitmap(pixels, area.Width, new Rectangle(0, 0, area.Width, area.Height));
        }
        else
        {
            result = t.Crop(DocumentSelection(end, offsetAtRelease));
        }
        Finish(result);
    }

    void Cancel()
    {
        if (phase == Finished) return;
        StopTracking();
        Finish(null);
    }

    void Finish(Bitmap? result)
    {
        if (phase == Finished) return;
        phase = Finished;
        timer.Stop();
        timer.Dispose();
        hooks?.Dispose();
        hooks = null;
        SystemCursors.Restore();
        tracker?.Dispose();
        tracker = null;
        overlay.Close();
        overlay.Dispose();
        Completed?.Invoke(result);
    }

    // ---- Tracking and scrolling ----

    void TrackLoop(Tracker t)
    {
        const int FrameMs = 30;
        try
        {
            while (!stopTracking.IsSet)
            {
                long began = clock.ElapsedMilliseconds;
                if (t.Step()) dirty = true;
                int wait = FrameMs - (int)(clock.ElapsedMilliseconds - began);
                if (wait > 0) stopTracking.Wait(wait);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    void StopTracking()
    {
        stopTracking.Set();
        trackThread?.Join();
        trackThread = null;
    }

    void Tick()
    {
        if (phase == Finished) return;

        // The hook sees real mouse movement, but some things (pens, remote-control tools)
        // move the cursor without hook events, so check where it actually is too. Skip the
        // instant right after a wheel turn, when the cursor may be mid-hop to the anchor.
        if (Native.GetCursorPos(out var actual) && clock.ElapsedMilliseconds - lastHopAt > 40)
        {
            var known = Cursor;
            if (actual.X != known.X || actual.Y != known.Y)
            {
                SetCursor(actual);
                dirty = true;
            }
        }

        if (phase == Dragging && tracker != null)
        {
            UpdateHashRange();
            AutoScroll(tracker);
        }
        if (dirty)
        {
            dirty = false;
            Render();
        }
    }

    void UpdateHashRange()
    {
        if (tracker == null) return;
        var c = Cursor;
        tracker.SetHashRange(Math.Min(start.X, c.X) - viewport.Left, Math.Max(start.X, c.X) - viewport.Left + 1);
    }

    int EdgeZone => (int)(40 * scale);

    /// <summary>Scrolls while the cursor is held near (or past) the top or bottom of the page; faster the further in.</summary>
    void AutoScroll(Tracker t)
    {
        var c = Cursor;
        int edge = EdgeZone;
        if (c.Y < viewport.Bottom - edge) armedDown = true;
        if (c.Y > viewport.Top + edge) armedUp = true;

        int direction = 0;
        double depth = 0;
        if (armedDown && c.Y >= viewport.Bottom - edge)
        {
            direction = 1;
            depth = (c.Y - (viewport.Bottom - edge) + 1) / (double)edge;
        }
        else if (armedUp && c.Y <= viewport.Top + edge)
        {
            direction = -1;
            depth = ((viewport.Top + edge) - c.Y + 1) / (double)edge;
        }

        if (direction != autoDirection)
        {
            autoDirection = direction;
            dirty = true;
        }
        if (direction == 0) return;

        // About 5 notches a second at the edge of the zone, up to about 30 when well past it.
        depth = Math.Clamp(depth, 0.1, 3.0);
        double interval = 220 / (1 + 2.2 * depth);
        long now = clock.ElapsedMilliseconds;
        if (now - lastScrollAt < interval) return;

        // Don't outrun the tracker: let it see each scroll before sending the next.
        if (t.FramesProcessed <= framesAtScroll || !t.Locked) return;
        if (t.CoveredHeight >= Tracker.MaxRows) return;

        ScrollPage(direction > 0 ? -WheelNotch : WheelNotch);
        lastScrollAt = now;
        framesAtScroll = t.FramesProcessed;
    }

    /// <summary>Turns the wheel over the page being captured, wherever the cursor happens to be.</summary>
    void ScrollPage(int delta)
    {
        if (phase != Dragging) return;
        lastHopAt = clock.ElapsedMilliseconds;
        ScreenHelpers.WheelAt(anchor, delta);
    }

    // ---- Geometry ----

    static Rectangle Normalize(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>The selection in tracker coordinates: x relative to the viewport, y in document rows.</summary>
    Rectangle DocumentSelection(Point end, int offset)
    {
        int x0 = Math.Clamp(Math.Min(start.X, end.X), viewport.Left, viewport.Right) - viewport.Left;
        int x1 = Math.Clamp(Math.Max(start.X, end.X), viewport.Left, viewport.Right) - viewport.Left;
        int startRow = start.Y - viewport.Top;
        int endRow = Math.Clamp(end.Y, viewport.Top, viewport.Bottom) - viewport.Top + offset;
        return Rectangle.FromLTRB(x0, Math.Min(startRow, endRow), x1, Math.Max(startRow, endRow));
    }

    /// <summary>The part of the selection that is on screen now, its full size, and how much is scrolled out of view.</summary>
    Rectangle SelectionOnScreen(out Size size, out int hiddenAbove, out int hiddenBelow)
    {
        var c = Cursor;
        hiddenAbove = hiddenBelow = 0;
        if (tracker == null || !tracker.HasScrolled)
        {
            var plain = Normalize(start, c);
            size = plain.Size;
            return plain;
        }

        int offset = tracker.Offset;
        var doc = DocumentSelection(c, offset);
        size = doc.Size;
        int top = doc.Top - offset + viewport.Top;
        int bottom = doc.Bottom - offset + viewport.Top;
        hiddenAbove = Math.Max(0, viewport.Top - top);
        hiddenBelow = Math.Max(0, bottom - viewport.Bottom);
        return Rectangle.FromLTRB(
            doc.Left + viewport.Left, Math.Max(top, viewport.Top),
            doc.Right + viewport.Left, Math.Min(bottom, viewport.Bottom));
    }

    // ---- Drawing ----

    void Render()
    {
        if (phase == Finished) return;
        if (phase == Dragging)
        {
            var selection = SelectionOnScreen(out var size, out int above, out int below);
            var state = (selection, size, above, below, autoDirection);
            if (state.Equals(lastDrawn)) return;
            lastDrawn = state;
            overlay.Render(g => DrawSelection(g, selection, size, above, below));
        }
        else
        {
            var monitor = Screen.FromPoint(Cursor).Bounds;
            if (monitor.Equals(lastDrawn)) return;
            lastDrawn = monitor;
            overlay.Render(g => DrawHint(g, monitor));
        }
    }

    void DrawHint(Graphics g, Rectangle monitor)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Dim);
        g.CompositingMode = CompositingMode.SourceOver;
        float s = ScreenHelpers.DpiScale(monitor.Location + new Size(monitor.Width / 2, monitor.Height / 2));
        var local = monitor;
        local.Offset(-desktop.X, -desktop.Y);
        DrawPill(g, "Drag to select   ·   Drag past the edge or use the mouse wheel to scroll   ·   Esc to cancel",
            new PointF(local.Left + local.Width / 2f, local.Top + 24 * s), s, centered: true);
    }

    void DrawSelection(Graphics g, Rectangle selection, Size size, int above, int below)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Dim);
        var local = selection;
        local.Offset(-desktop.X, -desktop.Y);
        if (local.Width > 0 && local.Height > 0)
            g.FillRectangle(Brushes.Transparent, local); // punch a clear hole

        g.CompositingMode = CompositingMode.SourceOver;
        float s = scale;
        using (var pen = new Pen(Accent, Math.Max(1f, MathF.Round(2 * s))))
        {
            var outline = local;
            outline.Inflate(1, 1);
            g.DrawRectangle(pen, outline);
        }

        // Size label just below the selection (or inside it if there's no room).
        string arrow = autoDirection > 0 ? "▼  " : autoDirection < 0 ? "▲  " : "";
        string label = $"{arrow}{size.Width:N0} × {size.Height:N0}";
        float gap = 8 * s;
        var labelAt = new PointF(local.Left, local.Bottom + gap);
        if (labelAt.Y + 32 * s > overlay.Height) labelAt = new PointF(local.Left + gap, local.Bottom - 32 * s - gap);
        DrawPill(g, label, labelAt, s, centered: false);

        if (above > 0)
            DrawPill(g, $"▲  {above:N0} px above", new PointF(local.Left + local.Width / 2f, local.Top + gap), s, centered: true);
        if (below > 0)
            DrawPill(g, $"▼  {below:N0} px below", new PointF(local.Left + local.Width / 2f, local.Bottom - 32 * s - gap), s, centered: true);
    }

    static void DrawPill(Graphics g, string text, PointF at, float s, bool centered)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI Semibold", 12.5f * s, GraphicsUnit.Pixel);
        var textSize = g.MeasureString(text, font);
        float padX = 10 * s, padY = 5 * s;
        var box = new RectangleF(at.X, at.Y, textSize.Width + padX * 2, textSize.Height + padY * 2);
        if (centered) box.X -= box.Width / 2;

        using (var path = RoundedRect(box, box.Height / 2))
        using (var back = new SolidBrush(PillBack))
            g.FillPath(back, path);
        g.DrawString(text, font, Brushes.White, box.X + padX, box.Y + padY);
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
