namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// D-10 / D-19: the GLOBAL, conservative detection-sensitivity defaults for the Phase-2 frame monitor.
/// These are deliberately NOT per-recipe fields — D-19 keeps the sensitivity knobs (the dHash freeze
/// Hamming bound, the blank/abnormal luma-variance epsilon, the all-alpha-0 sample fraction) global so
/// the risky Phase-3 empirical tuning window churns ONE place, not every recipe. The per-recipe TIMING
/// fields (freezeTimeoutMs / minHoldMs / hysteresis) are owned separately by Plan 04.
///
/// <para>Every value here is a SHIP-CONSERVATIVE starting point (D-10) — it is correct in shape and
/// safe (a static page must never false-trip), but the exact numbers are tuned against live pages in
/// Phase 3. The state machine (Plan 05) reads these; the pure detectors (DiffHash / BlankDetector) take
/// the relevant bound as a parameter-defaulted argument so they stay independently unit-testable.</para>
/// </summary>
public static class MonitorDefaults
{
    /// <summary>
    /// D-07 / D-10 / D-19: the global Hamming-distance bound under which two consecutive dHashes count
    /// as "effectively identical" (a freeze candidate). The canonical dHash literature uses ≤ ~3 over a
    /// 64-bit hash; we ship that conservative default (small bound = only a genuinely static frame trips,
    /// AA/cursor noise tolerated by the hash itself). Tuned in Phase 3.
    /// </summary>
    public const int FreezeHammingBound = 3;

    /// <summary>
    /// D-08 / D-10 / D-19: the global luma-variance epsilon under which a frame's sparse-sampled
    /// luminance is "near-uniform" (blank/abnormal — all-black, all-white, or a solid error/consent
    /// page). Variance is over the 0..255 Rec.601 luma scale; a flat fill has variance 0, real content
    /// is in the hundreds-to-thousands. The conservative default flags only genuinely flat frames; the
    /// precise epsilon is tuned against live pages in Phase 3.
    /// </summary>
    public const double BlankVarianceEpsilon = 12.0;

    /// <summary>
    /// D-08 / D-10 / D-19: the fraction (0..1) of sparse-sampled pixels that must read alpha ≈ 0 for the
    /// all-alpha-0 (transparent-blank) branch to fire. Straight-alpha "blank" is fully transparent, not
    /// black, so a near-total transparent sample is the unambiguous signal the luma-only check misses.
    /// Conservative high default (almost every sample must be transparent) — tuned in Phase 3.
    /// </summary>
    public const double BlankAlphaZeroFraction = 0.98;

    /// <summary>
    /// D-08: the per-sample alpha threshold (0..255) below which a sampled pixel counts as "transparent"
    /// for the all-alpha-0 branch. A small non-zero tolerance absorbs dithering/rounding around full
    /// transparency without admitting genuinely opaque pixels.
    /// </summary>
    public const byte AlphaZeroThreshold = 4;
}
