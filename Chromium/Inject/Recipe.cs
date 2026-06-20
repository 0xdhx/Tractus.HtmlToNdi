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

    /// <summary>
    /// The fallback-graphic policy keyword (D-20). Stored as <see cref="string"/> (the validator enforces
    /// the enum membership ∈ {<c>slate</c>, <c>black</c>, <c>hold-last</c>} — the same discipline as
    /// <see cref="UrlMatch"/> being a validator-checked string). Absent ⇒ the default <c>slate</c> is
    /// applied by the validator. The Phase-2 fallback path (Plan 04 <c>FallbackProvider</c>) selects
    /// behavior off this keyword: <c>slate</c> = the configured <see cref="FallbackAsset"/>/<c>slate.png</c>
    /// graphic (or a generated slate when absent/invalid), <c>black</c> = generated opaque black,
    /// <c>hold-last</c> = the monitor holds the last-good live frame.
    /// </summary>
    public string? FallbackPolicy { get; init; }

    /// <summary>
    /// OPTIONAL (D-33). The fallback ASSET filename resolved within <c>--fallback-dir</c> for a
    /// <see cref="FallbackPolicy"/>=<c>slate</c> recipe. Validated as a BARE filename only (no path
    /// separator, no <c>..</c>) by <see cref="RecipeValidator"/>; the on-disk path-traversal guard is
    /// re-applied at load in the <c>FallbackProvider</c> (defence-in-depth). When absent, convention is
    /// <c>slate.png</c>; a missing/invalid asset degrades to the in-memory generated default (D-20b).
    /// </summary>
    public string? FallbackAsset { get; init; }

    /// <summary>
    /// OPTIONAL/DEFAULTED (D-19). Milliseconds with no fresh paint after which the page is treated as
    /// frozen by the Plan-05 recovery state machine. Default <see cref="DefaultFreezeTimeoutMs"/> when
    /// absent. A DETECTION-TIMING knob (not a sensitivity threshold — those stay global, Plan 03).
    /// </summary>
    public int FreezeTimeoutMs { get; init; } = DefaultFreezeTimeoutMs;

    /// <summary>
    /// OPTIONAL/DEFAULTED (D-19). The minimum milliseconds the fallback must be HELD on air once entered
    /// (anti-flap min-hold, D-16). Default <see cref="DefaultMinHoldMs"/> when absent.
    /// </summary>
    public int MinHoldMs { get; init; } = DefaultMinHoldMs;

    /// <summary>
    /// OPTIONAL/DEFAULTED (D-19). Consecutive BAD frames required to ENTER fallback (hysteresis K-in,
    /// D-10/D-16). Default <see cref="DefaultHysteresisKIn"/> when absent.
    /// </summary>
    public int HysteresisKIn { get; init; } = DefaultHysteresisKIn;

    /// <summary>
    /// OPTIONAL/DEFAULTED (D-19). Consecutive GOOD frames required to EXIT fallback (hysteresis K-out,
    /// deliberately larger than K-in so recovery is conservative, D-10/D-16). Default
    /// <see cref="DefaultHysteresisKOut"/> when absent.
    /// </summary>
    public int HysteresisKOut { get; init; } = DefaultHysteresisKOut;

    /// <summary>Default <see cref="FreezeTimeoutMs"/> (~10 s) when the recipe omits it (D-10/D-16).</summary>
    public const int DefaultFreezeTimeoutMs = 10000;

    /// <summary>Default <see cref="MinHoldMs"/> (~2 s) when the recipe omits it (D-16 anti-flap).</summary>
    public const int DefaultMinHoldMs = 2000;

    /// <summary>Default <see cref="HysteresisKIn"/> (small — fast enter) when the recipe omits it.</summary>
    public const int DefaultHysteresisKIn = 3;

    /// <summary>Default <see cref="HysteresisKOut"/> (larger — conservative exit) when the recipe omits it.</summary>
    public const int DefaultHysteresisKOut = 10;

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
