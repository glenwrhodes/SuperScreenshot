using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;

namespace SuperScreenshot;

/// <summary>
/// Developer checks of scrolling capture against a real window, without the UI.
///
///   SuperScreenshot.exe --scroll-test X Y NOTCHES OUT.png [FRAMES_DIR]
///     Tracks the window under (X, Y), turns the wheel NOTCHES times there (negative = up;
///     a list like 30,-12,8 goes down, back up, then down again),
///     and saves everything stitched to OUT.png, with a log of every frame's decision in
///     OUT.png.txt. With FRAMES_DIR it also saves each grabbed frame there, for --replay.
///
///   SuperScreenshot.exe --replay FRAMES_DIR OUT.png [X0 X1]
///     Runs saved frames through the stitcher again (optionally matching only columns X0..X1),
///     so a problem seen once in a real app can be reproduced and debugged offline.
/// </summary>
internal static class ScrollTest
{
    public static int Run(string[] args)
    {
        if (args.Length < 5) return 2;
        var at = new Point(int.Parse(args[1]), int.Parse(args[2]));
        int[] moves = args[3].Split(',').Select(int.Parse).ToArray();
        string output = Path.GetFullPath(args[4]);
        string? framesDir = args.Length > 5 ? Path.GetFullPath(args[5]) : null;

        var viewport = ScreenHelpers.ViewportAt(at, IntPtr.Zero);
        var log = new StringBuilder();
        log.AppendLine($"viewport {viewport}");

        var saved = new List<int[]>();
        using var grabber = new FrameGrabber();
        using var tracker = new Tracker(viewport, reuse =>
        {
            int[] frame = grabber.Grab(viewport, framesDir == null ? reuse : null);
            if (framesDir != null) saved.Add(frame);
            return frame;
        });
        tracker.SetHashRange(0, viewport.Width);

        var clock = Stopwatch.StartNew();
        bool stop = false;
        var thread = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                long began = clock.ElapsedMilliseconds;
                tracker.Step();
                lock (log) log.AppendLine($"{began,6} ms  frame {tracker.FramesProcessed,3}  offset {tracker.Offset,6}  {tracker.LastDecision}");
                int wait = 30 - (int)(clock.ElapsedMilliseconds - began);
                if (wait > 0) Thread.Sleep(wait);
            }
        });
        thread.Start();
        Thread.Sleep(300);

        foreach (int notches in moves)
        {
            int delta = notches >= 0 ? -120 : 120;
            for (int i = 0; i < Math.Abs(notches); i++)
            {
                ScreenHelpers.WheelAt(at, delta);
                lock (log) log.AppendLine($"{clock.ElapsedMilliseconds,6} ms  -- wheel {delta}");
                Thread.Sleep(90);
            }
            Thread.Sleep(400);
        }
        Thread.Sleep(800);
        Volatile.Write(ref stop, true);
        thread.Join();

        log.AppendLine($"final offset {tracker.Offset}, covered {tracker.CoveredTop}..{tracker.CoveredBottom}");
        SaveResult(tracker, viewport.Width, output, log);

        if (framesDir != null)
        {
            Directory.CreateDirectory(framesDir);
            for (int i = 0; i < saved.Count; i++)
            {
                using var bitmap = Pixels.ToBitmap(saved[i], viewport.Width, new Rectangle(0, 0, viewport.Width, viewport.Height));
                bitmap.Save(Path.Combine(framesDir, $"frame{i:D4}.png"), ImageFormat.Png);
            }
        }
        return 0;
    }

    public static int Replay(string[] args)
    {
        if (args.Length < 3) return 2;
        string framesDir = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[2]);
        var files = Directory.GetFiles(framesDir, "frame*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) return 3;

        int[] Load(string file, out int width, out int height)
        {
            using var bitmap = new Bitmap(file);
            width = bitmap.Width;
            height = bitmap.Height;
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var pixels = new int[width * height];
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width, width);
            bitmap.UnlockBits(data);
            for (int i = 0; i < pixels.Length; i++) pixels[i] &= 0xFFFFFF;
            return pixels;
        }

        Load(files[0], out int w, out int h);
        int next = 0;
        using var tracker = new Tracker(new Rectangle(0, 0, w, h), reuse => Load(files[next++], out _, out _));
        if (args.Length >= 5) tracker.SetHashRange(int.Parse(args[3]), int.Parse(args[4]));
        else tracker.SetHashRange(0, w);

        var log = new StringBuilder();
        while (next < files.Count)
        {
            string name = Path.GetFileName(files[next]);
            tracker.Step();
            log.AppendLine($"{name}  offset {tracker.Offset,6}  {tracker.LastDecision}");
        }
        log.AppendLine($"final offset {tracker.Offset}, covered {tracker.CoveredTop}..{tracker.CoveredBottom}");
        SaveResult(tracker, w, output, log);
        return 0;
    }

    static void SaveResult(Tracker tracker, int width, string output, StringBuilder log)
    {
        using var image = tracker.Crop(new Rectangle(0, tracker.CoveredTop, width, tracker.CoveredHeight));
        image?.Save(output, ImageFormat.Png);
        File.WriteAllText(output + ".txt", log.ToString());
    }
}
