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

        Application.Run(new MainForm());
    }
}
