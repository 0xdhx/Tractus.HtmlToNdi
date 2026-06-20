
using CefSharp;
using CefSharp.OffScreen;
using Serilog;
using Tractus.HtmlToNdi.Chromium.Inject;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable, IFrameSource
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    /// <summary>
    /// D-16: the start URL is STORED here (not passed to the ctor) so the browser is constructed with
    /// <c>about:blank</c> and the first REAL navigation is deferred until AFTER the doc-start script is
    /// registered (register-before-Load — Pitfall 1). The current upstream ctor auto-loaded
    /// <c>initialUrl</c> immediately, which made "register before the first Load" impossible.
    /// </summary>
    private readonly string startUrl;

    /// <summary>
    /// D-15/D-04: the inject hook owning the persistent DevToolsClient + the tracked doc-start script id
    /// + the fail-closed swap. Constructed in <see cref="InitializeWrapperAsync"/> (needs the live
    /// browser). The startup recipe to register before the first Load is set via
    /// <see cref="StartupRecipe"/>.
    /// </summary>
    private InjectHook? injectHook;

    /// <summary>
    /// The recipe resolved at startup (D-16 two-phase resolution, set by Program before
    /// <see cref="InitializeWrapperAsync"/>). May be <c>null</c> (D-07 pass-through). Becomes the initial
    /// <see cref="CurrentRecipe"/>.
    /// </summary>
    public Recipe? StartupRecipe { get; set; }

    /// <summary>
    /// The recipe currently driving injection (consumed by the <c>/recipe</c> GET endpoint, Plan 04
    /// Task 3). Updated by <see cref="SwapRecipeAsync"/>. <c>null</c> = pass-through (no injection).
    /// </summary>
    public Recipe? CurrentRecipe { get; private set; }

    /// <summary>
    /// The live browser handle (D-17 — <see cref="InjectHook"/> reaches the DevTools client through it).
    /// </summary>
    internal ChromiumWebBrowser? Browser => this.browser;

    private Thread RenderWatchdog;

    /// <summary>
    /// 03-04 ([[cef-offscreen-needs-external-beginframe]]): the ~60fps external begin-frame DRIVER. With
    /// <see cref="WindowInfo.ExternalBeginFrameEnabled"/> set at CreateBrowser, CEF produces NO frames (no
    /// OnPaint at all) unless <c>SendExternalBeginFrame</c> is pumped — and that externally driven present
    /// cadence is what advances render-gated WebGL players (the MapLibre radar) that CEF's INTERNAL
    /// windowless begin-frame leaves frozen. Constructed in the ctor, started in
    /// <see cref="InitializeWrapperAsync"/>, exits on <see cref="disposedValue"/> — same lifecycle as
    /// <see cref="RenderWatchdog"/>. Reads <see cref="browser"/> fresh each tick, so a
    /// <see cref="RecreateBrowserCoreAsync"/> swap is driven automatically with NO thread recreation.
    /// </summary>
    private Thread BeginFrameDriver;
    private DateTime lastPaint = DateTime.MinValue;

    /// <summary>
    /// IFrameSource liveness clock (D-25) — surfaces the existing <see cref="lastPaint"/> stamp.
    /// </summary>
    public DateTime LastPaint => this.lastPaint;

    /// <summary>
    /// IFrameSource frame-ready event (D-01/D-25). RAISED from <see cref="OnBrowserPaint"/> for each
    /// paint; <see cref="NdiFrameSink"/> (and the future Phase-2 monitor) SUBSCRIBE. The
    /// <see cref="FrameView.Buffer"/> pointer is CALLBACK-SCOPED — subscribers must send/inspect
    /// in-call and copy if they retain it (Pitfall 3). See <see cref="IFrameSource.FrameReady"/>.
    /// </summary>
    public event Action<FrameView>? FrameReady;

    /// <summary>
    /// D-26/D-28: raised after EVERY successful navigation/swap (both <see cref="SwapRecipeAsync"/> and the
    /// same-recipe <see cref="SetUrlAsync"/> branch) carrying the NOW-current recipe. The composition root
    /// subscribes this to invoke <c>FrameMonitor.Reset(recipe)</c> + reload the fallback asset, so a
    /// /seturl or /recipe swap clears stale monitor state (Pitfall P-5 — without it the new page false-trips
    /// on the old page's dHash window). This wrapper holds NO FrameMonitor reference (D-28): the reset is a
    /// CALLBACK from here, NOT a direct FrameMonitor.Reset() call inside CefWrapper.
    /// </summary>
    public event Action<Recipe?>? RecipeSwapped;

    // D-15b: conditional one-shot smoke seam. OnBrowserPaint is private and the upstream send
    // is continuous (gated only on Program.NdiSenderPtr != Zero), so "exactly one send" cannot
    // be hooked from Program — the latch must live here. It is ARMED only under --smoke; on the
    // normal --url= path SmokeMode stays false and the continuous-send path is byte-for-byte
    // upstream (D-05).
    public bool SmokeMode { get; set; }
    private bool smokeSent;
    private readonly TaskCompletionSource<bool> smokeFrameSent =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when exactly one non-blank frame has been sent under --smoke (D-15b).
    /// Only meaningful when <see cref="SmokeMode"/> is true.
    /// </summary>
    public Task SmokeFrameSent => this.smokeFrameSent.Task;

    // D-29a (MON-01): pre-send OnPaint capture latch for the --onpaint-format gate. OnBrowserPaint
    // returns at :213 when Program.NdiSenderPtr == nint.Zero — BEFORE lastPaint (:225) and the
    // FrameReady event (:248) — so a no-NDI gate cannot capture the real bytes through either of
    // those downstream seams. This latch copies e.BufferHandle (Height*Width*4 bytes) into a
    // retained managed buffer BEFORE that guard returns, so the gate reads the REAL pre-send pixels
    // exactly as CEF painted them (no NDI sender required, no live send path altered). Armed only by
    // the gate; on the normal --url= path CaptureNextPaint stays false and OnBrowserPaint is
    // unchanged (the copy block is skipped). One-shot: it disarms after the first non-blank paint.
    public bool CaptureNextPaint { get; set; }
    private bool capturedPaint;
    private byte[]? capturedBuffer;
    private int capturedWidth;
    private int capturedHeight;
    // 03-03 (Q2 gate): NOT readonly so the alpha-0-damage gate can RE-ARM for a SECOND capture (a fresh TCS
    // per arm). The normal --url= send path NEVER re-arms (CaptureNextPaint stays false, L327-328), so the
    // re-arm is gate-only — the one-shot capturedPaint latch behaviour on the send path is unchanged.
    private TaskCompletionSource<bool> paintCaptured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// D-29a: completes when exactly one non-blank OnPaint buffer has been copied under
    /// <see cref="CaptureNextPaint"/>. Only meaningful when the gate armed the latch.
    /// </summary>
    public Task PaintCaptured => this.paintCaptured.Task;

    /// <summary>
    /// 03-03 (Q2 alpha-0-damage gate ONLY): re-arm the one-shot pre-send capture latch for a SECOND capture.
    /// Resets <see cref="capturedPaint"/> + clears the prior captured buffer + installs a FRESH
    /// <see cref="PaintCaptured"/> Task, then re-arms <see cref="CaptureNextPaint"/>. The caller awaits the new
    /// <see cref="PaintCaptured"/> for the next non-blank paint. This is GATE-ONLY: the normal --url= send path
    /// never calls it and leaves CaptureNextPaint false, so the send path's one-shot behaviour is unchanged.
    /// </summary>
    public void ReArmCapture()
    {
        this.capturedPaint = false;
        this.capturedBuffer = null;
        this.capturedWidth = 0;
        this.capturedHeight = 0;
        this.paintCaptured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.CaptureNextPaint = true;
    }

    /// <summary>
    /// D-29a: the copied pre-send OnPaint bytes (BGRA, <see cref="CapturedWidth"/> *
    /// <see cref="CapturedHeight"/> * 4) the gate reads back. Null until a paint is captured.
    /// </summary>
    public byte[]? CapturedBuffer => this.capturedBuffer;
    public int CapturedWidth => this.capturedWidth;
    public int CapturedHeight => this.capturedHeight;

    // RC-3 / D-29d (MON-01): our OWN reusable straight-alpha buffer. CEF's OnPaint surface is
    // PREMULTIPLIED BGRA (02-UAT.md L1 confirmed) but the locked wire format is BGRA STRAIGHT
    // (CLAUDE.md; AlphaConvention.Expected = Straight). OnBrowserPaint un-premultiplies e.BufferHandle
    // (CEF-owned, callback-scoped, may be reused — NEVER mutated in place) INTO this buffer BEFORE the
    // pre-send capture, so the --onpaint-format gate and the FrameReady FramePump/detectors all read
    // straight bytes, matching the already-straight FallbackProvider frames. Sized Height*Width*4 and
    // re-allocated ONLY on a geometry change (mirrors the FrameMonitor reallocation discipline) — NOT a
    // per-frame `new byte[]` on the hot path, so the conversion adds no GC jitter (D-30).
    private byte[]? straightBuffer;
    private int straightWidth;
    private int straightHeight;

    public CefWrapper(int width, int height, string initialUrl)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;

        // D-16: store the start URL for a DEFERRED first Load; construct on about:blank so NO real
        // document is created before the doc-start CDP script can register (the upstream ctor passed
        // initialUrl straight to the browser, auto-loading it immediately — the D-16 root cause).
        this.startUrl = initialUrl;

        // 03-04 fix: construct WITHOUT auto-creating the browser, then CreateBrowser on a windowless
        // surface with ExternalBeginFrameEnabled (NewWindowlessExternalBeginFrameWindowInfo). The
        // BrowserSettings passed here (TransparentBrowserSettings) is retained and applied by CreateBrowser.
        // D-16 ordering is preserved — still about:blank, so the doc-start CDP script still registers before
        // the first real Load in InitializeWrapperAsync.
        //
        // useLegacyRenderHandler:false MATCHES the validated spike probe (the spike that proved external
        // begin-frame advances the radar used false). The default (true) installs the legacy
        // DefaultRenderHandler — a Bitmap consumer for ScreenshotAsync; false leaves RenderHandler null. We
        // consume the Paint EVENT (raised by ChromiumWebBrowser itself, independent of RenderHandler) and use
        // no ScreenshotAsync/Bitmap, so a null RenderHandler is harmless. NOT independently confirmed as
        // necessary: the residual freeze under the begin-frame fix turned out to be the recipe's play-click +
        // hideChrome (fixed in recipes/accuweather.json), not this flag — kept to match the proven-good spike
        // config and avoid the legacy handler's extra present-path bookkeeping. Revisit if minimizing the diff.
        this.browser = new ChromiumWebBrowser(
            "about:blank",
            TransparentBrowserSettings(),
            automaticallyCreateBrowser: false,
            useLegacyRenderHandler: false)
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);

        // D-11/D-27: thin belt-and-suspenders re-assert — re-apply the FULL recipe Css ONLY at the
        // start of every frame load (never consent/JS through this path). Wired here so it survives
        // browser recreate (re-wired in RecreateBrowserCoreAsync). Subscribed BEFORE CreateBrowser so no
        // early FrameLoadStart is missed.
        this.browser.FrameLoadStart += this.OnFrameLoadStart;

        this.browser.CreateBrowser(NewWindowlessExternalBeginFrameWindowInfo());

        this.RenderWatchdog = new Thread(this.RenderWatchDogThread);
        this.BeginFrameDriver = new Thread(this.BeginFrameDriverThread);
    }

    private void RenderWatchDogThread()
    {
        while (!this.disposedValue)
        {
            // RC-1 (02-07): UTC basis. The watchdog's internal 1.0s invalidation compares against the
            // same `lastPaint` the FrameMonitor reads on its DateTime.UtcNow clock — both sides MUST be
            // UTC or a host UTC-offset (e.g. UTC-5) re-introduces a cross-component skew that misreads a
            // fresh paint as stale. Stamp (:306) and compare (here) are unified on DateTime.UtcNow.
            if(DateTime.UtcNow.Subtract(this.lastPaint).TotalSeconds >= 1.0)
            {
                this.browser.GetBrowser().GetHost().Invalidate(PaintElementType.View);
            }

            Thread.Sleep(1000);
        }
    }

    /// <summary>
    /// 03-04 ([[cef-offscreen-needs-external-beginframe]]) — the external begin-frame driver loop. Pumps
    /// <c>SendExternalBeginFrame</c> at ~60fps for the lifetime of the wrapper. MANDATORY: with
    /// <see cref="WindowInfo.ExternalBeginFrameEnabled"/> set, CEF emits no OnPaint at all without this.
    /// Reads <see cref="browser"/> fresh each tick (like <see cref="RenderWatchDogThread"/>) so a
    /// <see cref="RecreateBrowserCoreAsync"/> swap is picked up with NO thread recreation; the
    /// null-conditional + try/catch ride the brief window where the host is mid-recreate or disposing.
    /// </summary>
    private void BeginFrameDriverThread()
    {
        while (!this.disposedValue)
        {
            try
            {
                this.browser?.GetBrowserHost()?.SendExternalBeginFrame();
            }
            catch
            {
                // best-effort — a transient/disposed host during a recreate must not kill the driver.
            }

            Thread.Sleep(16);
        }
    }

    /// <summary>
    /// 03-04 fix WindowInfo: a windowless surface whose begin-frame is driven EXTERNALLY by
    /// <see cref="BeginFrameDriverThread"/>. <see cref="WindowInfo.WindowlessRenderingEnabled"/> keeps the
    /// CPU OnPaint path (<see cref="WindowInfo.SharedTextureEnabled"/> stays false by default —
    /// isolation-validated 03-04 — so the OnPaint → un-premult → BGRA-straight-alpha → NDI pipeline and the
    /// locked wire format are UNCHANGED). <see cref="WindowInfo.ExternalBeginFrameEnabled"/> is the actual
    /// fix: it hands the present cadence to the driver, which advances render-gated WebGL players (MapLibre
    /// radar) that CEF's internal windowless begin-frame leaves frozen. Used at BOTH browser-creation sites.
    /// </summary>
    private static WindowInfo NewWindowlessExternalBeginFrameWindowInfo()
    {
        var windowInfo = new WindowInfo();
        windowInfo.SetAsWindowless(IntPtr.Zero);
        windowInfo.WindowlessRenderingEnabled = true;
        windowInfo.ExternalBeginFrameEnabled = true;
        return windowInfo;
    }

    public async Task InitializeWrapperAsync()
    {
        if (this.browser is null)
        {
            return;
        }

        // 03-04: start the external begin-frame driver FIRST — with ExternalBeginFrameEnabled set at
        // CreateBrowser, CEF produces no frames (the about:blank surface never even presents) until
        // SendExternalBeginFrame is pumped. Mirrors the spike's driver-before-wait ordering.
        this.BeginFrameDriver.Start();

        // Resolves the about:blank load (D-16) — NOT a real document, so nothing the recipe targets has
        // rendered yet. Registration below therefore precedes the first real Load.
        await this.browser.WaitForInitialLoadAsync();

        this.browser.GetBrowserHost().WindowlessFrameRate = 60;
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
        this.RenderWatchdog.Start();

        // D-13: force the offscreen surface visible so Chromium does not report it hidden and halt
        // requestAnimationFrame (the anti-throttle CefCommandLineArgs are set process-globally at
        // Cef.Initialize in Program; this is the per-browser visibility lever).
        this.ForceVisible();

        // D-15/D-16: acquire the persistent DevToolsClient + Page.enable, register the startup recipe's
        // doc-start script, THEN Load(startUrl) — so the FIRST real document is injected (Pitfall 1).
        this.CurrentRecipe = this.StartupRecipe;
        this.injectHook = new InjectHook(this);
        await this.injectHook.EnsureClientAsync();
        await this.injectHook.RegisterRecipeAsync(this.StartupRecipe);

        // The deferred first real navigation (D-16) — register-before-Load satisfied by construction.
        this.Url = this.startUrl;
        this.browser.Load(this.startUrl);
    }

    /// <summary>
    /// D-13 force-visible: tell the host the surface is NOT hidden + take focus, then (best-effort) the
    /// CDP focus-emulation lever, so a surface that would otherwise report hidden does not throttle rAF.
    /// </summary>
    private void ForceVisible()
    {
        try
        {
            var host = this.browser?.GetBrowserHost();
            if (host is null)
            {
                return;
            }

            host.WasHidden(false);
            host.SendFocusEvent(true);
        }
        catch
        {
            // best-effort — never let a visibility lever fail startup.
        }
    }

    /// <summary>
    /// D-11/D-27 belt-and-suspenders: at the start of every frame load, re-assert the FULL
    /// <see cref="CurrentRecipe"/> Css ONLY (the flat recipe has only css — there is NO separate
    /// hideChromeCss subset field). NEVER injects consent/JS through this path (that is the doc-start
    /// CDP script's job, D-15). A null recipe / null css is a no-op (D-07 pass-through).
    /// </summary>
    private void OnFrameLoadStart(object? sender, FrameLoadStartEventArgs e)
    {
        var css = this.CurrentRecipe?.Css;
        if (string.IsNullOrEmpty(css))
        {
            return;
        }

        try
        {
            // Append a <style> node carrying the full recipe css. CSS-only — no consent/JS.
            var encoded = System.Text.Json.JsonSerializer.Serialize(css);
            var script =
                "(function(){try{var s=document.getElementById('__xpn_style_reassert')||document.createElement('style');" +
                "s.id='__xpn_style_reassert';s.textContent=" + encoded + ";" +
                "(document.head||document.documentElement).appendChild(s);}catch(e){}})();";
            e.Frame.ExecuteJavaScriptAsync(script);
        }
        catch
        {
            // best-effort re-assert — the load-bearing path is the doc-start CDP script.
        }
    }

    /// <summary>
    /// D-29c (MON-01): the explicit TRANSPARENT browser background. There was NO transparent-bg
    /// setting in the wrapper before this — the spike's keyability rode an implicit CefSharp.OffScreen
    /// default. Setting <see cref="BrowserSettings.BackgroundColor"/> to fully-transparent ARGB
    /// (0x00000000) makes CEF composite onto a transparent surface, so a 50%-alpha / fully-transparent
    /// page region paints back as SOURCE alpha (A&lt;255) instead of an opaque composited pixel. This is
    /// the SAME production path the live keyable NDI send uses (applied in both the ctor and
    /// <see cref="RecreateBrowserCoreAsync"/>), so the --onpaint-format gate exercises the real
    /// transparency path, not a test-only one. Without this, the MON-01 alpha readback would measure
    /// opaque composited pixels and pass while proving the wrong thing.
    /// </summary>
    private static BrowserSettings TransparentBrowserSettings()
    {
        return new BrowserSettings
        {
            // Fully-transparent ARGB background (A=0). CefSharp.OffScreen honors this on the OnPaint
            // surface so source alpha survives into the paint buffer.
            BackgroundColor = 0x00000000u,
        };
    }

    private unsafe void OnBrowserPaint(object? sender, OnPaintEventArgs e)
    {
        if (e.BufferHandle == nint.Zero || e.Width <= 0 || e.Height <= 0)
        {
            return;
        }

        // RC-3 / D-29d (MON-01): un-premultiply FIRST, before ANYTHING downstream reads the bytes. CEF's
        // OnPaint surface is PREMULTIPLIED BGRA (02-UAT.md L1) but the locked convention is STRAIGHT
        // (AlphaConvention.Expected). Re-allocate our own buffer ONLY on a geometry change (not per
        // frame — D-30 zero-jitter), then convert e.BufferHandle (CEF-owned, read-only, never mutated in
        // place) into it. From here on, EVERY reader — the D-29a capture, the smoke/blank checks, and the
        // FrameReady FramePump/detectors — reads `this.straightBuffer`, so the whole pipeline agrees with
        // the already-straight FallbackProvider frames on one convention. The send-path BGRA fourCC is
        // unchanged (the bytes are still BGRA, now correctly straight); the live XPression-keyer
        // validation (VAL-02) remains Phase 3 per D-29d.
        var byteCount = checked(e.Width * e.Height * 4);
        if (this.straightBuffer is null || this.straightWidth != e.Width || this.straightHeight != e.Height)
        {
            this.straightBuffer = new byte[byteCount];
            this.straightWidth = e.Width;
            this.straightHeight = e.Height;
        }

        var src = new ReadOnlySpan<byte>((void*)e.BufferHandle, byteCount);
        Monitor.UnpremultiplyLut.Unpremultiply(src, this.straightBuffer, e.Width, e.Height);

        // Pin our straight buffer for the rest of this callback so its address can be handed to the
        // capture copy, the blank checks, and the FrameView (all of which take an nint). The pin lives
        // exactly for the callback scope — the FrameView pointer contract (callback-scoped) is preserved.
        fixed (byte* straightPtr = this.straightBuffer)
        {
            var straight = (nint)straightPtr;

            // D-29a (MON-01): pre-send capture for the --onpaint-format gate, taken BEFORE the
            // NdiSenderPtr==Zero guard below — the only place the pre-send OnPaint bytes are reachable
            // with no NDI sender attached. Now copies from OUR STRAIGHT buffer (not CEF's premultiplied
            // one), so the gate's CapturedBuffer readback samples straight: the 50%-alpha red region reads
            // R≈255 -> observed = Straight -> matches AlphaConvention.Expected -> the gate exits 0.
            // One-shot + blank-skipping (a never-painting SwiftShader init must not count). On the normal
            // path CaptureNextPaint is false and this block is skipped entirely.
            if (this.CaptureNextPaint && !this.capturedPaint)
            {
                if (!IsBlankBuffer(straight, e.Width, e.Height))
                {
                    var copy = new byte[byteCount];
                    System.Runtime.InteropServices.Marshal.Copy(straight, copy, 0, byteCount);
                    this.capturedBuffer = copy;
                    this.capturedWidth = e.Width;
                    this.capturedHeight = e.Height;
                    this.capturedPaint = true;
                    this.paintCaptured.TrySetResult(true);
                }
            }

            if (Program.NdiSenderPtr == nint.Zero)
            {
                return;
            }

            var browser = sender as ChromiumWebBrowser;

            if (browser is null)
            {
                return;
            }

            // RC-1 (02-07): UTC paint stamp. LastPaint (the IFrameSource liveness clock the FrameMonitor
            // reads in Classify/SnapshotHealth on its DateTime.UtcNow default) MUST share the monitor's UTC
            // basis — DateTime.Now (LOCAL) on a UTC-offset host made every fresh frame look ~5h stale (the
            // L2/L4 blocker/major in 02-UAT.md). Never-painted sentinel stays DateTime.MinValue.
            this.lastPaint = DateTime.UtcNow;

            // D-15b: one-shot smoke latch. Under --smoke we send EXACTLY ONE non-blank frame, then
            // signal completion and suppress all further sends. A black/all-zero first paint does
            // NOT count as the one good frame (D-15c blank-buffer guard). Reads our straight buffer for
            // consistency (alpha-0 and all-black are basis-invariant, so the classification is unchanged).
            // On the normal path (SmokeMode == false) this whole block is skipped and the send is unchanged.
            if (this.SmokeMode)
            {
                if (this.smokeSent)
                {
                    return;
                }

                if (IsBlankBuffer(straight, e.Width, e.Height))
                {
                    // Not a usable frame yet — wait for a non-blank paint (the watchdog re-invalidates).
                    return;
                }
            }

            // D-01/D-25: this wrapper is the SOURCE — it RAISES the frame-ready event with a primitive,
            // callback-scoped view. The subscribed FramePump/monitor copies the bytes in-call. The view now
            // points at OUR STRAIGHT buffer (same geometry/stride: Width, Height, Width*4) — the pointer is
            // valid only for this invocation (the pin above covers exactly the callback scope).
            this.FrameReady?.Invoke(new FrameView(straight, e.Width, e.Height, e.Width * 4));

            if (this.SmokeMode && !this.smokeSent)
            {
                this.smokeSent = true;
                this.smokeFrameSent.TrySetResult(true);
            }
        }
    }

    /// <summary>
    /// D-15c / D-08: cheap blank-frame check over the BGRA paint buffer. Originally a luma-only all-black
    /// test (so a never-painting SwiftShader init does not count as a good frame); Plan 02-03 EXTENDED it
    /// with the two D-08 branches via <see cref="BlankDetector"/> — a low-variance/abnormal branch AND an
    /// all-alpha-0 (straight-alpha transparent-blank) branch the luma-only check missed. The original
    /// broadcast-black classification is PRESERVED (the explicit all-black scan below) and is ORed with the
    /// new branches, so the existing <see cref="OnBrowserPaint"/> smoke/capture call-sites do not regress —
    /// a genuinely all-black frame still returns true. Samples a sparse stride to stay O(1)-ish on 1080p.
    /// </summary>
    private static unsafe bool IsBlankBuffer(nint buffer, int width, int height)
    {
        if (buffer == nint.Zero || width <= 0 || height <= 0)
        {
            return true;
        }

        var bytesPerPixel = 4; // BGRA
        long totalPixels = (long)width * height;
        // Sample ~4096 pixels evenly across the frame.
        long step = Math.Max(1, totalPixels / 4096);
        var ptr = (byte*)buffer;
        long nonBlack = 0;

        for (long i = 0; i < totalPixels; i += step)
        {
            long off = i * bytesPerPixel;
            byte b = ptr[off + 0];
            byte g = ptr[off + 1];
            byte r = ptr[off + 2];

            // Rec.601-ish luma; broadcast-black threshold (~8% of 255).
            int luma = (299 * r + 587 * g + 114 * b) / 1000;
            if (luma > 20)
            {
                nonBlack++;
            }
        }

        // PRESERVED original all-black classification: a meaningful fraction must be non-black.
        if (nonBlack < 16)
        {
            return true;
        }

        // D-08 EXTENSION: even a non-black frame is blank/abnormal if it is near-uniform (low-variance,
        // abnormal folded in) or fully transparent (all-alpha-0). Delegate to the pure detector over a
        // span view of the contiguous BGRA buffer (stride == width*4 for the CEF OnPaint surface).
        long byteCount = totalPixels * bytesPerPixel;
        var span = new ReadOnlySpan<byte>((void*)buffer, checked((int)byteCount));
        return Monitor.BlankDetector.Analyze(span, width, height, width * bytesPerPixel).IsBlank;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposedValue)
        {
            if (disposing)
            {
                this.injectHook?.Dispose();
                this.injectHook = null;

                if (this.browser is not null)
                {
                    this.browser.Paint -= this.OnBrowserPaint;
                    this.browser.FrameLoadStart -= this.OnFrameLoadStart;
                    this.browser.Dispose();
                }

                this.browser = null;
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            this.disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        this.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// D-16: the ASYNC navigation/swap entry that AWAITS doc-start re-registration BEFORE Load — no
    /// fire-and-forget race (replaces the synchronous upstream <c>void SetUrl</c>). Behavior:
    /// <list type="bullet">
    ///   <item>if <paramref name="nextRecipe"/> differs from <see cref="CurrentRecipe"/>, delegate to the
    ///   fail-closed <see cref="SwapRecipeAsync"/> (re-register BEFORE Load, D-04/D-17);</item>
    ///   <item>otherwise just await a plain re-Load — the doc-start script re-fires on the new document
    ///   automatically, no re-registration needed.</item>
    /// </list>
    /// Callers resolve the recipe for the new URL (via the store) and pass it in; a <c>null</c> recipe is
    /// a pass-through (D-07).
    /// </summary>
    public async Task SetUrlAsync(string url, Recipe? nextRecipe = null)
    {
        if (this.browser is null)
        {
            return;
        }

        if (!ReferenceEquals(nextRecipe, this.CurrentRecipe))
        {
            // Different recipe → fail-closed swap (re-register before Load). SwapRecipeAsync drives Load.
            await this.SwapRecipeAsync(nextRecipe, url);
            return;
        }

        // Same recipe → the registered doc-start script re-fires on the new document; just navigate.
        this.Url = url;
        this.browser.Load(url);

        // D-26: a same-recipe URL change is STILL a swap for the monitor — the new page must be classified
        // fresh (its dHash window / freeze/recovery counters reset). Raise the swap signal the composition
        // root subscribes to invoke FrameMonitor.Reset() (Pitfall P-5). Reset fires on EVERY successful
        // SetUrlAsync, not only the recipe-swap branch.
        this.RecipeSwapped?.Invoke(this.CurrentRecipe);
    }

    /// <summary>
    /// D-04/D-17 fail-closed recipe swap (consumed by the <c>/recipe</c> POST and by
    /// <see cref="SetUrlAsync"/>). Delegates the CDP remove/re-add to the <see cref="InjectHook"/>
    /// (Remove-by-tracked-id → re-Add on the persistent client; RecreateBrowserAsync on a Remove
    /// failure), updates <see cref="CurrentRecipe"/>, THEN Loads the URL — register-before-Load.
    /// </summary>
    /// <param name="next">The recipe to swap in (<c>null</c> = pass-through).</param>
    /// <param name="url">The URL to (re-)load after re-registration; defaults to the current URL.</param>
    public async Task SwapRecipeAsync(Recipe? next, string? url = null)
    {
        if (this.browser is null || this.injectHook is null)
        {
            return;
        }

        await this.injectHook.SwapRecipeAsync(next);
        this.CurrentRecipe = next;

        var target = url ?? this.Url ?? this.startUrl;
        this.Url = target;
        this.browser.Load(target); // register-before-Load: re-registration already awaited above.

        // D-26: signal the swap so the composition root resets the monitor (clear stale windows + counters,
        // apply the new recipe's expectMotion, reload the fallback asset). Pitfall P-5 — without this the
        // new page false-trips on the prior page's dHash window. This wrapper holds NO FrameMonitor
        // reference: the reset is a CALLBACK the composition root wires (D-28).
        this.RecipeSwapped?.Invoke(this.CurrentRecipe);
    }

    /// <summary>
    /// D-17 steps 1–3 + 6 — the browser/paint/sink/smoke half of <see cref="InjectHook.RecreateBrowserAsync"/>
    /// (the CDP-client half lives in InjectHook). Called ONLY from the fail-closed swap fallback:
    /// <list type="number">
    ///   <item>unsubscribe Paint + FrameLoadStart from the old browser;</item>
    ///   <item>dispose the old browser (its DevTools client was already disposed by InjectHook);</item>
    ///   <item>recreate the browser on about:blank with the SAME size + audio handler, re-wire Paint +
    ///   FrameLoadStart, force visible (the D-13 anti-throttle flags are process-global from
    ///   Cef.Initialize and persist across this recreate);</item>
    ///   <item>(6) the IFrameSource/NdiFrameSink wiring is PRESERVED automatically — the sink is
    ///   subscribed to THIS wrapper's <see cref="FrameReady"/> event, not to the browser, so recreating
    ///   the browser does not orphan it; the smoke latch (SmokeMode/smokeSent/smokeFrameSent) lives on
    ///   this wrapper and is untouched, so --smoke still completes after a recreate.</item>
    /// </list>
    /// InjectHook then re-acquires the DevTools client, re-registers the doc-start script BEFORE Load
    /// (steps 4–5), and the caller drives Load.
    /// </summary>
    internal Task RecreateBrowserCoreAsync()
    {
        var old = this.browser;
        if (old is not null)
        {
            old.Paint -= this.OnBrowserPaint;            // step 1
            old.FrameLoadStart -= this.OnFrameLoadStart; // step 1
            old.Dispose();                                // step 2
        }

        // step 3: recreate on about:blank (deferred Load — InjectHook re-registers before the caller Loads).
        // 03-04: recreate on the SAME windowless + ExternalBeginFrameEnabled surface (site 2) AND with the
        // SAME useLegacyRenderHandler:false (see the ctor note), else the long-lived BeginFrameDriver's
        // SendExternalBeginFrame produces no paints for the new browser at all. The driver THREAD is NOT
        // recreated — it reads this.browser fresh (like RenderWatchdog) and drives whichever browser is current.
        this.browser = new ChromiumWebBrowser(
            "about:blank",
            TransparentBrowserSettings(),
            automaticallyCreateBrowser: false,
            useLegacyRenderHandler: false)
        {
            AudioHandler = new CustomAudioHandler(),
        };
        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);
        this.browser.Paint += this.OnBrowserPaint;            // re-wire — preserves the FrameReady→sink path (step 6)
        this.browser.FrameLoadStart += this.OnFrameLoadStart; // re-wire the D-27 css re-assert
        this.browser.CreateBrowser(NewWindowlessExternalBeginFrameWindowInfo());

        return this.browser.WaitForInitialLoadAsync().ContinueWith(_ => this.ForceVisible());
    }

    /// <summary>
    /// D-25 seam member. The <see cref="IFrameSource"/> contract is "navigate the source to a URL"; this
    /// wrapper's real navigation is the awaited <see cref="SetUrlAsync(string, Recipe?)"/>. The seam
    /// caller has no recipe context, so this forwards to a recipe-less re-load (same-recipe path — the
    /// registered doc-start script re-fires on the new document). Control-plane callers that DO have
    /// recipe context (the /recipe + /seturl endpoints) use <see cref="SetUrlAsync"/> directly and AWAIT
    /// re-registration (D-16); this explicit-interface forwarder exists only to satisfy the seam.
    /// </summary>
    void IFrameSource.SetUrl(string url)
    {
        _ = this.SetUrlAsync(url, this.CurrentRecipe);
    }

    public void ScrollBy(int increment)
    {
        this.browser.SendMouseWheelEvent(0, 0, 0, increment, CefEventFlags.None); 
    }

    public void Click(int x, int y)
    {
        var host = this.browser?.GetBrowser()?.GetHost();

        if(host is null)
        {
            return;
        }

        host.SendMouseClickEvent(x, y,
            MouseButtonType.Left, false, 1, CefEventFlags.None);
        Thread.Sleep(100);
        host.SendMouseClickEvent(x, y,
            MouseButtonType.Left, true, 1, CefEventFlags.None);
    }

    public void SendKeystrokes(Models.SendKeystrokeModel model)
    {
        var host = this.browser?.GetBrowser()?.GetHost();

        if (host is null)
        {
            return;
        }

        foreach(var c in model.ToSend)
        {
            host.SendKeyEvent(new KeyEvent()
            {
                Type = KeyEventType.KeyDown,
                NativeKeyCode = Convert.ToInt32(c)
            });
        }
    }
    public void RefreshPage()
    {
        this.browser.Reload();
    }
}
