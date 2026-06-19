namespace Tractus.HtmlToNdi.Chromium.Inject;

/// <summary>
/// The normalized, engine-agnostic recipe model (D-02). A flat external recipe JSON
/// <c>{urlMatch, css, js, targetSelector, fallbackPolicy, expectMotion}</c> (plus the optional
/// <c>expectsCrossOriginIframes</c> posture hint) is validated through <see cref="RecipeValidator"/>
/// and normalized into ONE instance of this record. It is the single internal shape both the
/// file-load path and the (Plan-04) <c>/recipe</c> HTTP body resolve to — nothing downstream
/// re-parses the raw JSON.
///
/// ENVELOPE-READY (D-02 / BACKLOG-INTEGRATION engine-agnostic constraint): this record is designed
/// to ADDITIVELY absorb a future structured <c>{prepare, drive, capture}</c> envelope (the deferred
/// Phase-5 Playwright adapter) WITHOUT a migration — new optional members are added, the flat fields
/// stay. For that reason it deliberately bakes in NO render-engine-specific or coordinate-click
/// concepts: it carries declarative data only. The fields below are the P1 flat shape; a later phase
/// adds the envelope members alongside them.
///
/// <para>P1 consumption note: <see cref="FallbackPolicy"/> and <see cref="ExpectMotion"/> are STORED
/// AS DATA ONLY in Phase 1 — nothing acts on them here; the Phase-2 monitor consumes them.
/// <see cref="ExpectsCrossOriginIframes"/> gates the D-03 site-isolation posture, consumed by Plan 04
/// at startup / swap time.</para>
/// </summary>
public sealed record Recipe
{
    /// <summary>
    /// REQUIRED. Host + optional path glob (D-05) — e.g. <c>example.com</c> or
    /// <c>example.com/live/*</c>. One recipe file per host; matched (never executed) via a tiny
    /// <c>*</c>→<c>.*</c> expansion in <see cref="RecipeStore"/>. Not a user-supplied regex.
    /// </summary>
    public required string UrlMatch { get; init; }

    /// <summary>
    /// Optional CSS payload. Belt-and-suspenders only — the live D-10 spike (b2) showed raw
    /// <c>&lt;style&gt;</c>-node injection may silently take no effect on a live page, so the
    /// load-bearing styling path is JS-driven (D-15). When present it must be non-empty and within
    /// the per-field size cap (D-19). Not syntax-checked here (D-19 — runtime failures are caught by
    /// Plan 05's <c>--inject-smoke</c>).
    /// </summary>
    public string? Css { get; init; }

    /// <summary>
    /// Optional JS payload — the robust DOM/style manipulation path (D-15). Authoring contract
    /// (D-02): the JS should be idempotent (guard via a recipe-specific global flag, D-24) and arm a
    /// DEBOUNCED <c>MutationObserver</c> so consent/CMP modals are re-dismissed and the target element
    /// re-isolated on every SPA re-render (the spike confirmed survival depends on this re-assert).
    /// When present it must be non-empty and within the per-field size cap (D-19). Not syntax-checked
    /// here (D-19).
    /// </summary>
    public string? Js { get; init; }

    /// <summary>Optional selector identifying the target element a recipe isolates/fills.</summary>
    public string? TargetSelector { get; init; }

    /// <summary>P1 DATA ONLY (consumed by the Phase-2 monitor). The fallback-graphic policy.</summary>
    public string? FallbackPolicy { get; init; }

    /// <summary>P1 DATA ONLY (consumed by the Phase-2 monitor). Whether the page is expected to be in
    /// motion (informs frozen-frame detection thresholds).</summary>
    public bool ExpectMotion { get; init; }

    /// <summary>
    /// Whether this recipe's target needs cross-origin iframe reach. Gates the D-03 site-isolation
    /// flags, which are FROZEN at <c>Cef.Initialize</c> (Plan 04). A swap to a recipe whose value
    /// differs from the launch posture is rejected (D-21) — see
    /// <see cref="RecipeStore.PostureMatches"/>.
    /// </summary>
    public bool ExpectsCrossOriginIframes { get; init; }
}
