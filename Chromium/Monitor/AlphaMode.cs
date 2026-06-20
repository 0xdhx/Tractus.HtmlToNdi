namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// D-22 / D-29d (MON-01): the alpha convention the fork's CEF OnPaint buffer actually uses, as
/// determined by the <c>--onpaint-format</c> readback gate. This is the SINGLE shared fact that every
/// downstream pixel consumer must agree on:
/// <list type="bullet">
///   <item>Plan 02-02's frame pump (the per-frame copy + NDI send convention),</item>
///   <item>Plan 02-03's blank/freeze detectors (luma + content-delta over the buffer),</item>
///   <item>Plan 02-04's fallback graphic generator (the fallback's alpha convention MUST match the
///   live convention — RESEARCH Pattern 6 / D-21 — or the keyer fringes at the fallback edge),</item>
///   <item>Phase-3 keying (the straight-vs-premultiplied keyer correction, if needed).</item>
/// </list>
///
/// <para><see cref="Straight"/> = the color channels (B/G/R) are NOT scaled by alpha — a 50%-alpha
/// pure red reads back as R≈255, A≈128. This is what NDI <c>FourCC_type_BGRA</c> means and what
/// CLAUDE.md asserts is "locked." <see cref="Premultiplied"/> = the color channels ARE scaled by
/// alpha — the same pixel reads back as R≈128, A≈128 (RESEARCH Pitfall P-1: CEF offscreen OnPaint is
/// frequently premultiplied). Over black the two are indistinguishable (likely why the spike passed);
/// over a known semi-transparent color they diverge — which is exactly what the gate measures.</para>
/// </summary>
public enum AlphaMode
{
    /// <summary>Color channels are NOT pre-scaled by alpha (NDI BGRA straight). The locked assumption.</summary>
    Straight,

    /// <summary>Color channels ARE pre-scaled by alpha (CEF offscreen default — Pitfall P-1). Triggers
    /// the D-29d un-premultiply contingency before any downstream pixel math.</summary>
    Premultiplied,
}

/// <summary>
/// D-22: the single compile-time EXPECTED alpha mode the project is built against (BGRA straight,
/// per CLAUDE.md "straight-alpha locked" and the NDI <c>FourCC_type_BGRA</c> contract). The
/// <c>--onpaint-format</c> gate COMPARES the runtime-observed mode against this constant and FAILS
/// LOUDLY on drift (the compile-time-expected-vs-runtime-observed contract): a future CEF bump that
/// silently flips the OnPaint buffer to premultiplied trips the gate (and CI) instead of silently
/// corrupting the on-air keyed image. If the observed mode is <see cref="AlphaMode.Premultiplied"/>,
/// the gate records the D-29d un-premult contingency (a 256-entry per-channel un-premultiply LUT, or a
/// CEF straight-alpha command-line flag) BEFORE any downstream pixel code is written against the wrong
/// assumption — the keying CORRECTION itself is the Phase-3 follow-up (the gate does NOT change the
/// send path).
/// </summary>
public static class AlphaConvention
{
    /// <summary>The locked, project-wide expected OnPaint alpha mode (BGRA straight).</summary>
    public const AlphaMode Expected = AlphaMode.Straight;
}
