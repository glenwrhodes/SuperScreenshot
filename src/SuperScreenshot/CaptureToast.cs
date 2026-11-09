using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SuperScreenshot;

/// <summary>
/// The little popup in the bottom-right corner after a capture, like Snipping Tool's: a
/// thumbnail and "copied to clipboard". It never takes the focus, so the app you were in keeps
/// it and Ctrl+V works straight away. Click it to open the preview window; it slides away by
/// itself after a few seconds, but not while the mouse is over it.
/// </summary>
internal sealed class CaptureToast : Form
{
    const int ShowForMs = 5500;
    const int SlideMs = 220;

    /// <summary>Raised when the popup is clicked (not its close button).</summary>
    public event Action? Opened;

    readonly Bitmap thumbnail;
    readonly string title, detail;
    readonly float s;
    readonly bool dark;
    readonly Rectangle home;   // where it sits once it has slid in
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    long shownAt, leavingAt = -1;
    bool hoverClose;

    Color Back => dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
    Color Border => dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);
    Color Text1 => dark ? Color.White : Color.FromArgb(26, 26, 26);
    Color Text2 => dark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(96, 96, 96);

    public CaptureToast(Bitmap image, bool copied, Point near)
    {
        dark = Settings.WindowsUsesDarkTheme;
        s = ScreenHelpers.DpiScale(near);
        title = copied ? "Screenshot copied to clipboard" : "Screenshot taken";
        detail = $"{image.Width:N0} × {image.Height:N0}   ·   Select to open";

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Text = "SuperScreenshot notification";
        Cursor = Cursors.Hand;

        var size = new Size(Px(364), Px(112));
        var work = Screen.FromPoint(near).WorkingArea;
        home = new Rectangle(work.Right - size.Width - Px(16), work.Bottom - size.Height - Px(16), size.Width, size.Height);
        Bounds = new Rectangle(work.Right, home.Top, size.Width, size.Height); // off to the right, slides in

        thumbnail = MakeThumbnail(image, new Size(Px(112), Px(74)));
        timer.Tick += (_, _) => Animate();
    }

    int Px(float logical) => (int)Math.Round(logical * s);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; } // clicking it mustn't take the focus either
        if (m.Msg == Native.WM_DPICHANGED) return;
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND: Windows 11 rounded corners (ignored on Windows 10)
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        shownAt = clock.ElapsedMilliseconds;
        timer.Start();
    }

    /// <summary>Slides it away (or closes it at once) without opening anything.</summary>
    public void Dismiss(bool immediately = false)
    {
        if (IsDisposed) return;
        if (immediately || !Visible) { Close(); return; }
        if (leavingAt < 0) leavingAt = clock.ElapsedMilliseconds;
    }

    void Animate()
    {
        long now = clock.ElapsedMilliseconds;
        var work = Screen.FromRectangle(home).WorkingArea;
        int hiddenLeft = work.Right;

        if (leavingAt >= 0)
        {
            double t = Math.Min(1, (now - leavingAt) / (SlideMs * 0.7));
            Left = home.Left + (int)((hiddenLeft - home.Left) * t * t);
            if (t >= 1) Close();
            return;
        }

        double inT = Math.Min(1, (now - shownAt) / (double)SlideMs);
        double eased = 1 - Math.Pow(1 - inT, 3);
        int left = hiddenLeft + (int)((home.Left - hiddenLeft) * eased);
        if (Left != left) Left = left;

        // Stay while the mouse is over it; otherwise leave after a few seconds.
        bool hovered = Bounds.Contains(Cursor.Position);
        bool overClose = hovered && CloseBox.Contains(PointToClient(Cursor.Position));
        if (overClose != hoverClose) { hoverClose = overClose; Invalidate(); }
        if (hovered) shownAt = now - SlideMs;
        else if (now - shownAt > ShowForMs) leavingAt = now;
    }

    Rectangle CloseBox => new(Width - Px(40), Px(8), Px(32), Px(28));

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (CloseBox.Contains(e.Location)) { Dismiss(); return; }
        Opened?.Invoke();
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Back);
        using (var pen = new Pen(Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        // Header: app icon and name, like a Windows notification.
        using (var icon = AppIcon.Get(new Size(Px(16), Px(16))))
            g.DrawIcon(icon, new Rectangle(Px(14), Px(12), Px(16), Px(16)));
        using (var small = new Font("Segoe UI", 12f * s, GraphicsUnit.Pixel))
        using (var text2 = new SolidBrush(Text2))
            g.DrawString("SuperScreenshot", small, text2, Px(36), Px(12.5f));

        // Close button.
        if (hoverClose)
            using (var hover = new SolidBrush(dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(230, 230, 230)))
                g.FillRectangle(hover, CloseBox);
        using (var pen = new Pen(Text2, Math.Max(1f, s)))
        {
            var c = new PointF(CloseBox.X + CloseBox.Width / 2f, CloseBox.Y + CloseBox.Height / 2f);
            float r = Px(4.5f);
            g.DrawLine(pen, c.X - r, c.Y - r, c.X + r, c.Y + r);
            g.DrawLine(pen, c.X - r, c.Y + r, c.X + r, c.Y - r);
        }

        // Thumbnail with rounded corners and a hairline border.
        var thumbAt = new Rectangle(Px(14), Px(32), thumbnail.Width, thumbnail.Height);
        using (var path = Rounded(thumbAt, Px(4)))
        {
            g.SetClip(path);
            g.DrawImage(thumbnail, thumbAt);
            g.ResetClip();
            using var pen = new Pen(Border);
            g.DrawPath(pen, path);
        }

        // Text.
        float textX = thumbAt.Right + Px(14);
        using (var bold = new Font("Segoe UI Semibold", 14f * s, GraphicsUnit.Pixel))
        using (var text1 = new SolidBrush(Text1))
            g.DrawString(title, bold, text1, new RectangleF(textX, Px(44), Width - textX - Px(12), Px(24)));
        using (var regular = new Font("Segoe UI", 12.5f * s, GraphicsUnit.Pixel))
        using (var text2 = new SolidBrush(Text2))
            g.DrawString(detail, regular, text2, new RectangleF(textX, Px(68), Width - textX - Px(12), Px(22)));
    }

    /// <summary>The image scaled to cover the box, from the top (so a tall capture shows its start).</summary>
    static Bitmap MakeThumbnail(Bitmap image, Size box)
    {
        var thumb = new Bitmap(box.Width, box.Height);
        using var g = Graphics.FromImage(thumb);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Max(box.Width / (float)image.Width, box.Height / (float)image.Height);
        float w = image.Width * scale, h = image.Height * scale;
        g.DrawImage(image, new RectangleF((box.Width - w) / 2, 0, w, h));
        return thumb;
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            thumbnail.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
