
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

    public CefWrapper(int width, int height, string initialUrl)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;

        // D-16: store the start URL for a DEFERRED first Load; construct on about:blank so NO real
        // document is created before the doc-start CDP script can register (the upstream ctor passed
        // initialUrl straight to the browser, auto-loading it immediately — the D-16 root cause).
        this.startUrl = initialUrl;

        this.browser = new ChromiumWebBrowser("about:blank")
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);

        // D-11/D-27: thin belt-and-suspenders re-assert — re-apply the FULL recipe Css ONLY at the
        // start of every frame load (never consent/JS through this path). Wired here so it survives
        // browser recreate (re-wired in RecreateBrowserCoreAsync).
        this.browser.FrameLoadStart += this.OnFrameLoadStart;

        this.RenderWatchdog = new Thread(this.RenderWatchDogThread);
    }

    private void RenderWatchDogThread()
    {
        while (!this.disposedValue)
        {
            if(DateTime.Now.Subtract(this.lastPaint).TotalSeconds >= 1.0)
            {
                this.browser.GetBrowser().GetHost().Invalidate(PaintElementType.View);
            }

            Thread.Sleep(1000);
        }
    }

    public async Task InitializeWrapperAsync()
    {
        if (this.browser is null)
        {
            return;
        }

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

    private void OnBrowserPaint(object? sender, OnPaintEventArgs e)
    {
        if (Program.NdiSenderPtr == nint.Zero)
        {
            return;
        }

        var browser = sender as ChromiumWebBrowser;

        if (browser is null)
        {
            return;
        }

        this.lastPaint = DateTime.Now;

        // D-15b: one-shot smoke latch. Under --smoke we send EXACTLY ONE non-blank frame, then
        // signal completion and suppress all further sends. A black/all-zero first paint does
        // NOT count as the one good frame (D-15c blank-buffer guard). On the normal path
        // (SmokeMode == false) this whole block is skipped and the send below is unchanged.
        if (this.SmokeMode)
        {
            if (this.smokeSent)
            {
                return;
            }

            if (IsBlankBuffer(e.BufferHandle, e.Width, e.Height))
            {
                // Not a usable frame yet — wait for a non-blank paint (the watchdog re-invalidates).
                return;
            }
        }

        // D-01/D-25: this wrapper is the SOURCE — it RAISES the frame-ready event with a primitive,
        // callback-scoped view. The subscribed NdiFrameSink does the actual NDIlib send (the send was
        // extracted out of this hot path). The buffer pointer is valid only for this invocation.
        this.FrameReady?.Invoke(new FrameView(e.BufferHandle, e.Width, e.Height, e.Width * 4));

        if (this.SmokeMode && !this.smokeSent)
        {
            this.smokeSent = true;
            this.smokeFrameSent.TrySetResult(true);
        }
    }

    /// <summary>
    /// D-15c: cheap non-blank check over the BGRA paint buffer — true if the sampled pixels are
    /// effectively all-black (so a never-painting SwiftShader init does not count as a good frame).
    /// Samples a sparse stride to stay O(1)-ish on a 1080p buffer.
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

        // Require a meaningful fraction of sampled pixels to be non-black.
        return nonBlack < 16;
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
        this.browser = new ChromiumWebBrowser("about:blank")
        {
            AudioHandler = new CustomAudioHandler(),
        };
        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);
        this.browser.Paint += this.OnBrowserPaint;            // re-wire — preserves the FrameReady→sink path (step 6)
        this.browser.FrameLoadStart += this.OnFrameLoadStart; // re-wire the D-27 css re-assert

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
