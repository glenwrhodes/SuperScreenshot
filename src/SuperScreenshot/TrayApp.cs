using Microsoft.Win32;

namespace SuperScreenshot;

/// <summary>
/// Keeps SuperScreenshot running in the notification area (hidden icons) with the
/// Win+Shift+A hotkey, like Snipping Tool. The window is only shown when needed.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string SettingsKey = @"Software\SuperScreenshot";
    const string RunValue = "SuperScreenshot";

    readonly MainForm form;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem startWithWindows;
    readonly GlobalHotkey? hotkey;

    public TrayApp(bool startHidden)
    {
        form = new MainForm();
        _ = form.Handle; // so the hotkey can post to the window before it is ever shown
        form.FormClosed += (_, _) => Exit();
        form.HiddenToTray += ShowTrayTipOnce;

        startWithWindows = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartWithWindows()) { CheckOnClick = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"New screenshot\t{GlobalHotkey.Text}", null, (_, _) => form.StartCapture())
        {
            Font = new Font(menu.Font, FontStyle.Bold),
        });
        menu.Items.Add("Open SuperScreenshot", null, (_, _) => form.ShowWindow());
        var openPreview = new ToolStripMenuItem("Open preview after capture", null,
            (_, _) => Settings.OpenPreviewAfterCapture = !Settings.OpenPreviewAfterCapture);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openPreview);
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        menu.Opening += (_, _) =>
        {
            startWithWindows.Checked = IsStartWithWindows();
            openPreview.Checked = Settings.OpenPreviewAfterCapture;
        };

        tray = new NotifyIcon
        {
            Icon = AppIcon.Small(),
            Text = $"SuperScreenshot ({GlobalHotkey.Text})",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) form.StartCapture();
        };

        try
        {
            hotkey = new GlobalHotkey(form.StartCapture);
        }
        catch (Exception ex)
        {
            form.SetStatus($"{GlobalHotkey.Text} isn't available ({ex.Message}). Use the tray icon.");
        }

        FirstRunSetup();
        if (IsStartWithWindows()) SetStartWithWindows(true); // keep the path current if the exe moved
        if (!startHidden) form.ShowWindow();
    }

    /// <summary>Called when another copy of the app was started: show this one instead.</summary>
    public void ShowFromOtherInstance() => form.BeginInvoke(form.ShowWindow);

    /// <summary>Called when "SuperScreenshot --exit" was run (the uninstaller does this).</summary>
    public void ExitFromOtherInstance()
    {
        Log.Write("exit requested by another copy");
        form.BeginInvoke(Exit);
    }

    bool exiting;

    void Exit()
    {
        if (exiting) return; // closing the window below raises FormClosed, which lands here again
        exiting = true;
        Log.Write("exit: start");
        hotkey?.Dispose();
        tray.Visible = false;
        tray.Dispose();
        Log.Write("exit: tray removed");
        form.CloseForReal();
        Log.Write("exit: window closed");
        ExitThread();
        Log.Write("exit: message loop told to stop");
    }

    // ---- Settings ----

    /// <summary>The first time it runs, set it to start with Windows, since a tray hotkey is only useful if it's running.</summary>
    static void FirstRunSetup()
    {
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (settings.GetValue("Configured") != null) return;
        SetStartWithWindows(true);
        settings.SetValue("Configured", 1, RegistryValueKind.DWord);
    }

    static bool IsStartWithWindows()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(RunValue) != null;
    }

    static void SetStartWithWindows(bool on)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) run.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
        else run.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    void ToggleStartWithWindows() => SetStartWithWindows(!IsStartWithWindows());

    void ShowTrayTipOnce()
    {
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (settings.GetValue("TrayTipShown") != null) return;
        settings.SetValue("TrayTipShown", 1, RegistryValueKind.DWord);
        tray.ShowBalloonTip(5000, "SuperScreenshot is still running",
            $"Press {GlobalHotkey.Text} for a screenshot, or click the icon in the hidden icons tray. Right-click it to exit.",
            ToolTipIcon.None);
    }
}

internal static class AppIcon
{
    /// <summary>The app icon at the size Windows wants, from the icon embedded in the exe.</summary>
    public static Icon Get(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("SuperScreenshot.app.ico");
        return stream != null ? new Icon(stream, size) : SystemIcons.Application;
    }

    public static Icon Small() => Get(SystemInformation.SmallIconSize);
}
