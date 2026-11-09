namespace SuperScreenshot;

/// <summary>Optional trace for diagnosing problems: set SUPERSCREENSHOT_LOG to a file path.</summary>
internal static class Log
{
    static readonly string? Path = Environment.GetEnvironmentVariable("SUPERSCREENSHOT_LOG");
    static readonly object Gate = new();

    public static void Write(string message)
    {
        if (Path == null) return;
        lock (Gate)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}/{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}
