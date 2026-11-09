namespace SuperScreenshot;

/// <summary>
/// Watches one area of the screen (the "viewport", usually the window being scrolled) and
/// stitches everything that scrolls through it into one tall image.
///
/// Coordinates: a "document row" is a viewport row plus <see cref="Offset"/>, the total
/// distance the content has scrolled since tracking started (positive = scrolled down).
///
/// How a frame is placed: every row of the old and new frame is hashed, in a few vertical
/// bands so a fixed sidebar can't spoil whole rows. Each changed row of the new frame votes
/// for the shifts that would line it up with an identical row of the old frame, and the shift
/// with the most votes wins. Rows that stay put at the top or bottom while everything else
/// moves are a sticky header or footer and are left out of the stitched image.
///
/// Which view of a row is kept: every row passes through the window several times, and the
/// view taken when it was nearest the middle of the window wins (a newer view wins a tie).
/// The edges are where things go wrong: rows scrolling in at the leading edge may not be
/// drawn yet or come from a frame caught mid-redraw, and see-through sticky headers, cookie
/// bars and floating bubbles sit at the edges too.
/// </summary>
internal sealed class Tracker : IDisposable
{
    /// <summary>Tallest image we will stitch, to keep memory bounded.</summary>
    public const int MaxRows = 30000;

    /// <summary>A row that appears more often than this in a frame is too ambiguous to vote.</summary>
    const int MaxRepeats = 8;

    /// <summary>Hash bands are at least this wide, and the hashed range at least MinHashWidth.</summary>
    const int MinBandWidth = 160;
    const int MinHashWidth = 320;
    const int MaxBands = 8;

    public Rectangle Viewport { get; }

    readonly int W, H;
    readonly Func<int[]?, int[]> grab;
    readonly FrameGrabber? grabber;
    readonly Dictionary<int, Row> rows = new();

    /// <summary>One stitched row, and how far from the window's edge it was when we took it.</summary>
    sealed class Row(int width)
    {
        public readonly int[] Pixels = new int[width];
        public int Quality = -1;
    }

    int[]? reference;   // last frame whose position we know
    int[]? spare;       // buffer to reuse for the next grab
    volatile int covTop, covBottom; // document rows [covTop, covBottom) are filled in
    volatile int offset;
    volatile bool hasScrolled;
    volatile bool locked = true;
    volatile int frames;
    long hashRange;

    // Scratch space, reused between frames.
    ulong[] hashRef = [], hashNew = [];
    bool[] flatRef = [], flatNew = [];
    int[] chain = [];
    readonly int[] votes;
    readonly Dictionary<ulong, int> heads = new();
    (int x0, int x1, int bands) refKey = (-1, -1, -1);

    public Tracker(Rectangle viewport, Func<int[]?, int[]>? source = null)
    {
        Viewport = viewport;
        W = viewport.Width;
        H = viewport.Height;
        if (source == null)
        {
            grabber = new FrameGrabber();
            grab = reuse => grabber.Grab(Viewport, reuse);
        }
        else
        {
            grab = source;
        }
        votes = new int[2 * H + 1];
        SetHashRange(0, W);
    }

    /// <summary>How far the content has scrolled since tracking started.</summary>
    public int Offset => offset;

    /// <summary>True once any scrolling has been seen.</summary>
    public bool HasScrolled => hasScrolled;

    /// <summary>False when the last frame could not be matched to the one before it.</summary>
    public bool Locked => locked;

    /// <summary>Number of frames looked at so far.</summary>
    public int FramesProcessed => frames;

    public int CoveredTop => covTop;
    public int CoveredBottom => covBottom;
    public int CoveredHeight => covBottom - covTop;

    /// <summary>
    /// Sets which columns (viewport-relative) are used to match frames. The user's selection
    /// is the best choice: it ignores sidebars and other things that don't scroll with it.
    /// </summary>
    public void SetHashRange(int x0, int x1)
    {
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (x1 - x0 < MinHashWidth)
        {
            int middle = (x0 + x1) / 2;
            x0 = middle - MinHashWidth / 2;
            x1 = x0 + MinHashWidth;
        }
        if (x0 < 0) { x1 -= x0; x0 = 0; }
        if (x1 > W) { x0 -= x1 - W; x1 = W; }
        x0 = Math.Max(0, x0);
        Interlocked.Exchange(ref hashRange, ((long)x0 << 32) | (uint)x1);
    }

    /// <summary>Grabs a frame and folds it into the stitched image. Returns true if anything changed.</summary>
    public bool Step()
    {
        if (reference == null)
        {
            reference = grab(null);
            for (int y = 0; y < H; y++) Store(y, reference, y, Math.Min(y, H - 1 - y));
            covTop = 0;
            covBottom = H;
            frames++;
            return true;
        }

        int[] frame = grab(spare);
        spare = null;

        long range = Interlocked.Read(ref hashRange);
        int x0 = (int)(range >> 32), x1 = (int)(uint)range;
        if (x1 <= x0 || x1 > W) { x0 = 0; x1 = W; }
        int bands = Math.Clamp((x1 - x0) / MinBandWidth, 1, MaxBands);
        int n = bands * H;
        EnsureScratch(n);

        if (refKey != (x0, x1, bands))
        {
            HashRows(reference, x0, x1, bands, hashRef, flatRef);
            refKey = (x0, x1, bands);
        }
        HashRows(frame, x0, x1, bands, hashNew, flatNew);

        int changed = 0, changedDetailed = 0;
        for (int i = 0; i < n; i++)
        {
            if (hashRef[i] == hashNew[i]) continue;
            changed++;
            if (!flatNew[i]) changedDetailed++;
        }

        if (changed == 0)
        {
            // Nothing moved; keep the old reference and reuse this buffer.
            spare = frame;
            locked = true;
            frames++;
            return false;
        }

        // A shift wins if it explains a good share of the changed rows that match anything at
        // all (rows spoiled by a fixed sidebar or an animation match nothing and don't count),
        // and isn't a tiny fraction of everything that changed.
        int shift = FindShift(bands, out int support, out int matchable);
        bool scrolled = shift != 0
            && support >= 4
            && support >= matchable * 3 / 10
            && support >= changedDetailed / 25;

        if (scrolled)
        {
            Place(frame, shift, bands);
            Accept(frame);
            locked = true;
        }
        else if (changedDetailed <= Math.Max(3, n / 40))
        {
            // A small change in place (hover effect, blinking caret). Nothing scrolled.
            Accept(frame);
            locked = true;
        }
        else
        {
            // Lots changed but no shift explains it (mid-repaint, video, page swapped).
            // Keep matching against the last good frame.
            spare = frame;
            locked = false;
        }
        frames++;
        return true;
    }

    /// <summary>Cuts a rectangle out of the stitched image. X is viewport-relative, Y is in document rows.</summary>
    public Bitmap? Crop(Rectangle document)
    {
        int x0 = Math.Clamp(document.Left, 0, W), x1 = Math.Clamp(document.Right, 0, W);
        int y0 = Math.Max(document.Top, covTop), y1 = Math.Min(document.Bottom, covBottom);
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;
        return Pixels.Build(x1 - x0, y1 - y0, y =>
            rows.TryGetValue(y0 + y, out var row) ? (row.Pixels, x0) : (null, 0));
    }

    /// <summary>One stitched row (for tests).</summary>
    internal int[]? RowAt(int documentRow) => rows.TryGetValue(documentRow, out var row) ? row.Pixels : null;

    void Accept(int[] frame)
    {
        spare = reference;
        reference = frame;
        (hashRef, hashNew) = (hashNew, hashRef);
        (flatRef, flatNew) = (flatNew, flatRef);
    }

    /// <summary>Finds the shift (in rows) that lines up the most changed rows of the new frame with the old one.</summary>
    int FindShift(int bands, out int support, out int matchable)
    {
        matchable = 0;
        heads.Clear();
        for (int b = 0; b < bands; b++)
        {
            ulong salt = (ulong)(b + 1) * 0x9E3779B97F4A7C15UL;
            for (int y = 0; y < H; y++)
            {
                int i = b * H + y;
                if (flatRef[i]) continue;
                ulong key = hashRef[i] ^ salt;
                chain[i] = heads.TryGetValue(key, out int head) ? head : -1;
                heads[key] = i;
            }
        }

        Array.Clear(votes);
        for (int b = 0; b < bands; b++)
        {
            ulong salt = (ulong)(b + 1) * 0x9E3779B97F4A7C15UL;
            int bandStart = b * H;
            for (int y = 0; y < H; y++)
            {
                int i = bandStart + y;
                if (flatNew[i] || hashNew[i] == hashRef[i]) continue;
                if (!heads.TryGetValue(hashNew[i] ^ salt, out int head)) continue;

                int count = 0;
                for (int k = head; k >= 0 && count <= MaxRepeats; k = chain[k]) count++;
                if (count > MaxRepeats) continue;

                matchable++;
                for (int k = head; k >= 0; k = chain[k])
                {
                    int shift = (k - bandStart) - y;
                    if (shift != 0) votes[shift + H]++;
                }
            }
        }

        int best = 0;
        support = 0;
        for (int s = 1; s < H; s++)
        {
            // Check +s and -s together so ties go to the smaller shift.
            if (votes[H + s] > support) { support = votes[H + s]; best = s; }
            if (votes[H - s] > support) { support = votes[H - s]; best = -s; }
        }
        return best;
    }

    /// <summary>Writes the newly revealed rows of a frame that scrolled by <paramref name="shift"/>.</summary>
    void Place(int[] frame, int shift, int bands)
    {
        int header = StaticRows(bands, fromTop: true);
        int footer = StaticRows(bands, fromTop: false);
        if (header + footer + Math.Abs(shift) >= H) header = footer = 0;

        int newOffset = offset + shift;
        bool room = covBottom - covTop < MaxRows;

        if (room)
        {
            if (shift > 0)
            {
                // Scrolling down. If we're at the bottom of what we have, the old frame's footer is
                // sitting there: drop it, so the page underneath replaces it.
                if (covBottom <= offset + H)
                {
                    Forget(offset + H - footer, covBottom);
                    covBottom = Math.Min(covBottom, offset + H - footer);
                }
                covBottom = Math.Max(covBottom, newOffset + H - footer);
            }
            else
            {
                // Scrolling up: the same, mirrored, for a sticky header.
                if (covTop >= offset)
                {
                    Forget(covTop, offset + header);
                    covTop = Math.Max(covTop, offset + header);
                }
                covTop = Math.Min(covTop, newOffset + header);
            }

            int last = H - footer - 1;
            for (int y = header; y <= last; y++) Store(y + newOffset, frame, y, Math.Min(y - header, last - y));
        }

        offset = newOffset;
        hasScrolled = true;
    }

    /// <summary>
    /// Counts rows at the top (or bottom) that didn't change although the content scrolled:
    /// a sticky header/footer, or the browser's own toolbar. Single-colour rows count too: in the
    /// selected columns a footer is often just a plain bar. If some of them are really page
    /// background that happened to line up, the only cost is a few blank rows at the very end.
    /// </summary>
    int StaticRows(int bands, bool fromTop)
    {
        int count = 0;
        for (int step = 0; step < H; step++)
        {
            int y = fromTop ? step : H - 1 - step;
            for (int b = 0; b < bands; b++)
                if (hashRef[b * H + y] != hashNew[b * H + y]) return count;
            count = step + 1;
        }
        return count;
    }

    /// <summary>Keeps this view of a row if it's at least as central as the one we have.</summary>
    void Store(int documentRow, int[] frame, int y, int quality)
    {
        if (!rows.TryGetValue(documentRow, out var row))
        {
            row = new Row(W);
            rows[documentRow] = row;
        }
        if (quality < row.Quality) return;
        Array.Copy(frame, y * W, row.Pixels, 0, W);
        row.Quality = quality;
    }

    /// <summary>Marks rows as known to be wrong (an old sticky footer), so any later view replaces them.</summary>
    void Forget(int from, int to)
    {
        for (int r = from; r < to; r++)
            if (rows.TryGetValue(r, out var row)) row.Quality = -1;
    }

    void EnsureScratch(int n)
    {
        if (hashRef.Length >= n) return;
        hashRef = new ulong[n];
        hashNew = new ulong[n];
        flatRef = new bool[n];
        flatNew = new bool[n];
        chain = new int[n];
        refKey = (-1, -1, -1);
    }

    /// <summary>
    /// Hashes each row of each band. The low bits of every colour channel are dropped so tiny
    /// rendering differences don't break a match. "Flat" means the row is a single colour.
    /// </summary>
    void HashRows(int[] pixels, int x0, int x1, int bands, ulong[] hashes, bool[] flat)
    {
        const int Mask = 0xF8F8F8;
        int span = x1 - x0;
        for (int b = 0; b < bands; b++)
        {
            int bx0 = x0 + span * b / bands;
            int bx1 = x0 + span * (b + 1) / bands;
            for (int y = 0; y < H; y++)
            {
                int i = y * W + bx0, end = y * W + bx1;
                int first = pixels[i] & Mask;
                ulong hash = 14695981039346656037UL;
                bool same = true;
                for (; i < end; i++)
                {
                    int v = pixels[i] & Mask;
                    same &= v == first;
                    hash = (hash ^ (uint)v) * 1099511628211UL;
                }
                hashes[b * H + y] = hash;
                flat[b * H + y] = same;
            }
        }
    }

    public void Dispose() => grabber?.Dispose();
}
