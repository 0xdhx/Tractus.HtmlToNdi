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

    // ── D-13/D-24 LIVENESS-BEACON region (the by-value contract with InjectHook's injected canvas) ──
    //
    // The injected beacon (InjectHook.BuildPayload, BeaconSizePx) is a fixed 16x16 canvas pinned to the
    // TOP-LEFT corner (0,0) that draws OPAQUE-RGB content mutating every rAF tick over an alpha-0
    // background. The FrameMonitor samples the CENTER of that region off the copied straight front buffer
    // and compares consecutive ticks to derive beacon liveness (ticking / frozen / absent). These coords
    // are DUPLICATED BY VALUE (not by a shared reference — FrameMonitor is CEF/Inject-agnostic, D-02, and
    // must not import the Inject type) from InjectHook.BeaconSizePx; keep the two in sync.

    /// <summary>D-13: the beacon canvas X origin in the captured buffer (top-left corner placement).</summary>
    public const int BeaconOriginX = 0;

    /// <summary>D-13: the beacon canvas Y origin in the captured buffer (top-left corner placement).</summary>
    public const int BeaconOriginY = 0;

    /// <summary>D-13: the beacon canvas edge length (must match InjectHook.BeaconSizePx).</summary>
    public const int BeaconSizePx = 16;

    /// <summary>
    /// D-13: the per-channel RGB delta (summed over the sampled beacon pixels) above which the beacon
    /// counts as "ticking" (changed since the prior sample). The injected bar mutates R/G/B by
    /// tens-of-counts each tick, so a small bound cleanly separates a real tick from sampling noise while
    /// a frozen (identical) beacon reads 0. Tuned conservatively in Phase 3 if needed.
    /// </summary>
    public const int BeaconChangeBound = 6;

    /// <summary>
    /// D-13: the minimum summed OPAQUE-RGB energy (over the sampled beacon pixels) at/above which the
    /// beacon region is treated as PRESENT. Below it the region is ABSENT (never injected / failed to arm),
    /// which MUST degrade to the dHash + paint-age backstop and NEVER false-trip on "beacon absent" (D-13).
    /// A real beacon draws an opaque bar, so its RGB energy is well above 0.
    /// </summary>
    public const int BeaconPresenceFloor = 1;
}
