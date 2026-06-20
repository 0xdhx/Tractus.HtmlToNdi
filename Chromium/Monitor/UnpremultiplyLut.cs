namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// RC-3 / D-29d (MON-01): the O(1)/pixel per-channel un-premultiply lookup table — the named
/// contingency for the confirmed-Premultiplied CEF OnPaint buffer (02-UAT.md L1: half-alpha red read
/// back R≈128/A≈128 = premultiplied; the locked wire format is BGRA STRAIGHT per CLAUDE.md and
/// <see cref="AlphaConvention.Expected"/>). CEF's offscreen Skia compositor emits premultiplied BGRA;
/// there is no CefSharp.OffScreen setting that flips the OnPaint surface to straight
/// (<see cref="TransparentBrowserSettings"/>'s <c>BackgroundColor = 0x00000000u</c> is already set and
/// per 02-UAT.md does NOT change premult — verified, not a fix). So we convert in-process.
///
/// <para><b>Formula.</b> For a premultiplied channel value <c>c</c> at alpha <c>a</c>, the straight
/// (un-premultiplied) value is <c>a == 0 ? c : min(255, round(c * 255 / a))</c>:
/// <list type="bullet">
///   <item><c>a == 0</c> → PASSTHROUGH: a fully-transparent pixel's color is undefined and dividing by
///   zero is meaningless — the bytes are left as-is (never divide by zero).</item>
///   <item><c>a == 255</c> → IDENTITY: <c>round(c * 255 / 255) == c</c>.</item>
///   <item>half-alpha (<c>a == 128</c>): a premultiplied 50%-red <c>R=128</c> recovers to
///   <c>round(128 * 255 / 128) = 255</c> — the straight source color the <c>--onpaint-format</c> gate
///   asserts.</item>
/// </list>
/// </para>
///
/// <para><b>Cost.</b> A single 256×256 byte table (<see cref="Table"/>) built ONCE at static init —
/// indexed by <c>[alpha, premultChannelValue]</c>. Per-pixel conversion is then three array indexes
/// (B, G, R), no division, no branch, no allocation: the D-29d O(1)/pixel performance claim. The alpha
/// byte is shared between the straight and premultiplied conventions, so it is NEVER altered — only the
/// B, G, R bytes are rewritten.</para>
///
/// <para>CEF-agnostic by construction (no CefSharp type), mirroring the other Monitor pixel components
/// (<see cref="BlankDetector"/>, <see cref="DiffHash"/>) so it is unit-testable without CEF natives.</para>
/// </summary>
public static class UnpremultiplyLut
{
    /// <summary>
    /// The 256×256 straight-from-premultiplied table. <c>Table[alpha, premult]</c> gives the straight
    /// channel value. Row <c>alpha == 0</c> is the identity (passthrough — caller leaves alpha-0 pixels
    /// untouched anyway); row <c>alpha == 255</c> is the identity by the formula. Built once at static
    /// init via <see cref="Build"/>.
    /// </summary>
    private static readonly byte[,] Table = Build();

    private static byte[,] Build()
    {
        var table = new byte[256, 256];
        for (var a = 0; a < 256; a++)
        {
            for (var c = 0; c < 256; c++)
            {
                if (a == 0)
                {
                    // Fully-transparent: color is undefined, never divide by zero — passthrough.
                    table[a, c] = (byte)c;
                    continue;
                }

                // straight = min(255, round(premult * 255 / alpha)).
                var straight = (int)System.Math.Round(c * 255.0 / a, System.MidpointRounding.AwayFromZero);
                if (straight > 255)
                {
                    straight = 255;
                }

                table[a, c] = (byte)straight;
            }
        }

        return table;
    }

    /// <summary>
    /// Look up the straight channel value for a single premultiplied channel byte at the given alpha.
    /// O(1) — one array index, no division. <c>alpha == 0</c> returns <paramref name="premultChannel"/>
    /// unchanged (passthrough); <c>alpha == 255</c> returns it unchanged (identity).
    /// </summary>
    public static byte StraightChannel(byte alpha, byte premultChannel) => Table[alpha, premultChannel];

    /// <summary>
    /// Convert a premultiplied BGRA buffer to STRAIGHT alpha, writing into a caller-provided destination.
    /// Reads <paramref name="premultiplied"/> read-only (CEF's OnPaint buffer is callback-owned and may be
    /// reused — it is NEVER mutated here); writes only to <paramref name="destination"/>. The conversion is
    /// per-pixel: read A, look up straight B/G/R from the table, copy A through unchanged. Both buffers are
    /// tightly packed BGRA at <c>width * height * 4</c> bytes (stride == width*4, the contiguous CEF OnPaint
    /// surface). The bounded read/write covers exactly that extent (T-2-08-1).
    /// </summary>
    /// <param name="premultiplied">Source premultiplied BGRA bytes (read-only). Length must be ≥ width*height*4.</param>
    /// <param name="destination">Destination straight BGRA bytes (written). Length must be ≥ width*height*4.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    public static void Unpremultiply(
        System.ReadOnlySpan<byte> premultiplied,
        System.Span<byte> destination,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var byteCount = checked(width * height * 4);
        if (premultiplied.Length < byteCount || destination.Length < byteCount)
        {
            throw new System.ArgumentException(
                $"BGRA buffers must be at least {byteCount} bytes (width*height*4) for {width}x{height}.");
        }

        for (var off = 0; off < byteCount; off += 4)
        {
            // BGRA byte order. Alpha is the 4th byte and is shared by both conventions — copy it through.
            var b = premultiplied[off + 0];
            var g = premultiplied[off + 1];
            var r = premultiplied[off + 2];
            var a = premultiplied[off + 3];

            destination[off + 0] = Table[a, b];
            destination[off + 1] = Table[a, g];
            destination[off + 2] = Table[a, r];
            destination[off + 3] = a; // alpha NEVER altered.
        }
    }
}
