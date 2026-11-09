namespace SuperScreenshot;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Whatever happens, never leave the user with crosshair cursors.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SystemCursors.Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => SystemCursors.Restore();
        Application.ThreadException += (_, e) =>
        {
            SystemCursors.Restore();
            MessageBox.Show(e.Exception.Message, "SuperScreenshot", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        ApplicationConfiguration.Initialize(); // per-monitor DPI aware: every coordinate is a physical pixel

        if (args.Length > 0 && args[0] == "--scroll-test")
        {
            Environment.ExitCode = ScrollTest.Run(args);
            return;
        }

        // One copy at a time. Starting it again just opens the window of the one already running.
        using var single = new Mutex(true, @"Local\SuperScreenshot.Running", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SuperScreenshot.Show");
        if (!first)
        {
            showSignal.Set();
            return;
        }

        // "--tray" is how it starts with Windows: no window, just the tray icon and hotkey.
        var app = new TrayApp(startHidden: args.Contains("--tray"));
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => app.ShowFromOtherInstance(), null, Timeout.Infinite, executeOnlyOnce: false);
        Application.Run(app);
    }
}
