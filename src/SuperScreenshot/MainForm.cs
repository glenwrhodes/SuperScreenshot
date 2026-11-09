using System.Drawing.Imaging;

namespace SuperScreenshot;

/// <summary>The app's one window: a New button, and the preview of the last screenshot.</summary>
internal sealed class MainForm : Form
{
    static readonly Color Accent = Color.FromArgb(43, 136, 255);
    static readonly Color BarBack = Color.FromArgb(246, 247, 249);
    static readonly Color ViewerBack = Color.FromArgb(228, 231, 236);

    readonly FlowLayoutPanel bar;
    readonly Button newButton, copyButton, saveButton;
    readonly Label status;
    readonly Panel viewer;
    readonly PictureBox picture;
    readonly Label intro;

    Bitmap? image;
    CaptureSession? session;
    CaptureToast? toast;
    bool starting;
    bool closingForReal;

    /// <summary>Raised when the user closes the window and it goes to the tray instead.</summary>
    public event Action? HiddenToTray;

    public MainForm()
    {
        Text = "SuperScreenshot";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.White;
        ClientSize = new Size(620, 260);
        MinimumSize = new Size(480, 200);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Icon = AppIcon.Get(new Size(32, 32));

        newButton = MakeButton("+  New", primary: true);
        copyButton = MakeButton("Copy", primary: false);
        saveButton = MakeButton("Save…", primary: false);
        status = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 96, 105),
            Margin = new Padding(10, 9, 0, 0),
        };
        bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = BarBack,
            Padding = new Padding(8, 7, 8, 7),
        };
        bar.Controls.AddRange([newButton, copyButton, saveButton, status]);

        picture = new PictureBox { SizeMode = PictureBoxSizeMode.Normal, Location = Point.Empty, BackColor = Color.White };
        viewer = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ViewerBack, Visible = false };
        viewer.Controls.Add(picture);
        viewer.Resize += (_, _) => CenterPicture();

        intro = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(70, 76, 85),
            Padding = new Padding(24),
            Text =
                $"Press {GlobalHotkey.Text} anywhere (or click New) and drag to select.\n\n" +
                "Need more than fits on the screen? Keep dragging past the bottom edge,\n" +
                "or turn the mouse wheel, and the page scrolls while you select.\n\n" +
                "Your screenshot is copied to the clipboard automatically.\n" +
                "Closing this window keeps SuperScreenshot ready in the hidden icons tray.",
        };

        // Fill controls first, then the toolbar, so the toolbar docks on top.
        Controls.Add(viewer);
        Controls.Add(intro);
        Controls.Add(bar);

        newButton.Click += (_, _) => StartCapture();
        copyButton.Click += (_, _) => CopyImage();
        saveButton.Click += (_, _) => SaveImage();
        UpdateButtons();
    }

    static Button MakeButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(0, 0, 6, 0),
            BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Color.FromArgb(30, 34, 40),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(205, 210, 218);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(25, 118, 235) : Color.FromArgb(238, 241, 245);
        return button;
    }

    public void SetStatus(string text) => status.Text = text;

    /// <summary>Closing the window just hides it; the app stays ready in the tray.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X button and Alt+F4 hide it. Windows shutdown, Task Manager and the tray's Exit really close it.
        if (!closingForReal && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.OnFormClosing(e);
    }

    public void CloseForReal()
    {
        closingForReal = true;
        Close();
    }

    /// <summary>Shows the window in front of everything, even when another app has the focus.</summary>
    public void ShowWindow()
    {
        if (session != null || starting) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        IntPtr foreground = Native.GetForegroundWindow();
        uint theirs = Native.GetWindowThreadProcessId(foreground, out _);
        uint ours = Native.GetCurrentThreadId();
        // Windows only lets the app in use hand over the focus, so briefly share its input state.
        bool attached = theirs != 0 && theirs != ours && Native.AttachThreadInput(ours, theirs, true);
        Native.SetForegroundWindow(Handle);
        Native.BringWindowToTop(Handle);
        if (attached) Native.AttachThreadInput(ours, theirs, false);
        Activate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.N: StartCapture(); return true;
            case Keys.Control | Keys.C: CopyImage(); return true;
            case Keys.Control | Keys.S: SaveImage(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public async void StartCapture()
    {
        if (session != null || starting) return;
        starting = true;
        toast?.Dismiss(immediately: true); // never in the next screenshot

        bool wasVisible = Visible;
        var previousState = WindowState;
        bool wasShowing = Visible && WindowState != FormWindowState.Minimized;
        Hide();
        if (wasShowing) await Task.Delay(250); // let the window fade out before the overlay appears

        var capture = new CaptureSession();
        session = capture;
        starting = false;
        capture.Completed += result =>
        {
            session = null;
            if (result != null)
            {
                bool copied = ShowImage(result);
                // Like Snipping Tool: started from the window, the window comes back with the
                // screenshot. Started from the hotkey or tray, just a popup in the corner that
                // leaves the focus where it was, so you can paste straight away.
                if (wasShowing || Settings.OpenPreviewAfterCapture) ShowWindow();
                else ShowToast(result, copied);
            }
            else if (capture.Error != null)
            {
                status.Text = "Something went wrong: " + capture.Error;
                ShowWindow();
            }
            else if (wasVisible)
            {
                // Cancelled: put the window back how it was. From the tray, stay hidden.
                WindowState = previousState;
                Show();
            }
        };
        capture.Start(); // reports its own failures through Completed
    }

    void ShowToast(Bitmap bitmap, bool copied)
    {
        toast?.Dismiss(immediately: true);
        var popup = new CaptureToast(bitmap, copied, Cursor.Position);
        popup.Opened += ShowWindow;
        popup.FormClosed += (_, _) => { if (toast == popup) toast = null; };
        toast = popup;
        popup.Show();
    }

    /// <summary>Puts a new screenshot in the window (without showing it) and copies it. Returns true if copied.</summary>
    bool ShowImage(Bitmap bitmap)
    {
        picture.Image = null;
        image?.Dispose();
        image = bitmap;
        picture.Image = bitmap;
        picture.Size = bitmap.Size;
        intro.Visible = false;
        viewer.Visible = true;
        viewer.AutoScrollPosition = Point.Empty;
        UpdateButtons();
        FitToImage();
        CenterPicture();

        string size = $"{bitmap.Width:N0} × {bitmap.Height:N0}";
        bool copied = CopyImage(quiet: true);
        status.Text = copied ? $"{size}  ·  copied to clipboard" : size;
        return copied;
    }

    /// <summary>Sizes the window to show the image at 100%, within 90% of the screen.</summary>
    void FitToImage()
    {
        if (image == null) return;
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        var work = Screen.FromControl(this).WorkingArea;
        int frameW = Width - ClientSize.Width, frameH = Height - ClientSize.Height;
        int margin = LogicalToDeviceUnits(24);
        int scrollbar = SystemInformation.VerticalScrollBarWidth;
        int width = image.Width + frameW + margin + scrollbar;
        int height = image.Height + frameH + bar.Height + margin + scrollbar;
        width = Math.Clamp(width, MinimumSize.Width, (int)(work.Width * 0.9));
        height = Math.Clamp(height, MinimumSize.Height, (int)(work.Height * 0.9));
        Bounds = new Rectangle(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
    }

    void CenterPicture()
    {
        if (image == null) return;
        int pad = LogicalToDeviceUnits(12);
        var scroll = viewer.AutoScrollPosition;
        int x = Math.Max(pad, (viewer.ClientSize.Width - picture.Width) / 2);
        int y = Math.Max(pad, (viewer.ClientSize.Height - picture.Height) / 2);
        picture.Location = new Point(x + scroll.X, y + scroll.Y);
        viewer.AutoScrollMargin = new Size(pad, pad);
    }

    void UpdateButtons()
    {
        copyButton.Enabled = saveButton.Enabled = image != null;
    }

    bool CopyImage(bool quiet = false)
    {
        if (image == null) return false;
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, image);
            var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            data.SetData("PNG", false, png); // keeps full quality in apps that understand PNG
            Clipboard.SetDataObject(data, true, 5, 100);
            if (!quiet) status.Text = $"{image.Width:N0} × {image.Height:N0}  ·  copied to clipboard";
            return true;
        }
        catch (Exception ex)
        {
            status.Text = "Couldn't copy: " + ex.Message;
            return false;
        }
    }

    void SaveImage()
    {
        if (image == null) return;
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string screenshots = Path.Combine(pictures, "Screenshots");
        using var dialog = new SaveFileDialog
        {
            Title = "Save screenshot",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png",
            InitialDirectory = Directory.Exists(screenshots) ? screenshots : pictures,
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            bool jpeg = Path.GetExtension(dialog.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(dialog.FileName).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            image.Save(dialog.FileName, jpeg ? ImageFormat.Jpeg : ImageFormat.Png);
            status.Text = "Saved " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            status.Text = "Couldn't save: " + ex.Message;
        }
    }
}
