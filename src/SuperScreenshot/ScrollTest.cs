using System.Diagnostics;
using System.Text;

namespace SuperScreenshot;

/// <summary>
/// Developer check of scrolling capture against a real window, without the UI:
///   SuperScreenshot.exe --scroll-test X Y NOTCHES OUT.png
/// Tracks the window under (X, Y), turns the wheel NOTCHES times there (negative = up),
/// and saves everything stitched to OUT.png plus a log in OUT.png.txt.
/// </summary>
internal static class ScrollTest
{
    public static int Run(string[] args)
    {
        if (args.Length < 5) return 2;
        var at = new Point(int.Parse(args[1]), int.Parse(args[2]));
        int notches = int.Parse(args[3]);
        string output = Path.GetFullPath(args[4]);
        int delta = notches >= 0 ? -120 : 120;

        var viewport = ScreenHelpers.ViewportAt(at, IntPtr.Zero);
        var log = new StringBuilder();
        log.AppendLine($"viewport {viewport}");

        using var tracker = new Tracker(viewport);
        tracker.SetHashRange(0, viewport.Width);
        var clock = Stopwatch.StartNew();
        bool stop = false;
        var thread = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                long began = clock.ElapsedMilliseconds;
                tracker.Step();
                int wait = 30 - (int)(clock.ElapsedMilliseconds - began);
                if (wait > 0) Thread.Sleep(wait);
            }
        });
        thread.Start();
        Thread.Sleep(300);

        for (int i = 0; i < Math.Abs(notches); i++)
        {
            ScreenHelpers.WheelAt(at, delta);
            Thread.Sleep(90);
            log.AppendLine($"{clock.ElapsedMilliseconds,6} ms  offset {tracker.Offset,6}  locked {tracker.Locked}  frames {tracker.FramesProcessed}");
        }
        Thread.Sleep(800);
        Volatile.Write(ref stop, true);
        thread.Join();

        log.AppendLine($"final offset {tracker.Offset}, covered {tracker.CoveredTop}..{tracker.CoveredBottom}");
        using var image = tracker.Crop(new Rectangle(0, tracker.CoveredTop, viewport.Width, tracker.CoveredHeight));
        image?.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllText(output + ".txt", log.ToString());
        return 0;
    }
}
