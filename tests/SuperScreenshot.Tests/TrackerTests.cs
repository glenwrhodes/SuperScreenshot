using Xunit;

namespace SuperScreenshot.Tests;

/// <summary>
/// Feeds the tracker synthetic "screenshots" of a tall page seen through a window, and checks
/// the stitched result row by row against the page.
/// </summary>
public class TrackerTests
{
    const int W = 700, H = 800;

    /// <summary>A tall page that looks a bit like text: lines of speckled ink with blank gaps.</summary>
    sealed class Page
    {
        public readonly int[][] Rows;

        public Page(int height, int seed)
        {
            var random = new Random(seed);
            Rows = new int[height][];
            int y = 0;
            while (y < height)
            {
                int gap = random.Next(4, 30);
                for (int i = 0; i < gap && y < height; i++, y++) Rows[y] = Blank(0xFFFFFF);

                int lineHeight = random.Next(8, 20);
                int indent = random.Next(0, 80), length = random.Next(200, W - indent);
                int ink = random.Next(0x000000, 0x404040);
                for (int i = 0; i < lineHeight && y < height; i++, y++)
                {
                    var row = Blank(0xFFFFFF);
                    for (int x = indent; x < indent + length; x++)
                        if (random.Next(3) == 0) row[x] = ink + random.Next(0, 0x80);
                    Rows[y] = row;
                }
            }
        }

        static int[] Blank(int colour)
        {
            var row = new int[W];
            Array.Fill(row, colour);
            return row;
        }
    }

    static int[] Pattern(int y, int seed)
    {
        var random = new Random(seed * 10007 + y);
        var row = new int[W];
        for (int x = 0; x < W; x++) row[x] = 0x203040 + random.Next(0, 0x40);
        return row;
    }

    /// <summary>What the window shows when the page is scrolled to <paramref name="scroll"/>.</summary>
    static int[] Frame(Page page, int scroll, int header, int footer, int sidebar = 0)
    {
        var frame = new int[W * H];
        for (int y = 0; y < H; y++)
        {
            int[] row = y < header ? Pattern(y, 1)
                : y >= H - footer ? Pattern(y, 2)
                : page.Rows[y + scroll];
            Array.Copy(row, 0, frame, y * W, W);
            if (sidebar > 0) Array.Copy(Pattern(y, 3), 0, frame, y * W, sidebar);
        }
        return frame;
    }

    static Tracker Run(Page page, IEnumerable<int> scrolls, int header, int footer, int sidebar = 0)
    {
        var frames = new Queue<int[]>(scrolls.Select(s => Frame(page, s, header, footer, sidebar)));
        var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        while (frames.Count > 0) tracker.Step();
        return tracker;
    }

    static void AssertRow(int[] expected, int[]? actual, int documentRow, int fromX = 0)
    {
        Assert.True(actual != null, $"document row {documentRow} is missing");
        for (int x = fromX; x < W; x++)
            if (expected[x] != actual![x])
                Assert.Fail($"document row {documentRow} differs at x={x}");
    }

    /// <summary>The stitched image may stop a little short of the last frame, but only by plain background rows.</summary>
    static void AssertOnlyBlankRowsLost(Page page, int coveredBottom, int expectedBottom)
    {
        Assert.InRange(coveredBottom, expectedBottom - 40, expectedBottom);
        for (int d = coveredBottom; d < expectedBottom; d++)
            Assert.True(page.Rows[d].All(p => p == page.Rows[d][0]), $"row {d} had content but was dropped");
    }

    static IEnumerable<int> Walk(int from, int to, int seed, int maxStep = 300)
    {
        var random = new Random(seed);
        int at = from;
        yield return at;
        while (at != to)
        {
            int step = random.Next(1, maxStep);
            at = from < to ? Math.Min(to, at + step) : Math.Max(to, at - step);
            yield return at;
            if (random.Next(4) == 0) yield return at; // sometimes nothing moves between frames
        }
    }

    [Fact]
    public void ScrollingDown_StitchesThePage_WithoutRepeatingStickyHeaderOrFooter()
    {
        var page = new Page(6000, seed: 7);
        const int header = 60, footer = 40;
        using var tracker = Run(page, Walk(0, 4000, seed: 1), header, footer);

        Assert.True(tracker.HasScrolled);
        Assert.Equal(4000, tracker.Offset);
        Assert.Equal(0, tracker.CoveredTop);
        AssertOnlyBlankRowsLost(page, tracker.CoveredBottom, 4000 + H - footer);

        for (int d = 0; d < header; d++) AssertRow(Pattern(d, 1), tracker.RowAt(d), d);
        for (int d = header; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
    }

    [Fact]
    public void PlainStickyFooter_IsNotRepeated_WhenOnlyPartOfTheWidthIsMatched()
    {
        // A sticky footer that is a plain grey bar inside the selected columns (its text is
        // off to the side), with a one-pixel border line: every footer row is a single colour there.
        var page = new Page(6000, seed: 14);
        const int footer = 45;
        var frames = new Queue<int[]>(Walk(0, 3500, seed: 8).Select(scroll =>
        {
            var frame = Frame(page, scroll, header: 0, footer: 0);
            for (int y = H - footer; y < H; y++)
                Array.Fill(frame, y == H - footer ? 0xCCCCCC : 0xEEEEEE, y * W, W);
            return frame;
        }));
        using var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        tracker.SetHashRange(200, 600);
        while (frames.Count > 0) tracker.Step();

        Assert.Equal(3500, tracker.Offset);
        AssertOnlyBlankRowsLost(page, tracker.CoveredBottom, 3500 + H - footer);
        for (int d = 0; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
    }

    [Fact]
    public void ScrollingUp_StitchesThePage()
    {
        var page = new Page(6000, seed: 8);
        const int header = 50, footer = 0;
        const int startScroll = 3000;
        using var tracker = Run(page, Walk(startScroll, 0, seed: 2), header, footer);

        Assert.Equal(-startScroll, tracker.Offset);
        Assert.Equal(-startScroll + header, tracker.CoveredTop);
        for (int d = tracker.CoveredTop; d < tracker.CoveredBottom; d++)
            AssertRow(page.Rows[d + startScroll], tracker.RowAt(d), d);
    }

    [Fact]
    public void DownThenBackUpThenDown_KeepsEverything()
    {
        var page = new Page(7000, seed: 9);
        const int header = 0, footer = 30;
        var path = Walk(0, 2500, 3).Concat(Walk(2500, 800, 4).Skip(1)).Concat(Walk(800, 4200, 5).Skip(1));
        using var tracker = Run(page, path, header, footer);

        Assert.Equal(4200, tracker.Offset);
        AssertOnlyBlankRowsLost(page, tracker.CoveredBottom, 4200 + H - footer);
        for (int d = 0; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
    }

    [Fact]
    public void FixedSidebar_DoesNotStopTracking()
    {
        var page = new Page(5000, seed: 10);
        const int sidebar = 150;
        using var tracker = Run(page, Walk(0, 3000, seed: 6), header: 0, footer: 0, sidebar);

        Assert.Equal(3000, tracker.Offset);
        for (int d = 0; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d, fromX: sidebar);
    }

    [Fact]
    public void GarbledFrame_IsSkipped_AndTrackingContinues()
    {
        var page = new Page(4000, seed: 11);
        var noise = new Random(5);
        var frames = new Queue<int[]>(new[]
        {
            Frame(page, 0, 0, 0),
            Frame(page, 120, 0, 0),
            Enumerable.Range(0, W * H).Select(_ => noise.Next(0, 0xFFFFFF)).ToArray(), // mid-repaint junk
            Frame(page, 260, 0, 0),
            Frame(page, 400, 0, 0),
        });
        using var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        while (frames.Count > 0) tracker.Step();

        Assert.Equal(400, tracker.Offset);
        for (int d = 0; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
    }

    [Fact]
    public void TornFrame_DoesNotLeaveDoubledLines()
    {
        // While scrolling fast, a grab can catch the window half-updated: the top part at the new
        // position and the rest still at an in-between one. That used to leave a seam where a few
        // lines repeat.
        var page = new Page(4000, seed: 15);
        var torn = Frame(page, 200, 0, 0);
        var lagging = Frame(page, 115, 0, 0);
        Array.Copy(lagging, 520 * W, torn, 520 * W, (H - 520) * W);
        var frames = new Queue<int[]>(new[]
        {
            Frame(page, 0, 0, 0), Frame(page, 100, 0, 0), torn, Frame(page, 300, 0, 0), Frame(page, 420, 0, 0),
        });
        using var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        while (frames.Count > 0) tracker.Step();

        Assert.Equal(420, tracker.Offset);
        for (int d = 0; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
    }

    [Fact]
    public void RowsCaughtBeforeTheyWereDrawn_AreFilledInByLaterFrames()
    {
        // Browsers can show newly scrolled-in rows blank for a moment until they're painted.
        var page = new Page(4000, seed: 16);
        var unpainted = Frame(page, 250, 0, 0);
        Array.Fill(unpainted, 0xFFFFFF, (H - 180) * W, 180 * W);
        var frames = new Queue<int[]>(new[]
        {
            Frame(page, 0, 0, 0), Frame(page, 120, 0, 0), unpainted, Frame(page, 330, 0, 0), Frame(page, 600, 0, 0),
        });
        using var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        while (frames.Count > 0) tracker.Step();

        Assert.Equal(600, tracker.Offset);
        for (int d = 0; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
    }

    [Fact]
    public void SeeThroughStickyHeader_IsNotPaintedOverThePage()
    {
        // A translucent header (blurred, or tinted like rgba white) shows the content scrolling
        // under it, so it changes every frame and isn't recognised as static. It must still not
        // end up stamped over the page every few hundred pixels.
        var page = new Page(5000, seed: 17);
        const int header = 70;
        var frames = new Queue<int[]>(Walk(0, 3000, seed: 9).Select(scroll =>
        {
            var frame = Frame(page, scroll, 0, 0);
            for (int i = 0; i < header * W; i++) frame[i] = (frame[i] >> 2 & 0x3F3F3F) + 0xA0A0A0; // header tinted over content
            return frame;
        }));
        using var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        while (frames.Count > 0) tracker.Step();

        Assert.Equal(3000, tracker.Offset);
        // Only the very top was never seen anywhere but under the header.
        for (int d = header; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
    }

    [Fact]
    public void SmallChangeInPlace_IsNotAScroll()
    {
        var page = new Page(3000, seed: 12);
        var first = Frame(page, 500, 0, 0);
        var hovered = (int[])first.Clone();
        for (int y = 300; y < 312; y++)
            for (int x = 100; x < 400; x++)
                hovered[y * W + x] = 0x0055CC; // a link turning blue under the mouse
        var frames = new Queue<int[]>(new[] { first, hovered, first, hovered });
        using var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        while (frames.Count > 0) tracker.Step();

        Assert.False(tracker.HasScrolled);
        Assert.Equal(0, tracker.Offset);
        Assert.True(tracker.Locked);
    }

    [Fact]
    public void Crop_ReturnsTheSelectedPartOfTheStitchedPage()
    {
        var page = new Page(4000, seed: 13);
        using var tracker = Run(page, Walk(0, 1500, seed: 7), header: 0, footer: 0);
        using var image = tracker.Crop(new Rectangle(100, 300, 250, 1800));

        Assert.NotNull(image);
        Assert.Equal(250, image!.Width);
        Assert.Equal(1800, image.Height);
        foreach (int y in new[] { 0, 1, 777, 1799 })
            foreach (int x in new[] { 0, 120, 249 })
                Assert.Equal(page.Rows[300 + y][100 + x] | unchecked((int)0xFF000000), image.GetPixel(x, y).ToArgb());
    }
}
