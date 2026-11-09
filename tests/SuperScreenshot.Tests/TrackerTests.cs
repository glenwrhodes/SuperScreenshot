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
        public int[][] Rows;

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

    /// <summary>A File Explorer-like list: every row has the same icon, date and type; only a narrow name differs.</summary>
    static Page ListPage(int rowsCount, int rowHeight, int seed)
    {
        var random = new Random(seed);
        var sameEverywhere = new int[rowHeight][];
        for (int y = 0; y < rowHeight; y++)
        {
            var row = new int[W];
            Array.Fill(row, 0xFFFFFF);
            if (y > 6 && y < rowHeight - 6)
            {
                for (int x = 12; x < 30; x++) row[x] = 0xE8B830;                                  // folder icon
                for (int x = 330; x < 650; x++) if ((x + y * 3) % 7 < 3) row[x] = 0x606060;     // "2026-10-31 12:24 PM  File folder"
            }
            sameEverywhere[y] = row;
        }
        var page = new Page(1, seed);
        Array.Resize(ref page.Rows, rowsCount * rowHeight);
        for (int r = 0; r < rowsCount; r++)
        {
            int nameLength = random.Next(40, 160);
            var name = new int[rowHeight][];
            for (int y = 0; y < rowHeight; y++)
            {
                var row = (int[])sameEverywhere[y].Clone();
                if (y > 8 && y < rowHeight - 8)
                    for (int x = 40; x < 40 + nameLength; x++)
                        if (random.Next(3) == 0) row[x] = 0x202020;                                // the name: the only unique part
                page.Rows[r * rowHeight + y] = row;
            }
        }
        return page;
    }

    [Fact]
    public void HoverEffects_DoNotThrowOffAList()
    {
        // In File Explorer the row under the mouse lights up and a tooltip pops up. Our wheel
        // hops put the mouse on the list for a moment, so some frames have them and some don't.
        const int rowHeight = 33, hoverY = 400;
        var page = ListPage(300, rowHeight, seed: 19);
        var random = new Random(3);
        var path = Walk(0, 2400, seed: 12, maxStep: 200).ToList();
        var frames = new Queue<int[]>(path.Select(scroll =>
        {
            var frame = Frame(page, scroll, header: 40, footer: 30);
            if (random.Next(2) == 0)
            {
                // Highlight the whole row under the mouse.
                int rowTop = (hoverY + scroll) / rowHeight * rowHeight - scroll;
                for (int y = Math.Max(40, rowTop); y < Math.Min(H - 30, rowTop + rowHeight); y++)
                    for (int x = 0; x < W; x++)
                        if (frame[y * W + x] == 0xFFFFFF) frame[y * W + x] = 0xE5F3FF;
            }
            if (random.Next(3) == 0)
            {
                // A tooltip box next to the mouse.
                for (int y = hoverY + 14; y < hoverY + 74; y++)
                    for (int x = 220; x < 560; x++)
                        frame[y * W + x] = (y == hoverY + 14 || x == 220) ? 0x767676 : ((x * 7 + y * 3) % 11 < 2 ? 0x303030 : 0xF8F8F8);
            }
            return frame;
        }));
        using var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        while (frames.Count > 0) tracker.Step();

        Assert.Equal(2400, tracker.Offset);
    }

    [Fact]
    public void HoverHighlightFollowingTheMouse_IsNotAScroll()
    {
        // Dragging down a File Explorer list without scrolling: the highlighted row follows the
        // mouse one row at a time. Since rows look alike, that looks like a one-row scroll.
        const int rowHeight = 43;
        var page = ListPage(200, rowHeight, seed: 20);
        var frames = new Queue<int[]>(Enumerable.Range(2, 14).Select(row =>
        {
            var frame = Frame(page, 0, header: 40, footer: 30);
            for (int y = row * rowHeight; y < (row + 1) * rowHeight && y < H - 30; y++)
                for (int x = 0; x < W; x++)
                    if (frame[y * W + x] == 0xFFFFFF) frame[y * W + x] = 0xE5F3FF;
            return frame;
        }));
        using var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        while (frames.Count > 0)
        {
            tracker.Step();
            Assert.True(tracker.Locked, "a hover change must not stall tracking");
        }

        Assert.False(tracker.HasScrolled, $"the hover was taken for a scroll (offset {tracker.Offset})");
    }

    [Fact]
    public void ListOfNearIdenticalRows_IsNotOffByARow()
    {
        const int rowHeight = 33;
        var page = ListPage(300, rowHeight, seed: 18);
        // Scroll by awkward amounts, including exact multiples of the row height.
        var path = new[] { 0, 33, 99, 150, 231, 300, 333, 432, 500, 561, 660, 700, 792, 891, 990, 1100, 1188, 1300, 1386, 1500 };
        using var tracker = Run(page, path, header: 40, footer: 30);

        Assert.Equal(1500, tracker.Offset);
        for (int d = 40; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
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

    /// <summary>A page that is normal text at the top and nearly empty below <paramref name="sparseFrom"/>: a short line every so often.</summary>
    static Page SparsePage(int height, int sparseFrom, int seed)
    {
        var page = new Page(height, seed);
        var random = new Random(seed + 1);
        for (int y = sparseFrom; y < height; y++)
        {
            var row = new int[W];
            Array.Fill(row, 0xFFFFFF);
            if ((y - sparseFrom) % 230 < 14)
                for (int x = 60; x < 380; x++)
                    if (random.Next(3) == 0) row[x] = random.Next(0, 0x404040);
            page.Rows[y] = row;
        }
        return page;
    }

    [Fact]
    public void ScrollingThroughANearlyEmptyArea_WithAFixedToolbar_KeepsCount()
    {
        // Gmail below the end of a message: a few short lines move while the browser's toolbar
        // and the app's header stay put. That must not be taken for a change in place (which
        // would lose the scroll and throw everything after it off), even going back up.
        var page = SparsePage(6000, sparseFrom: 1800, seed: 21);
        const int header = 150;
        var path = Walk(0, 3600, seed: 13, maxStep: 60).Concat(Walk(3600, 1200, seed: 14, maxStep: 60).Skip(1)).ToList();
        using var tracker = Run(page, path, header, footer: 0);

        Assert.Equal(1200, tracker.Offset);
        for (int d = header; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
    }

    [Fact]
    public void NearlyEmptyArea_WithANarrowSelection_KeepsCount()
    {
        var page = SparsePage(6000, sparseFrom: 1500, seed: 22);
        const int header = 120;
        var path = Walk(0, 3000, seed: 15, maxStep: 45).Concat(Walk(3000, 900, seed: 16, maxStep: 90).Skip(1)).ToList();
        var frames = new Queue<int[]>(path.Select(s => Frame(page, s, header, footer: 0)));
        using var tracker = new Tracker(new Rectangle(0, 0, W, H), _ => frames.Dequeue());
        tracker.SetHashRange(100, 360);
        while (frames.Count > 0) tracker.Step();

        Assert.Equal(900, tracker.Offset);
        for (int d = header; d < tracker.CoveredBottom; d++) AssertRow(page.Rows[d], tracker.RowAt(d), d);
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
