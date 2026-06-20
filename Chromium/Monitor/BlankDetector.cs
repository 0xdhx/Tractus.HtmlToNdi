namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// D-08: which blank/abnormal branch fired, so the Plan-05 state machine and the Plan-06 <c>/health</c>
/// <c>fallbackReason</c> can report WHY a frame was held. <see cref="None"/> = the frame is not blank.
/// </summary>
public enum BlankReason
{
    /// <summary>The frame is content-bearing — not blank.</summary>
    None = 0,

    /// <summary>
    /// Near-uniform luminance (variance below the global epsilon): all-black, all-white, OR a solid
    /// non-black error/consent page. The "abnormal" case folds in here per D-08 — no separate detector.
    /// </summary>
    LowVariance,

    /// <summary>
    /// Effectively all sampled pixels are transparent (alpha ≈ 0). Straight-alpha "blank" is fully
    /// transparent, NOT black, so this catches the on-air-invisible frame the luma-only check misses.
    /// </summary>
    AllAlphaZero,
}

/// <summary>
/// D-08: the blank/abnormal detection result. <see cref="IsBlank"/> is the actionable boolean;
/// <see cref="Reason"/> distinguishes the two branches for reporting.
/// </summary>
public readonly record struct BlankResult(bool IsBlank, BlankReason Reason)
{
    /// <summary>A not-blank result (content-bearing frame).</summary>
    public static readonly BlankResult NotBlank = new(false, BlankReason.None);
}

/// <summary>
/// D-08: the pure, CEF-agnostic blank/abnormal detector. EXTENDS (does not replace) the
/// <c>CefWrapper.IsBlankBuffer</c> sampler: it lifts the same sparse-sample shape
/// (<c>step = max(1, totalPixels / 4096)</c>) and the same Rec.601 luma coefficients, then adds the two
/// D-08 branches the luma-only original lacks:
/// <list type="number">
///   <item>a LOW-VARIANCE branch — near-uniform luminance flags blank/abnormal (a solid error/consent
///   page "abnormal" folds in here, NOT a third detector); and</item>
///   <item>an ALL-ALPHA-0 branch — straight-alpha "blank" is transparent, not black, so an RGB-only
///   variance check misses it; sampling the alpha byte is the unambiguous signal.</item>
/// </list>
/// The sensitivity thresholds live in <see cref="MonitorDefaults"/> as conservative GLOBAL defaults
/// (D-10/D-19), not per-recipe fields. No CefSharp import — unit-tested on synthetic frames.
/// </summary>
public static class BlankDetector
{
    /// <summary>
    /// Analyze a BGRA frame for the two D-08 blank conditions. The all-alpha-0 branch is checked FIRST
    /// (an on-air-invisible frame is blank regardless of its RGB content); the low-variance branch
    /// (abnormal folded in) is checked second.
    /// </summary>
    /// <param name="bgra">The BGRA pixel buffer (B,G,R,A byte order, 4 bytes/pixel).</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="stride">Bytes per row (≥ width*4).</param>
    /// <param name="varianceEpsilon">Global low-variance threshold (default <see cref="MonitorDefaults.BlankVarianceEpsilon"/>).</param>
    /// <param name="alphaZeroFraction">Global transparent-sample fraction (default <see cref="MonitorDefaults.BlankAlphaZeroFraction"/>).</param>
    public static BlankResult Analyze(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        double varianceEpsilon = MonitorDefaults.BlankVarianceEpsilon,
        double alphaZeroFraction = MonitorDefaults.BlankAlphaZeroFraction)
    {
        if (bgra.IsEmpty || width <= 0 || height <= 0 || stride < width * 4)
        {
            // A degenerate frame is treated as blank (same fail-safe posture as IsBlankBuffer).
            return new BlankResult(true, BlankReason.LowVariance);
        }

        const int bytesPerPixel = 4; // BGRA
        long totalPixels = (long)width * height;
        // The exact IsBlankBuffer sparse-sample shape: ~4096 pixels evenly across the frame.
        long step = Math.Max(1, totalPixels / 4096);

        long sampleCount = 0;
        long alphaZeroCount = 0;
        // Welford-free two-pass-in-one: accumulate sum and sum-of-squares of luma for the variance.
        double lumaSum = 0;
        double lumaSqSum = 0;

        for (long i = 0; i < totalPixels; i += step)
        {
            // Map the linear pixel index back through the (possibly padded) stride.
            long y = i / width;
            long x = i % width;
            long off = y * stride + x * bytesPerPixel;

            int b = bgra[(int)(off + 0)];
            int g = bgra[(int)(off + 1)];
            int r = bgra[(int)(off + 2)];
            int a = bgra[(int)(off + 3)];

            // Rec.601 luma — the SAME coefficients as CefWrapper.IsBlankBuffer.
            int luma = (299 * r + 587 * g + 114 * b) / 1000;
            lumaSum += luma;
            lumaSqSum += (double)luma * luma;

            if (a <= MonitorDefaults.AlphaZeroThreshold)
            {
                alphaZeroCount++;
            }

            sampleCount++;
        }

        if (sampleCount == 0)
        {
            return new BlankResult(true, BlankReason.LowVariance);
        }

        // (1) ALL-ALPHA-0 branch first — an on-air-invisible frame is blank no matter its RGB.
        if ((double)alphaZeroCount / sampleCount >= alphaZeroFraction)
        {
            return new BlankResult(true, BlankReason.AllAlphaZero);
        }

        // (2) LOW-VARIANCE branch — near-uniform luminance (abnormal folds in here).
        double mean = lumaSum / sampleCount;
        double variance = (lumaSqSum / sampleCount) - (mean * mean);
        if (variance < 0)
        {
            variance = 0; // floating-point guard
        }

        if (variance <= varianceEpsilon)
        {
            return new BlankResult(true, BlankReason.LowVariance);
        }

        return BlankResult.NotBlank;
    }
}
