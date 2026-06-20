using System.Text.Json;
using CefSharp;
using CefSharp.DevTools;
using CefSharp.OffScreen;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium.Inject;

/// <summary>
/// The document-start inject hook (D-15 / INJ-01 / INJ-03 / D-04 / D-17). Hosts the CDP
/// <c>Page.addScriptToEvaluateOnNewDocument</c> registration that runs the recipe payload at
/// document-start (before the page's own inline head scripts), tracks the returned
/// <c>ScriptIdentifier</c>, swaps recipes FAIL-CLOSED (remove-by-tracked-id → re-add — never old+new
/// together), and owns the fully-specified <see cref="RecreateBrowserAsync"/> (D-17) fail-closed
/// fallback used when a Remove fails.
///
/// <para><b>PERSISTENT DevToolsClient (spike-mandated, NOT create-use-dispose-per-call).</b> The D-10
/// live spike (<c>docs/research/01-inject-live-spike-decision-record.md</c>, § Schema / Build Impact)
/// EMPIRICALLY FALSIFIED the per-call <c>GetDevToolsClient()</c> lifecycle: a transient client disposed
/// after the Add tears down the document-start registration (scoped to that CDP session) BEFORE
/// navigation, so the injected script NEVER ARMS (uniform-NO probes while Add/Remove still report
/// success). The required design is ONE persistent client held for the browser's lifetime, with
/// <c>Page.enable</c> called once before the first Add, and the SAME client reused across
/// register → Load → /seturl swap → /recipe swap. The swap's Remove MUST target the same session that
/// did the Add (a Remove on a different client's id-space is meaningless — the spike's second bug). The
/// client is disposed + re-acquired ONLY inside <see cref="RecreateBrowserAsync"/> (recreating the
/// <c>ChromiumWebBrowser</c> invalidates the client), after which <c>Page.enable</c> + re-register run
/// again BEFORE Load.</para>
///
/// <para>The swap path the spike CONFIRMED clean is Remove-by-<c>.Identifier</c> → re-Add (the primary
/// path); <see cref="RecreateBrowserAsync"/> is the fail-CLOSED fallback on a Remove ERROR only, NOT
/// the default. Verified 148 member names (no re-check needed):
/// <c>AddScriptToEvaluateOnNewDocumentResponse.Identifier</c> (string) and
/// <c>RemoveScriptToEvaluateOnNewDocumentResponse.Success</c> (bool).</para>
/// </summary>
public sealed class InjectHook : IDisposable
{
    // The browser owner: InjectHook reaches the live ChromiumWebBrowser handle and the
    // recreate/re-wire orchestration through it (browser/paint/sink/smoke state lives in CefWrapper —
    // D-17 step 6 preserves it). InjectHook owns ONLY the CDP client + the tracked script id.
    private readonly CefWrapper owner;

    // D-10 spike mandate: ONE persistent DevToolsClient held across register → Load → swap. Disposed +
    // re-acquired ONLY in RecreateBrowserAsync. A per-call (using) client unregisters the doc-start
    // script on dispose → the script never arms (the falsified pattern (a)).
    private DevToolsClient? devClient;

    // Whether Page.enable has been issued on the CURRENT devClient. Reset whenever the client is
    // re-acquired (RecreateBrowserAsync), because enable is per-session.
    private bool pageEnabled;

    // D-04: the tracked ScriptIdentifier returned by the last successful Add. The swap removes THIS id
    // on THIS client before re-adding — never leaving old+new both active.
    private string? activeScriptId;

    public InjectHook(CefWrapper owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>
    /// Acquire the ONE persistent DevToolsClient (spike mandate) and issue <c>Page.enable</c> once. Call
    /// AFTER <c>WaitForInitialLoadAsync</c> (about:blank) and BEFORE the first
    /// <see cref="RegisterRecipeAsync"/> / first real Load. Idempotent: a no-op if the client is already
    /// held and enabled. Re-invoked by <see cref="RecreateBrowserAsync"/> after a fresh browser.
    /// </summary>
    public async Task EnsureClientAsync()
    {
        var browser = this.owner.Browser
            ?? throw new InvalidOperationException("InjectHook.EnsureClientAsync: browser is null");

        // Persistent client: acquire once and HOLD it (do NOT wrap in `using`). Disposing it would tear
        // down the document-start registration scoped to this CDP session (the spike's root-cause bug).
        this.devClient ??= browser.GetDevToolsClient();

        if (!this.pageEnabled)
        {
            // Page.enable ONCE before the first AddScriptToEvaluateOnNewDocumentAsync (spike mandate).
            await this.devClient.Page.EnableAsync();
            this.pageEnabled = true;
        }
    }

    /// <summary>
    /// D-15 / INJ-01 / INJ-03: register the recipe's document-start payload via
    /// <c>Page.AddScriptToEvaluateOnNewDocumentAsync</c> on the PERSISTENT client and store the returned
    /// <c>.Identifier</c> as <see cref="activeScriptId"/>. Call this BEFORE the corresponding Load
    /// (Pitfall 1: register-before-Load) so the FIRST real document is injected. A <c>null</c> recipe is
    /// a pass-through (D-07): nothing is registered, the page renders unmodified.
    /// </summary>
    public async Task RegisterRecipeAsync(Recipe? recipe)
    {
        if (recipe is null)
        {
            // D-07 pass-through: no recipe → inject nothing. The "no recipe for <host>" warning was
            // already logged by the store at Match time.
            this.activeScriptId = null;
            return;
        }

        await this.EnsureClientAsync();

        var payload = BuildPayload(recipe);
        var resp = await this.devClient!.Page.AddScriptToEvaluateOnNewDocumentAsync(payload);

        // Verified 148 member name (spike A1): AddScriptToEvaluateOnNewDocumentResponse.Identifier.
        this.activeScriptId = resp.Identifier;
        Log.Information("inject: registered doc-start script id={Id} for {UrlMatch}", this.activeScriptId, recipe.UrlMatch);
    }

    /// <summary>
    /// D-04 fail-closed recipe swap. (1) If a script id is tracked, Remove it on the SAME persistent
    /// client; (2) on a Remove FAILURE (verified member <c>.Success == false</c>) escalate to
    /// <see cref="RecreateBrowserAsync"/> — the fail-closed fallback that guarantees the stale script is
    /// gone, never running old+new together; (3) re-register the next recipe BEFORE Load. The actual
    /// Load(url) is driven by the caller (<see cref="CefWrapper.SwapRecipeAsync"/> /
    /// <see cref="CefWrapper.SetUrlAsync"/>) AFTER this returns, preserving register-before-Load ordering.
    /// </summary>
    public async Task SwapRecipeAsync(Recipe? next)
    {
        await this.EnsureClientAsync();

        if (this.activeScriptId is not null)
        {
            try
            {
                var removed = await this.devClient!.Page
                    .RemoveScriptToEvaluateOnNewDocumentAsync(this.activeScriptId);

                // Verified 148 member name (spike A2): RemoveScriptToEvaluateOnNewDocumentResponse.Success.
                if (!removed.Success)
                {
                    // FAIL CLOSED: the stale registration may still fire on the next document. Recreate
                    // the browser (D-17) so it CANNOT survive — never run old+new together (T-01-07).
                    Log.Warning("inject: Remove(id={Id}) returned Success=false — failing closed via RecreateBrowserAsync", this.activeScriptId);
                    await this.RecreateBrowserAsync(next);
                    return; // RecreateBrowserAsync already re-registered `next` before Load.
                }
            }
            catch (Exception ex)
            {
                // Any CDP error on Remove is also a fail-closed trigger (cannot prove the old script is gone).
                Log.Warning("inject: Remove(id={Id}) threw {Message} — failing closed via RecreateBrowserAsync", this.activeScriptId, ex.Message);
                await this.RecreateBrowserAsync(next);
                return;
            }

            this.activeScriptId = null;
        }

        // Primary clean path (spike-confirmed): re-Add on the same client BEFORE Load.
        await this.RegisterRecipeAsync(next);
    }

    /// <summary>
    /// D-17 — the fully-specified D-04 fail-closed fallback, run ONLY when a Remove fails (NOT the
    /// default swap path). Recreating the <c>ChromiumWebBrowser</c> invalidates the persistent
    /// DevToolsClient, so this is the ONE place the client is disposed + re-acquired. The checklist
    /// (each step documented inline):
    /// <list type="number">
    ///   <item>unsubscribe paint;</item>
    ///   <item>dispose the browser AND its DevTools client;</item>
    ///   <item>recreate the browser (SAME size, audio handler, D-13 anti-throttle flags are
    ///   process-global from Cef.Initialize so they persist; force-visible re-applied);</item>
    ///   <item>re-acquire a DevTools client + Page.enable (the old client is dead — the Gemini-flagged hazard);</item>
    ///   <item>re-register the doc-start script BEFORE Load;</item>
    ///   <item>preserve the IFrameSource/NdiFrameSink wiring + the smoke latch (do not orphan the sink
    ///   subscription or the SmokeMode/smokeFrameSent state).</item>
    /// </list>
    /// Steps 1–3 + 6 mutate browser/paint/sink/smoke state owned by <see cref="CefWrapper"/>, so they
    /// are delegated to <c>owner.RecreateBrowserCoreAsync</c> (which performs the dispose→recreate→re-wire
    /// while keeping the sink subscription + smoke latch). Steps 4–5 (the CDP re-acquire + re-register)
    /// are owned HERE, because the DevTools client + tracked script id are InjectHook's responsibility.
    /// </summary>
    public async Task RecreateBrowserAsync(Recipe? next)
    {
        Log.Warning("inject: RecreateBrowserAsync — disposing + recreating the browser (fail-closed swap fallback, D-17)");

        // Steps 1–3 + 6: CefWrapper owns the browser/paint/sink/smoke state. It unsubscribes paint,
        // disposes the old browser, recreates it (same size + audio handler + force-visible), and
        // RE-WIRES the same Paint handler — which preserves the IFrameSource/NdiFrameSink subscription
        // (the sink is subscribed to CefWrapper.FrameReady, not to the browser) and the smoke latch
        // (SmokeMode/smokeSent/smokeFrameSent live on CefWrapper and are untouched by the recreate).
        // It also disposes the DevTools client this hook holds (passed in below) so we don't leak it.
        this.DisposeClient(); // step 2 (client half): the old client is invalid after the browser dies.
        await this.owner.RecreateBrowserCoreAsync();

        // Step 4: the old client is dead — re-acquire a fresh persistent client + Page.enable.
        this.pageEnabled = false;
        this.activeScriptId = null;
        await this.EnsureClientAsync();

        // Step 5: re-register the doc-start script BEFORE the caller Loads — so the recreated browser's
        // first real document is injected (register-before-Load, restored after recreate).
        await this.RegisterRecipeAsync(next);

        // The caller (CefWrapper.SwapRecipeAsync) drives Load(url) AFTER this returns.
    }

    /// <summary>
    /// Builds the document-start payload: the recipe's <c>Js</c> wrapped inside the FIXED MutationObserver
    /// harness (D-02). The harness, executed at document-start (before the page's inline head scripts):
    /// <list type="bullet">
    ///   <item>sets the D-14 doc-start proof marker <c>window.__injectionSeenBeforeInlineHeadScript</c>
    ///   as its FIRST statement (the assertion surface for Plan 05's <c>--inject-smoke</c>);</item>
    ///   <item>is idempotent via the <c>window.__xpnInjected</c> guard flag;</item>
    ///   <item>appends a <c>&lt;style&gt;</c> element carrying <c>recipe.Css</c> from JS (there is NO CDP
    ///   inject-CSS verb — the spike (b2) showed raw &lt;style&gt; may take no effect on a live page, so
    ///   this is belt-and-suspenders; the load-bearing styling path is recipe.Js, D-15);</item>
    ///   <item>holds <c>recipe.Js</c> in an <c>apply()</c> closure and runs it once at document-start;</item>
    ///   <item>installs a DEBOUNCED MutationObserver on <c>document.documentElement</c>
    ///   (<c>{childList:true, subtree:true}</c>) that re-runs <c>apply()</c> on re-mount — INJ-03 SPA
    ///   soft-nav survival; the debounce (D-02) guards 60fps mutation-storm perf degradation.</item>
    /// </list>
    ///
    /// <para>AUTHORING CONTRACT (recipe.Js, enforced by Plan 05's synthetic fixture, NOT by this C#):
    /// the JS does consent-dismiss / chrome-hide / target-isolate-and-fill / <c>play()</c> via SELECTORS
    /// — NEVER coordinate-clicking (INJ-05 anti-feature boundary). This hook exposes no mouse/coordinate
    /// API.</para>
    /// </summary>
    /// <summary>
    /// D-13/D-24 BEACON GEOMETRY (the contract between the injected canvas here and the FrameMonitor
    /// beacon-region pre-key RGB sample). The beacon is a small FIXED-position canvas pinned to the
    /// TOP-LEFT corner at (0,0); the monitor samples pixel <c>(BeaconSampleX, BeaconSampleY)</c> — the
    /// CENTER of the canvas — off the copied straight front buffer. These constants are duplicated (by
    /// value, not by reference — FrameMonitor is CEF-agnostic and must not import this Inject type, D-02)
    /// in <c>MonitorDefaults.BeaconSampleX/Y</c>; keep them in sync. Corner (0,0): the canvas occupies real
    /// captured pixels (NOT display:none / visibility:hidden / 0-size / off-screen — all cull the paint,
    /// research §"Visibility gotchas"); it fills the FULL 16x16 with a UNIFORM low-alpha (alpha = 1/255 ≈
    /// 0.0039) color whose RGB MUTATES every rAF tick so Chromium damage-gating cannot optimize it away
    /// (research §16).
    ///
    /// <para><b>A=1 LOW-ALPHA BEACON (D-13 A4 contingency — the live Q2 gate REFUTED alpha-0 on CEF148).</b>
    /// The original design drew OPAQUE RGB over an alpha-0 (transparent) corner. The live --beacon-damage-check
    /// gate (research §19) proved that on CefSharp 148 the alpha-0 corner composites near-transparent and the
    /// un-premult LUT amplifies it to a CONSTANT near-white (~[227,243,249]) that MASKS the per-tick RGB
    /// mutation — no detectable change in the straight buffer the FrameMonitor samples. The A4 fix: fill the
    /// region UNIFORMLY at alpha = 1/255 with mutating RGB. At ~0.4% opacity the beacon still keys out
    /// ~invisibly on air under the XPression straight-alpha key, but a PRESENT low-alpha region's RGB is
    /// amplified by the un-premult LUT into large (0↔255-scale) pre-key swings → the per-tick mutation becomes
    /// DETECTABLE. Keys-out-invisible AND amplified-detectable — the property alpha-0 lacked.</para>
    /// </summary>
    private const int BeaconSizePx = 16;

    private static string BuildPayload(Recipe recipe)
    {
        // JSON-encode the CSS and JS so arbitrary recipe content embeds safely inside the JS literal
        // (no string-break / injection-into-the-harness). These are DATA, not interpolated code.
        var cssLiteral = JsonSerializer.Serialize(recipe.Css ?? string.Empty);
        // recipe.Js is executed as a function body inside apply(); embed it as a string and run it via
        // the Function constructor so a syntax error in the recipe cannot break the harness's own parse
        // (it throws at apply()-time, caught + logged, instead of voiding the whole document-start script).
        var jsLiteral = JsonSerializer.Serialize(recipe.Js ?? string.Empty);

        // D-18/D-03: the target selector the __xpnTargetPresent stanza resolves to prove the target is
        // present + isolated (full-bleed). JSON-encoded as DATA (queried via document.querySelector, never
        // interpolated as code). Empty when the recipe declares no targetSelector → targetPresent stays
        // false (the proof marker correctly reports "no target to isolate").
        var targetSelectorLiteral = JsonSerializer.Serialize(recipe.TargetSelector ?? string.Empty);

        // The debounce interval (ms) for the MutationObserver re-assert (D-02 — guards 60fps storms).
        const int DebounceMs = 50;

        // D-15: the BEACON stanza is composed into the payload ONLY when recipe.ExpectMotion is true — a
        // SERVER-SIDE C# branch (NOT a JS runtime check), so a static page's payload carries no beacon at
        // all (monitor infrastructure, not per-recipe boilerplate). The canvas fills the full corner with a
        // UNIFORM alpha=1/255 color whose RGB mutates each rAF tick (D-13 A4); the rAF loop is guarded by the
        // distinct global window.__xpnBeaconArmed (the __xpnInjected idempotency pattern, L240-242) so the
        // observer re-running apply() never starts a SECOND loop. NO mouse/coordinate-click primitive (D-17).
        var beaconStanza = recipe.ExpectMotion
            ? $$"""
                // D-13 LIVENESS BEACON (expectMotion=true ONLY — composed server-side). A {{BeaconSizePx}}x{{BeaconSizePx}}
                // fixed canvas at the top-left corner. A=1 LOW-ALPHA design (D-13 A4 contingency): the live Q2
                // --beacon-damage-check gate REFUTED the original alpha-0 beacon on CefSharp 148 — the alpha-0
                // corner composited to a CONSTANT un-premult-amplified near-white (~[227,243,249]) that masked
                // the mutation. The fix: fill the FULL canvas UNIFORMLY at alpha = 1/255 (≈0.4% opacity) with
                // RGB mutating each rAF tick. At ~0.4% opacity it keys out ~invisibly on air (XPression
                // straight-alpha key), yet a PRESENT low-alpha region's RGB is amplified by the un-premult LUT
                // into large pre-key swings the FrameMonitor samples → mutation DETECTABLE (research §16/§19).
                // Guarded by __xpnBeaconArmed so the observer's apply() re-fire never double-arms the rAF loop.
                function __xpnArmBeacon() {
                    if (window.__xpnBeaconArmed) { return; }
                    try {
                        var c = document.getElementById('__xpn_beacon');
                        if (!c) {
                            c = document.createElement('canvas');
                            c.id = '__xpn_beacon';
                            c.width = {{BeaconSizePx}};
                            c.height = {{BeaconSizePx}};
                            var s = c.style;
                            // Real captured pixels: fixed top-left, NOT display:none/visibility:hidden/0-size/
                            // off-screen (all cull the paint). z-index keeps it in the composited layer.
                            s.setProperty('position', 'fixed', 'important');
                            s.setProperty('left', '0', 'important');
                            s.setProperty('top', '0', 'important');
                            s.setProperty('width', '{{BeaconSizePx}}px', 'important');
                            s.setProperty('height', '{{BeaconSizePx}}px', 'important');
                            s.setProperty('margin', '0', 'important');
                            s.setProperty('padding', '0', 'important');
                            s.setProperty('pointer-events', 'none', 'important');
                            s.setProperty('z-index', '2147483647', 'important');
                            // CSS background stays transparent — the per-tick canvas FILL below paints the
                            // load-bearing pixels (uniform alpha=1/255 RGB), which the un-premult LUT amplifies
                            // into the changing pre-key color the monitor samples (D-13 A4; keyed out on air).
                            s.setProperty('background', 'transparent', 'important');
                            (document.body || document.documentElement).appendChild(c);
                        }
                        var ctx = c.getContext('2d');
                        if (!ctx) { return; }
                        window.__xpnBeaconArmed = true;
                        var __xpnBeaconTick = 0;
                        function __xpnBeaconDraw() {
                            try {
                                __xpnBeaconTick = (__xpnBeaconTick + 1) & 0xff;
                                // D-13 A4: clear, then fill the FULL canvas UNIFORMLY at alpha = 1/255 with RGB
                                // mutating each tick. NOT alpha-0 (A4-refuted: composited to a constant near-white
                                // that masked the mutation) and NOT alpha-1.0/a-partial-bar. The uniform low-alpha
                                // region is PRESENT, keys out ~invisibly on air (~0.4% opacity), and the un-premult
                                // LUT amplifies its mutating RGB into a detectable pre-key swing (research §16/§19).
                                ctx.clearRect(0, 0, {{BeaconSizePx}}, {{BeaconSizePx}});
                                var r = (__xpnBeaconTick * 7) & 0xff;
                                var g = (__xpnBeaconTick * 13) & 0xff;
                                var b = (__xpnBeaconTick * 29) & 0xff;
                                ctx.fillStyle = 'rgba(' + r + ',' + g + ',' + b + ',' + (1 / 255) + ')';
                                ctx.fillRect(0, 0, {{BeaconSizePx}}, {{BeaconSizePx}});
                            } catch (e) {}
                            window.requestAnimationFrame(__xpnBeaconDraw);
                        }
                        window.requestAnimationFrame(__xpnBeaconDraw);
                    } catch (e) {}
                }

        """
            : "// (beacon stanza omitted — recipe.ExpectMotion=false, D-15)";

        // D-15: the beacon arm CALL — also gated server-side so a non-motion payload never references the
        // function. Placed inside apply() after the user fn so it re-arms (idempotently) under the observer.
        var beaconApplyCall = recipe.ExpectMotion
            ? "__xpnArmBeacon();"
            : "/* no beacon (expectMotion=false, D-15) */";

        return $$"""
        (function () {
            // D-14 proof marker — FIRST statement, set before the page's own inline head scripts run.
            // This is the assertion surface Plan 05's --inject-smoke reads to prove document-start timing.
            try { window.__injectionSeenBeforeInlineHeadScript = true; } catch (e) {}

            // Idempotent guard (D-24): the doc-start script can be (re-)registered; never double-arm.
            if (window.__xpnInjected) { return; }
            window.__xpnInjected = true;

            var __xpnCss = {{cssLiteral}};
            var __xpnJsSrc = {{jsLiteral}};
            // D-03/D-18: the recipe's target selector (DATA, queried — never interpolated as code). Empty
            // string ⇒ no target declared ⇒ __xpnTargetPresent stays false.
            var __xpnTargetSelector = {{targetSelectorLiteral}};

            {{beaconStanza}}

            // Belt-and-suspenders <style> shim (NOT the load-bearing path — spike (b2)). Appended from
            // JS because there is no CDP inject-CSS-at-document-start verb.
            function __xpnApplyCss() {
                if (!__xpnCss) { return; }
                try {
                    var existing = document.getElementById('__xpn_style');
                    if (existing) { existing.textContent = __xpnCss; return; }
                    var style = document.createElement('style');
                    style.id = '__xpn_style';
                    style.textContent = __xpnCss;
                    (document.head || document.documentElement).appendChild(style);
                } catch (e) {}
            }

            // recipe.Js held in an apply() closure (D-15 load-bearing path): consent-dismiss / chrome-hide
            // / target-isolate-and-fill / play() via SELECTORS. Compiled once via Function so a recipe
            // syntax error surfaces here, not as a harness parse failure.
            var __xpnUserFn = null;
            try { __xpnUserFn = new Function(__xpnJsSrc); } catch (e) { __xpnUserFn = null; }

            // D-03 TARGET-PRESENT PROOF MARKER (re-evaluated EACH apply() — NOT behind the one-time guard;
            // it is a per-tick re-evaluation, Test 3). Sets window.__xpnTargetPresent true when the recipe's
            // target selector resolves AND the element is isolated/full-bleed (non-zero rendered box). This is
            // observability ONLY (D-14): it is surfaced on /health but NEVER wired into UseFallback (it strobes
            // on SPA re-render). The flag is READ by the SIBLING CDP probe composed at /health (Program.cs,
            // Task 2 Part D) — NOT by FrameMonitor (which stays IFrameSource-only, D-24). NO coordinate API.
            function __xpnEvalTargetPresent() {
                try {
                    if (!__xpnTargetSelector) { window.__xpnTargetPresent = false; return; }
                    var t = document.querySelector(__xpnTargetSelector);
                    if (!t) { window.__xpnTargetPresent = false; return; }
                    // "isolated" = the target has a real rendered box (it was found AND laid out). A
                    // getBoundingClientRect with non-zero area is the cheap, layout-shift-robust proof.
                    var rect = t.getBoundingClientRect();
                    window.__xpnTargetPresent = !!(rect && rect.width > 0 && rect.height > 0);
                } catch (e) { window.__xpnTargetPresent = false; }
            }

            function apply() {
                __xpnApplyCss();
                if (__xpnUserFn) { try { __xpnUserFn(); } catch (e) {} }
                // D-03: re-evaluate the target-present proof marker every apply() (per-tick, NOT guarded).
                __xpnEvalTargetPresent();
                // D-13/D-15: (re-)arm the liveness beacon — idempotent via __xpnBeaconArmed (no-op on the
                // expectMotion=false payload, where the call expands to a comment).
                {{beaconApplyCall}}
            }

            // DEBOUNCED MutationObserver (D-02 / INJ-03) — re-assert on SPA re-render / consent re-mount.
            // The spike confirmed survival across history.pushState soft-nav depends on THIS re-assert
            // (the doc-start script itself does not re-fire without a new document). Debounce guards a
            // continuously-mutating page from a 60fps re-apply storm.
            var __xpnTimer = null;
            function __xpnSchedule() {
                if (__xpnTimer) { return; }
                __xpnTimer = setTimeout(function () { __xpnTimer = null; apply(); }, {{DebounceMs}});
            }

            function __xpnArmObserver() {
                try {
                    // At document-start the document is EMPTY — document.documentElement is still null
                    // (the <html> element has not been parsed yet). Arming on the null documentElement
                    // (the prior `if (!target) return`) left the observer UN-armed, so apply() ran exactly
                    // once on the empty document and NEVER re-fired — every element-gated recipe action
                    // (target isolate/fill, play(), and the INJ-03 consent re-dismiss) silently no-op'd.
                    // Observe `document` (always present at document-start) with subtree:true so the observer
                    // catches the later-parsed <html>/<body> and re-runs apply() once those actions are
                    // possible. (Caught by the D-14 --inject-smoke fixture: INJ-05 targetFilled/playStarted.)
                    var target = document.documentElement || document;
                    var obs = new MutationObserver(__xpnSchedule);
                    obs.observe(target, { childList: true, subtree: true });
                } catch (e) {}
            }

            // First apply at document-start, then arm the persistent observer.
            apply();
            __xpnArmObserver();
        })();
        """;
    }

    private void DisposeClient()
    {
        try { this.devClient?.Dispose(); } catch { /* best-effort */ }
        this.devClient = null;
        this.pageEnabled = false;
    }

    public void Dispose()
    {
        this.DisposeClient();
    }
}
