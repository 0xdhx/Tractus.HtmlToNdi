using System.Numerics;

namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// D-07: the pure, CEF-agnostic content-freeze primitive — the canonical dHash (difference hash;
/// Krawetz) over a downsampled grayscale view of a BGRA frame, plus the Hamming distance between two
/// hashes. This file is the PRIMITIVE only: no windowing ring, no <c>expectMotion</c> gating, no timer
/// — those live in the Plan-05 state machine, which calls <see cref="Compute"/> on a timer and compares
/// the result across a window with <see cref="Hamming"/>.
///
/// <para>Why dHash over SAD (the D-07 rationale): dHash is invariant to brightness/contrast shifts and
/// tolerant of sub-cell noise, so anti-aliasing shimmer and a moving cursor do NOT register as a change
/// — preventing the false "not frozen" where SAD sees AA jitter and declares a frozen page live. It is
/// also allocation-free and ~microseconds per frame.</para>
///
/// <para>NO CefSharp import — this operates purely on a BGRA span + dims and is unit-tested on synthetic
/// frames with no render surface, so a future Phase-5 frame source can reuse it unchanged.</para>
/// </summary>
public static class DiffHash
{
    // Canonical dHash grid: sample to 9 wide x 8 tall grayscale (72 cells). The extra column gives
    // 8 horizontal left>right comparisons per row x 8 rows = 64 bits, packed into a ulong.
    private const int GridW = 9;
    private const int GridH = 8;

    /// <summary>
    /// Compute the 64-bit dHash of a BGRA frame. Box-averages the frame down to a 9x8 Rec.601 grayscale
    /// grid, then for each row sets bit = 1 where the left cell's luma &gt; its right neighbour's. The
    /// downsample scratch is a 72-byte <c>stackalloc</c> — no per-call heap allocation on the hot path.
    /// </summary>
    /// <param name="bgra">The BGRA pixel buffer (B,G,R,A byte order, 4 bytes/pixel).</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="stride">Bytes per row (≥ width*4; lets a padded buffer be hashed correctly).</param>
    /// <returns>The 64-bit difference hash; 0 for a degenerate/empty frame.</returns>
    public static ulong Compute(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (bgra.IsEmpty || width <= 0 || height <= 0 || stride < width * 4)
        {
            return 0UL;
        }

        // Rec.601 luma per cell, scaled x1000 to stay integer through the box average. Max cell luma is
        // 255*1000 = 255000, x (cell pixel count) — a long accumulator avoids overflow on large frames.
        Span<int> grid = stackalloc int[GridW * GridH];

        for (int gy = 0; gy < GridH; gy++)
        {
            // Source row span [y0, y1) covered by this grid row.
            int y0 = (int)((long)gy * height / GridH);
            int y1 = (int)((long)(gy + 1) * height / GridH);
            if (y1 <= y0)
            {
                y1 = y0 + 1;
            }

            for (int gx = 0; gx < GridW; gx++)
            {
                int x0 = (int)((long)gx * width / GridW);
                int x1 = (int)((long)(gx + 1) * width / GridW);
                if (x1 <= x0)
                {
                    x1 = x0 + 1;
                }

                long sum = 0;
                long count = 0;
                for (int y = y0; y < y1 && y < height; y++)
                {
                    int rowBase = y * stride;
                    for (int x = x0; x < x1 && x < width; x++)
                    {
                        int off = rowBase + x * 4;
                        // Rec.601 luma from BGRA — the SAME coefficients as CefWrapper.IsBlankBuffer:
                        // (299*R + 587*G + 114*B) / 1000. Kept x1000 here (divide once after the average).
                        int b = bgra[off + 0];
                        int g = bgra[off + 1];
                        int r = bgra[off + 2];
                        sum += 299 * r + 587 * g + 114 * b;
                        count++;
                    }
                }

                grid[gy * GridW + gx] = count > 0 ? (int)(sum / count) : 0;
            }
        }

        ulong hash = 0UL;
        int bit = 0;
        for (int row = 0; row < GridH; row++)
        {
            for (int col = 0; col < GridH; col++, bit++)
            {
                int i = row * GridW + col;
                if (grid[i] > grid[i + 1])
                {
                    hash |= 1UL << bit;
                }
            }
        }

        return hash;
    }

    /// <summary>
    /// The Hamming distance between two dHashes — the number of differing bits, via the single-intrinsic
    /// <see cref="BitOperations.PopCount"/> (NOT a hand-rolled bit-counting loop, per the Don't-Hand-Roll
    /// guidance). A distance ≤ <see cref="MonitorDefaults.FreezeHammingBound"/> means "effectively
    /// identical" — a freeze candidate when the whole comparison window agrees (state machine, Plan 05).
    /// </summary>
    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
