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

        // "--exit" asks the running copy to quit (the uninstaller uses it) and waits until it has.
        if (args.Contains("--exit"))
        {
            AskRunningCopyToExit();
            return;
        }

        // One copy at a time. Starting it again just opens the window of the one already running.
        using var single = new Mutex(true, RunningMutex, out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        using var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEvent);
        if (!first)
        {
            showSignal.Set();
            return;
        }

        // "--tray" is how it starts with Windows: no window, just the tray icon and hotkey.
        var app = new TrayApp(startHidden: args.Contains("--tray"));
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => app.ShowFromOtherInstance(), null, Timeout.Infinite, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => app.ExitFromOtherInstance(), null, Timeout.Infinite, executeOnlyOnce: true);
        Application.Run(app);
        Log.Write("message loop ended, leaving Main");
    }

    const string RunningMutex = @"Local\SuperScreenshot.Running";
    const string ShowEvent = @"Local\SuperScreenshot.Show";
    const string ExitEvent = @"Local\SuperScreenshot.Exit";

    static void AskRunningCopyToExit()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEvent, out var exit)) return; // nothing running
        using (exit) exit.Set();
        if (!Mutex.TryOpenExisting(RunningMutex, out var running)) return;
        using (running)
        {
            try
            {
                if (running.WaitOne(TimeSpan.FromSeconds(10))) running.ReleaseMutex();
            }
            catch (AbandonedMutexException)
            {
                // It ended without releasing it; it's gone either way.
            }
        }
    }
}
