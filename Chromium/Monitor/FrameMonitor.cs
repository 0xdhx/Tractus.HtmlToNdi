using System.Runtime.InteropServices;
using Tractus.HtmlToNdi.Chromium.Inject;

namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// D-13/D-15/D-16: the recovery state-machine status. THESE VALUES ARE the <c>/health</c> status strings
/// Plan 06 reports — do NOT rename without updating the health snapshot contract.
/// </summary>
/// <remarks>
/// HEALTHY → SUSPECT → TRIPPED → RECOVERING → (HEALTHY | RECOVERY-EXHAUSTED):
/// <list type="bullet">
///   <item><see cref="Healthy"/> — fresh frames change and LastPaint is recent; output is Live.</item>
///   <item><see cref="Suspect"/> — at least one bad sample seen, but fewer than K-in consecutive (no flip
///   yet; a single bad sample must NOT flip — asymmetric fail-fast-but-not-flap, D-16).</item>
///   <item><see cref="Tripped"/> — K-in consecutive bad samples → output flipped to fallback; the injected
///   refresh delegate has been (or is about to be) invoked single-flight (D-13).</item>
///   <item><see cref="Recovering"/> — a refresh was issued; counting K-out consecutive good/fresh samples
///   that arrive AFTER lastRefreshTs (a bare /refresh is NOT recovery — D-15).</item>
///   <item><see cref="RecoveryExhausted"/> — N refresh attempts exhausted with no recovery; fallback is
///   HELD, refreshing STOPS, the watchdog (Phase 4) reads this (D-13).</item>
/// </list>
/// </remarks>
public enum MonitorStatus
{
    /// <summary>On air live; fresh changing frames, recent paint.</summary>
    Healthy,

    /// <summary>A bad-sample run is building but has not yet reached K-in (no fallback flip — D-16).</summary>
    Suspect,

    /// <summary>K-in bad samples reached; output flipped to fallback; single-flight refresh issued (D-13).</summary>
    Tripped,

    /// <summary>Refresh issued; counting K-out good samples that POST-DATE the refresh (D-15).</summary>
    Recovering,

    /// <summary>N attempts exhausted; fallback held, refreshing stopped (D-13 — the deferred watchdog reads this).</summary>
    RecoveryExhausted,
}

/// <summary>
/// D-01/D-02/D-05/D-30: the single-authority pump's CONSUMER side. <see cref="FrameMonitor"/>
/// subscribes to an <see cref="IFrameSource"/> (the CEF frame-source wrapper, ctor-injected — NOT a global,
/// mirroring <see cref="NdiFrameSink"/>), copies each callback-scoped <see cref="FrameView.Buffer"/>
/// out into a PRE-ALLOCATED PINNED wait-free double-buffer BEFORE the handler returns, and owns the
/// "current output" state (live-last-good vs fallback). The <see cref="FramePump"/> PULLS
/// <see cref="SnapshotCurrentOutput"/> each tick (push→pull at the sink boundary, D-03).
/// </summary>
/// <remarks>
/// <para>THREAD MODEL (the review-critical no-analog code, D-05/D-30):</para>
/// <list type="bullet">
///   <item>PRODUCER = the CEF UI thread, in-call from <see cref="IFrameSource.FrameReady"/>. It copies
///   <c>Height*Stride</c> bytes from the callback-scoped <c>view.Buffer</c> into the pinned
///   <c>_spare</c> buffer, then publishes with a SINGLE <see cref="System.Threading.Interlocked.Exchange{T}(ref T, T)"/>
///   POINTER-SWAP — never an array copy at swap, never a per-frame <c>new byte[]</c> (zero GC jitter).</item>
///   <item>CONSUMER = the pump thread, calling <see cref="SnapshotCurrentOutput"/>. It only ever reads
///   the PUBLISHED <c>_front</c> reference, so it observes either the prior fully-written buffer or the
///   new fully-written buffer — never a torn/half-written frame (T-2-02-1).</item>
/// </list>
/// <para>The buffers are PINNED for their whole lifetime (managed <c>byte[]</c> held by a
/// <see cref="GCHandle"/> with <see cref="GCHandleType.Pinned"/>) so the pinned address the pump
/// passes as <c>p_data</c> is stable across <c>NDIlib.send_send_video_v2</c> (D-30, T-2-02-4) — the GC
/// cannot relocate them mid-send. A geometry change reallocates+repins both buffers (T-2-02-2 — no
/// copy with a mismatched stride, no overrun).</para>
/// <para>This file MUST NOT import any CefSharp / render-engine binding type (D-02 / INJ-06): it binds
/// ONLY to <see cref="IFrameSource"/>, so the deferred Phase-5 PlaywrightFrameSource inherits the
/// monitor/pump unchanged.</para>
/// </remarks>
public sealed class FrameMonitor : IDisposable
{
    /// <summary>Which buffer <see cref="SnapshotCurrentOutput"/> hands the pump (D-04 atomic flip).</summary>
    public enum Output
    {
        /// <summary>The live last-good painted frame (the normal on-air path).</summary>
        Live,

        /// <summary>The fallback hold frame (a clean frame held on air while the live source misbehaves).</summary>
        Fallback,
    }

    /// <summary>
    /// A self-describing pinned BGRA frame the pump sends directly: the pinned <see cref="DataPtr"/> is
    /// what the pump passes as <c>p_data</c> (stable across the send, D-30), plus the geometry +
    /// alpha convention so the pump builds <c>video_frame_v2_t</c> byte-identically to
    /// <see cref="NdiFrameSink"/>. This is a struct (no per-tick allocation on the pull path).
    /// </summary>
    public readonly struct FrameBuffer
    {
        /// <summary>The PINNED address of the BGRA bytes — passed straight to NDI as <c>p_data</c> (D-30).</summary>
        public readonly nint DataPtr;

        /// <summary>Frame width in pixels.</summary>
        public readonly int Width;

        /// <summary>Frame height in pixels.</summary>
        public readonly int Height;

        /// <summary>Row stride in bytes (<c>Width * 4</c> for BGRA).</summary>
        public readonly int Stride;

        /// <summary>The alpha convention these bytes carry (D-21 — live and fallback MUST agree).</summary>
        public readonly AlphaMode AlphaMode;

        public FrameBuffer(nint dataPtr, int width, int height, int stride, AlphaMode alphaMode)
        {
            this.DataPtr = dataPtr;
            this.Width = width;
            this.Height = height;
            this.Stride = stride;
            this.AlphaMode = alphaMode;
        }
    }

    /// <summary>
    /// A pinned managed BGRA buffer. The <see cref="byte"/>[] is held by a long-lived pinning
    /// <see cref="GCHandle"/> so <see cref="Ptr"/> is stable for the buffer's whole lifetime (D-30).
    /// </summary>
    private sealed class PinnedBuffer
    {
        public readonly byte[] Bytes;
        private GCHandle handle;

        public PinnedBuffer(int byteCount)
        {
            this.Bytes = new byte[byteCount];
            this.handle = GCHandle.Alloc(this.Bytes, GCHandleType.Pinned);
        }

        /// <summary>The pinned, GC-stable address of <see cref="Bytes"/> (used as NDI <c>p_data</c>).</summary>
        public nint Ptr => this.handle.AddrOfPinnedObject();

        public void Free()
        {
            if (this.handle.IsAllocated)
            {
                this.handle.Free();
            }
        }
    }

    // D-21: the project-wide alpha convention every live/fallback frame carries (Plan 01 constant).
    private static readonly AlphaMode FrameAlphaMode = AlphaConvention.Expected;

    private readonly IFrameSource source;
    private readonly object reallocLock = new();

    // The wait-free live double-buffer (D-05/D-30). _front is the PUBLISHED buffer the pump reads;
    // _spare is the one the producer writes the next copy into. The producer publishes by a single
    // Interlocked.Exchange POINTER-SWAP of these two references — never an array copy at swap.
    private PinnedBuffer? front;
    private PinnedBuffer? spare;

    // Geometry the live double-buffer is currently sized for; a paint at a different size reallocates.
    private int liveWidth;
    private int liveHeight;
    private int liveStride;

    // The fallback / never-null placeholder slot (D-21 / SnapshotCurrentOutput-never-null). Seeded with
    // a generated-default solid pinned frame so a Fallback flip can NEVER hand the pump null before
    // Plan 04 supplies the real validated fallback. Replaced wholesale by Plan 04's FallbackProvider.
    private PinnedBuffer fallback;
    private int fallbackWidth;
    private int fallbackHeight;
    private int fallbackStride;

    // The atomic current-output discriminator (D-04). Read by the pull path, flipped by UseFallback.
    // 0 = Live, 1 = Fallback (int for Interlocked / volatile-read semantics).
    private volatile int outputState;

    private long lastCopyTicks; // DateTime.UtcNow.Ticks of the most recent live copy (Interlocked).
    private bool disposed;

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Plan 05: the asymmetric-hysteresis recovery state machine (D-06/D-07/D-13/D-14/D-15/D-16/D-31).
    // FrameMonitor binds to NO CEF type — recovery is an INJECTED Func<Task> refresh delegate (D-28);
    // the composition root injects the in-process refresh action (the frame-source wrapper's RefreshPage,
    // bound as a delegate). Reset is also driven from the composition root (the swap path), NOT a
    // frame-source-wrapper→FrameMonitor back-reference (D-28).
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 5 Hz (~200 ms) sample cadence (D-06). NON-REENTRANT (D-31).</summary>
    private const int SampleIntervalMs = 200;

    /// <summary>How many recent dHashes the freeze window compares (whole-window-identical ⇒ frozen, D-07).</summary>
    private const int FreezeWindowSize = 5;

    /// <summary>Default bounded refresh attempts before RECOVERY-EXHAUSTED (D-13).</summary>
    public const int DefaultMaxRefreshAttempts = 3;

    /// <summary>Cooldown after a refresh before another may be issued (single-flight settle, D-13).</summary>
    private const int RefreshCooldownMs = 3000;

    // Injected recovery delegate (D-28) — the frame-source wrapper's in-process RefreshPage bound as a
    // delegate in production, a counting fake in the unit tests. NEVER a direct render-engine-wrapper
    // type reference (the CEF-agnostic seam). Null ⇒ recovery is detection-only
    // (the state machine still flips/holds fallback, it just cannot self-heal — used by minimal tests).
    private readonly Func<Task>? refreshDelegate;

    // Injected clock for determinism (D-31 testability): tests pass a manually-advanceable clock so the
    // sample tick can be invoked directly with no real 200 ms waits. Defaults to DateTime.UtcNow.
    private readonly Func<DateTime> clock;

    private readonly int maxRefreshAttempts;

    // The non-reentrant 5 Hz sampler (D-06/D-31). A System.Threading.Timer whose callback is guarded by
    // an Interlocked flag so an overlapping tick is DROPPED rather than stacked.
    private System.Threading.Timer? sampleTimer;
    private int sampleInFlight; // 0 = idle, 1 = a tick is running (the re-entrancy guard, D-31).

    // Current recipe timing/hysteresis knobs (Plan 04 fields; conservative defaults when no recipe set).
    private int freezeTimeoutMs = Recipe.DefaultFreezeTimeoutMs;
    private int minHoldMs = Recipe.DefaultMinHoldMs;
    private int hysteresisKIn = Recipe.DefaultHysteresisKIn;
    private int hysteresisKOut = Recipe.DefaultHysteresisKOut;
    private bool expectMotion; // D-11: false default disables the content-freeze branch (static page safe).

    // State-machine state (all mutated only on the sample thread / under stateLock for cross-thread reads).
    private readonly object stateLock = new();
    private MonitorStatus status = MonitorStatus.Healthy;
    private int badRun;  // consecutive bad samples (drives the K-in flip, D-16).
    private int goodRun; // consecutive good samples POST-refresh (drives the K-out exit, D-15/D-16).
    private int recoveryAttempts;
    private DateTime fallbackEnteredTs = DateTime.MinValue;
    private DateTime lastRefreshTs = DateTime.MinValue;
    private string fallbackReason = string.Empty;
    private DateTime lastTransitionTs = DateTime.MinValue;
    private string lastTransitionReason = string.Empty;
    private bool refreshInFlight; // single-flight latch across the await of refreshDelegate (D-13).

    // The dHash freeze window — the most recent FreezeWindowSize hashes (D-07).
    private readonly ulong[] hashRing = new ulong[FreezeWindowSize];
    private int hashRingCount;
    private int hashRingNext;

    // Transition log (Pitfall P-4 — every state change is recorded with its triggering reason).
    private readonly List<string> transitionLog = new();

    /// <summary>
    /// Constructs the monitor over a ctor-injected frame source (D-02/D-26 — NOT a global) and
    /// subscribes to <see cref="IFrameSource.FrameReady"/>. The source's current
    /// <see cref="IFrameSource.Width"/>/<see cref="IFrameSource.Height"/> seed the default-frame and the
    /// initial live-buffer geometry; the first paint resizes if CEF reports a different size.
    /// </summary>
    /// <param name="source">The ctor-injected frame source (D-02 — not a global).</param>
    /// <param name="refreshDelegate">D-28: the INJECTED in-process recovery action (e.g.
    /// the frame-source wrapper's <c>RefreshPage()</c> bound as a delegate). FrameMonitor invokes THIS on
    /// TRIPPED (single-flight),
    /// never a CEF type. <c>null</c> ⇒ detection-only (the state machine still flips/holds fallback but
    /// cannot self-heal — used by tests that only exercise the detection path).</param>
    /// <param name="clock">D-31 testability: the "now" source the cadence check and timestamps read.
    /// Tests inject a manually-advanceable clock for determinism; defaults to <see cref="DateTime.UtcNow"/>.</param>
    /// <param name="maxRefreshAttempts">D-13: bounded refresh attempts before RECOVERY-EXHAUSTED.</param>
    public FrameMonitor(
        IFrameSource source,
        Func<Task>? refreshDelegate = null,
        Func<DateTime>? clock = null,
        int maxRefreshAttempts = DefaultMaxRefreshAttempts)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.refreshDelegate = refreshDelegate;
        this.clock = clock ?? (() => DateTime.UtcNow);
        this.maxRefreshAttempts = maxRefreshAttempts > 0 ? maxRefreshAttempts : DefaultMaxRefreshAttempts;

        var w = source.Width > 0 ? source.Width : 1920;
        var h = source.Height > 0 ? source.Height : 1080;

        this.AllocateLiveBuffers(w, h);
        this.SeedDefaultFallback(w, h);

        this.lastTransitionTs = this.clock();

        // D-PATTERNS: subscribe to the WRAPPER event, not the browser — survives a browser recreate.
        this.source.FrameReady += this.OnFrameReady;
    }

    /// <summary>Output geometry the pump derives its frame rate / dimensions from.</summary>
    public int Width => this.liveWidth;

    /// <summary>Output geometry the pump derives its frame rate / dimensions from.</summary>
    public int Height => this.liveHeight;

    /// <summary>The current output state (live vs fallback) — a frame-boundary flip the pump reads.</summary>
    public Output OutputState => this.outputState == 0 ? Output.Live : Output.Fallback;

    /// <summary>UTC timestamp of the most recently copied live paint (liveness, read by Plan 06 /health).</summary>
    public DateTime LastCopyTs => new(Interlocked.Read(ref this.lastCopyTicks), DateTimeKind.Utc);

    // ── Recovery state-machine public surface (read by Plan 06 /health; the values ARE the status enum) ──

    /// <summary>The current recovery state (D-13). These enum values ARE the <c>/health</c> status strings.</summary>
    public MonitorStatus Status { get { lock (this.stateLock) { return this.status; } } }

    /// <summary>Bounded refresh attempts issued in the current recovery episode (D-13).</summary>
    public int RecoveryAttempts { get { lock (this.stateLock) { return this.recoveryAttempts; } } }

    /// <summary>UTC timestamp of the most recent refresh issuance (the D-15 post-refresh recovery boundary).</summary>
    public DateTime LastRefreshTs { get { lock (this.stateLock) { return this.lastRefreshTs; } } }

    /// <summary>Which signal tripped the current fallback (cadence / freeze / blank-reason), for /health.</summary>
    public string FallbackReason { get { lock (this.stateLock) { return this.fallbackReason; } } }

    /// <summary>The most recent state transition + its triggering reason (Pitfall P-4, for /health).</summary>
    public (DateTime Ts, string Reason) LastTransition
    {
        get { lock (this.stateLock) { return (this.lastTransitionTs, this.lastTransitionReason); } }
    }

    /// <summary>Milliseconds since the last live copy (the cadence age the /health snapshot reports).</summary>
    public double LastGoodFrameAgeMs => (this.clock() - this.LastCopyTs).TotalMilliseconds;

    /// <summary>Snapshot of the transition log (Pitfall P-4 — every change recorded with its reason).</summary>
    public IReadOnlyList<string> TransitionLog { get { lock (this.stateLock) { return this.transitionLog.ToArray(); } } }

    /// <summary>
    /// Apply a recipe's timing/hysteresis knobs (Plan 04 fields) + <c>expectMotion</c> gate to the state
    /// machine (D-19/D-11). A <c>null</c> recipe restores the conservative defaults (D-10). Called at
    /// construction-time wiring and on every <see cref="Reset"/> so a swapped recipe's timing takes effect.
    /// </summary>
    public void ApplyRecipe(Recipe? recipe)
    {
        lock (this.stateLock)
        {
            this.freezeTimeoutMs = recipe?.FreezeTimeoutMs ?? Recipe.DefaultFreezeTimeoutMs;
            this.minHoldMs = recipe?.MinHoldMs ?? Recipe.DefaultMinHoldMs;
            this.hysteresisKIn = recipe?.HysteresisKIn ?? Recipe.DefaultHysteresisKIn;
            this.hysteresisKOut = recipe?.HysteresisKOut ?? Recipe.DefaultHysteresisKOut;
            this.expectMotion = recipe?.ExpectMotion ?? false;
        }
    }

    /// <summary>
    /// Start the NON-REENTRANT 5 Hz sample timer (D-06/D-31). The composition root calls this after the
    /// monitor + recovery delegate are wired. The timer callback is guarded by an Interlocked flag so an
    /// overlapping tick is DROPPED, never stacked. Idempotent.
    /// </summary>
    public void StartSampling()
    {
        if (this.disposed)
        {
            return;
        }

        this.sampleTimer ??= new System.Threading.Timer(
            this.SampleTimerCallback, null, SampleIntervalMs, SampleIntervalMs);
    }

    // The timer callback (D-31): a re-entrancy guard drops an overlapping tick instead of stacking it.
    private void SampleTimerCallback(object? _)
    {
        // Non-reentrant guard: if a prior tick is still running, drop this one (System.Threading.Timer
        // CAN overlap callbacks under load — Interlocked.CompareExchange makes the body single-flight).
        if (Interlocked.CompareExchange(ref this.sampleInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            this.OnSampleTick(this.clock());
        }
        catch
        {
            // A sample tick must never throw out of the timer thread (the sampler must keep running).
        }
        finally
        {
            Interlocked.Exchange(ref this.sampleInFlight, 0);
        }
    }

    /// <summary>
    /// D-06/D-07/D-13/D-15/D-16: ONE sample tick — the deterministic core the unit tests invoke directly
    /// with an injected clock (no real 200 ms wait). Samples the latest COPIED front buffer (never the
    /// callback-scoped CEF pointer), runs the three checks (cadence / freeze / blank), and advances the
    /// asymmetric-hysteresis state machine + single-flight recovery.
    /// </summary>
    /// <param name="now">The current time (injected for determinism).</param>
    public void OnSampleTick(DateTime now)
    {
        var (isBad, reason) = this.Classify(now);

        lock (this.stateLock)
        {
            if (isBad)
            {
                this.OnBadSample(now, reason);
            }
            else
            {
                this.OnGoodSample(now);
            }
        }
    }

    /// <summary>
    /// Run the three detection checks against the latest copied live frame (D-07 / RESEARCH Pattern 2):
    /// (1) CADENCE — no fresh paint past freezeTimeoutMs (the strongest no-paint signal; a wedged renderer
    /// is always bad). (2) FREEZE — dHash window all-identical AND expectMotion (gated, D-11). (3) BLANK —
    /// variance / all-alpha-0. Returns (isBad, reason). Samples the COPIED front buffer, never view.Buffer.
    /// </summary>
    private (bool IsBad, string Reason) Classify(DateTime now)
    {
        // (1) CADENCE — the no-paint signal. A wedged renderer (LastPaint stalled) is bad regardless of
        // expectMotion — BUT when expectMotion=false a stalled LastPaint on an intentionally-static page
        // is benign UNLESS the frame is also blank (D-07/D-11 nuance). So: a cadence stall is only "bad"
        // on its own when motion is expected; otherwise it must be corroborated by a blank frame.
        var paintAgeMs = (now - this.source.LastPaint).TotalMilliseconds;
        var cadenceStalled = this.source.LastPaint != DateTime.MinValue && paintAgeMs > this.freezeTimeoutMs;

        // Read the latest COPIED front buffer (D-05 — never the callback-scoped CEF pointer). A copy into
        // a managed array lets the detectors take a ReadOnlySpan with no pinning churn on the sample path.
        var snapshot = this.CopyFrontBufferForDetection(out var w, out var h, out var stride);

        BlankResult blank = BlankResult.NotBlank;
        if (snapshot is not null)
        {
            blank = BlankDetector.Analyze(snapshot, w, h, stride);
        }

        if (cadenceStalled && this.expectMotion)
        {
            return (true, "cadence-stall");
        }

        if (cadenceStalled && blank.IsBlank)
        {
            return (true, "cadence-stall+blank");
        }

        // (3) BLANK — fires regardless of expectMotion (a transparent/uniform on-air frame is always bad).
        if (blank.IsBlank)
        {
            return (true, blank.Reason == BlankReason.AllAlphaZero ? "blank-alpha0" : "blank-lowvariance");
        }

        // (2) FREEZE — content-delta freeze, GATED by expectMotion (D-11: a static page must never trip).
        if (snapshot is not null && this.expectMotion)
        {
            var hash = DiffHash.Compute(snapshot, w, h, stride);
            this.PushHash(hash);
            if (this.HashWindowFrozen())
            {
                return (true, "freeze-dhash");
            }
        }
        else
        {
            // expectMotion=false: do NOT accumulate a freeze window (the branch is disabled, D-11).
            this.ClearHashWindow();
        }

        return (false, string.Empty);
    }

    // Push a hash into the freeze ring (D-07).
    private void PushHash(ulong hash)
    {
        this.hashRing[this.hashRingNext] = hash;
        this.hashRingNext = (this.hashRingNext + 1) % FreezeWindowSize;
        if (this.hashRingCount < FreezeWindowSize)
        {
            this.hashRingCount++;
        }
    }

    private void ClearHashWindow()
    {
        this.hashRingCount = 0;
        this.hashRingNext = 0;
    }

    // The window is FROZEN only when it is FULL and every hash is within the global Hamming bound of the
    // newest (D-07 — a freeze candidate needs the WHOLE window to agree, not a single repeat).
    private bool HashWindowFrozen()
    {
        if (this.hashRingCount < FreezeWindowSize)
        {
            return false;
        }

        var newest = this.hashRing[(this.hashRingNext - 1 + FreezeWindowSize) % FreezeWindowSize];
        for (var i = 0; i < FreezeWindowSize; i++)
        {
            if (DiffHash.Hamming(newest, this.hashRing[i]) > MonitorDefaults.FreezeHammingBound)
            {
                return false;
            }
        }

        return true;
    }

    // Copy the latest published front buffer into a managed array for detection (D-05 — the detectors read
    // a COPY, never the live pinned buffer mid-swap and never the callback-scoped CEF pointer). Returns
    // null when no live frame has been published yet.
    private byte[]? CopyFrontBufferForDetection(out int width, out int height, out int stride)
    {
        var f = Volatile.Read(ref this.front);
        width = this.liveWidth;
        height = this.liveHeight;
        stride = this.liveStride;

        if (f is null || Interlocked.Read(ref this.lastCopyTicks) == 0)
        {
            return null; // no live paint yet — detection has nothing to sample.
        }

        var byteCount = height * stride;
        if (byteCount <= 0 || f.Bytes.Length < byteCount)
        {
            return null;
        }

        var copy = new byte[byteCount];
        Array.Copy(f.Bytes, copy, byteCount);
        return copy;
    }

    // ── The asymmetric-hysteresis state machine (D-13/D-14/D-15/D-16) ──

    private void OnBadSample(DateTime now, string reason)
    {
        this.goodRun = 0;
        this.badRun++;

        switch (this.status)
        {
            case MonitorStatus.Healthy:
            case MonitorStatus.Suspect:
                if (this.badRun >= this.hysteresisKIn)
                {
                    // K-in reached → TRIP: flip to fallback at a frame boundary (D-04), hold ≥ minHoldMs
                    // (D-14), and issue the single-flight in-process refresh (D-13).
                    this.EnterFallback(now, reason);
                    this.Transition(MonitorStatus.Tripped, $"tripped:{reason} (K-in={this.hysteresisKIn})");
                    this.TryIssueRefresh(now);
                }
                else if (this.status == MonitorStatus.Healthy)
                {
                    // First bad sample(s) but below K-in — SUSPECT, NO flip yet (fail-fast-not-flap, D-16).
                    this.Transition(MonitorStatus.Suspect, $"suspect:{reason} (badRun={this.badRun})");
                }

                break;

            case MonitorStatus.Tripped:
            case MonitorStatus.Recovering:
                // Already on fallback. A bad sample resets the post-refresh good run and, once the prior
                // refresh has SETTLED (cooldown elapsed) and min-hold is satisfied, escalates another
                // single-flight refresh — bounded by maxRefreshAttempts (D-13).
                this.TryIssueRefresh(now);
                break;

            case MonitorStatus.RecoveryExhausted:
                // HELD — refreshing has stopped (D-13). Stay put, keep fallback on air.
                break;
        }
    }

    private void OnGoodSample(DateTime now)
    {
        switch (this.status)
        {
            case MonitorStatus.Healthy:
                this.badRun = 0;
                break;

            case MonitorStatus.Suspect:
                // Recovered before reaching K-in — back to HEALTHY, no flip ever happened (D-16).
                this.badRun = 0;
                this.goodRun = 0;
                this.Transition(MonitorStatus.Healthy, "recovered-before-trip");
                break;

            case MonitorStatus.Tripped:
            case MonitorStatus.Recovering:
                this.badRun = 0;

                // D-15: a good frame only counts toward recovery if a FRESH PAINT arrived AFTER the refresh
                // (source.LastPaint > lastRefreshTs). A frame whose paint pre-dates the refresh — or no
                // refresh issued — is NOT evidence of recovery ("a bare /refresh is not recovery"). Using
                // the SOURCE paint clock (not the copy clock) is the real signal: a wedged renderer never
                // advances LastPaint, so its stale repaints can never satisfy this gate.
                if (this.lastRefreshTs == DateTime.MinValue || this.source.LastPaint <= this.lastRefreshTs)
                {
                    this.goodRun = 0;
                    break;
                }

                this.goodRun++;
                if (this.goodRun >= this.hysteresisKOut && this.MinHoldElapsed(now))
                {
                    // K-out consecutive POST-refresh good frames AND min-hold satisfied → EXIT fallback.
                    this.ExitFallback(now);
                }

                break;

            case MonitorStatus.RecoveryExhausted:
                // Once exhausted we do NOT auto-recover (the page may flap); the watchdog/operator resets.
                break;
        }
    }

    // D-13: issue a single-flight in-process refresh via the INJECTED delegate — bounded + cooldowned.
    private void TryIssueRefresh(DateTime now)
    {
        if (this.refreshDelegate is null)
        {
            // Detection-only wiring (some tests): still move to RECOVERING/EXHAUSTED bookkeeping so the
            // state machine is observable, but there is no delegate to call.
            return;
        }

        // Bounded attempts exhausted → hold fallback, stop refreshing (D-13). Checked FIRST so the
        // EXHAUSTED transition is deterministic the moment N attempts are spent — independent of the
        // single-flight latch or the cooldown window (those only gate ISSUING a NEW refresh).
        if (this.recoveryAttempts >= this.maxRefreshAttempts)
        {
            if (this.status != MonitorStatus.RecoveryExhausted)
            {
                this.Transition(MonitorStatus.RecoveryExhausted,
                    $"recovery-exhausted ({this.recoveryAttempts}/{this.maxRefreshAttempts} attempts)");
            }

            return;
        }

        if (Volatile.Read(ref this.refreshInFlight))
        {
            return; // single-flight: a prior refresh await has not completed.
        }

        // Cooldown: do not reload while a prior refresh is still settling (D-13).
        if (this.lastRefreshTs != DateTime.MinValue
            && (now - this.lastRefreshTs).TotalMilliseconds < RefreshCooldownMs)
        {
            return;
        }

        this.refreshInFlight = true;
        this.recoveryAttempts++;
        this.lastRefreshTs = now;
        this.goodRun = 0;
        this.Transition(MonitorStatus.Recovering,
            $"refresh#{this.recoveryAttempts} issued (single-flight, post-refresh K-out gate armed)");

        // Fire the injected delegate. We do NOT await inside the lock; the latch clears on completion.
        var task = this.refreshDelegate();
        if (task is null)
        {
            this.refreshInFlight = false;
            return;
        }

        task.ContinueWith(
            _ => Volatile.Write(ref this.refreshInFlight, false),
            TaskScheduler.Default);
    }

    private void EnterFallback(DateTime now, string reason)
    {
        this.UseFallback(true);
        this.fallbackEnteredTs = now;
        this.fallbackReason = reason;
        this.goodRun = 0;
    }

    private void ExitFallback(DateTime now)
    {
        this.UseFallback(false);
        this.badRun = 0;
        this.goodRun = 0;
        this.recoveryAttempts = 0;
        this.fallbackReason = string.Empty;
        this.ClearHashWindow();
        this.Transition(MonitorStatus.Healthy, "recovered:K-out-good-frames-post-refresh");
    }

    private bool MinHoldElapsed(DateTime now)
        => this.fallbackEnteredTs == DateTime.MinValue
           || (now - this.fallbackEnteredTs).TotalMilliseconds >= this.minHoldMs;

    // Record a state change with its triggering reason (Pitfall P-4). MUST be called under stateLock.
    private void Transition(MonitorStatus next, string reason)
    {
        var now = this.clock();
        this.transitionLog.Add($"{now:O} {this.status}->{next} {reason}");
        this.status = next;
        this.lastTransitionTs = now;
        this.lastTransitionReason = reason;
    }

    /// <summary>
    /// PRODUCER (CEF UI thread, in-call): copy the callback-scoped BGRA bytes out into the pinned spare
    /// buffer BEFORE returning (D-05 — never retain <c>view.Buffer</c> past the call), then publish via a
    /// single <see cref="Interlocked.Exchange{T}(ref T, T)"/> POINTER-SWAP (D-30 — no per-frame
    /// allocation, no array copy at swap). A geometry change reallocates+repins both buffers first.
    /// </summary>
    private void OnFrameReady(FrameView view)
    {
        if (this.disposed)
        {
            return;
        }

        var byteCount = view.Height * view.Stride;
        if (byteCount <= 0)
        {
            return;
        }

        // Geometry change (or first-real-size mismatch): reallocate+repin both live buffers so we never
        // copy with a mismatched stride / overrun a smaller buffer (T-2-02-2).
        if (view.Width != this.liveWidth || view.Height != this.liveHeight || view.Stride != this.liveStride)
        {
            lock (this.reallocLock)
            {
                this.AllocateLiveBuffers(view.Width, view.Height, view.Stride);
            }
        }

        var target = this.spare;
        if (target is null || target.Bytes.Length < byteCount)
        {
            return; // defensive — a concurrent realloc is in flight; skip this frame.
        }

        // Copy the callback-scoped CEF bytes into the pinned spare BEFORE the handler returns (D-05).
        // The ONLY use of view.Buffer is as the SOURCE of this copy — it is never stored in a field.
        Marshal.Copy(view.Buffer, target.Bytes, 0, byteCount);

        // Publish: atomic POINTER-SWAP of the two pinned-buffer references (D-30). After this, a pull
        // sees the fully-written buffer; the old front becomes the next spare.
        this.spare = Interlocked.Exchange(ref this.front, target);

        Interlocked.Exchange(ref this.lastCopyTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// CONSUMER (pump thread): the pump PULLS this each tick (D-03). Returns the published live buffer
    /// when <see cref="OutputState"/> is <see cref="Output.Live"/>, or the fallback buffer when
    /// <see cref="Output.Fallback"/>. NEVER returns null — before Plan 04 supplies a real fallback the
    /// monitor holds a seeded generated-default placeholder, so a Fallback flip can never hand the pump
    /// null (the SnapshotCurrentOutput-never-null contract).
    /// </summary>
    public FrameBuffer SnapshotCurrentOutput()
    {
        if (this.outputState == 0)
        {
            // Live: read the PUBLISHED reference (volatile read via Interlocked.CompareExchange-style).
            var f = Volatile.Read(ref this.front);
            if (f is not null)
            {
                return new FrameBuffer(f.Ptr, this.liveWidth, this.liveHeight, this.liveStride, FrameAlphaMode);
            }

            // No live paint yet — fall through to the never-null fallback/placeholder slot.
        }

        return new FrameBuffer(
            this.fallback.Ptr,
            this.fallbackWidth,
            this.fallbackHeight,
            this.fallbackStride,
            FrameAlphaMode);
    }

    /// <summary>
    /// PUBLIC reusable hold primitive (D-04 / BACKLOG-INTEGRATION 3) — NOT private to a freeze branch.
    /// Flips the current-output state; the change is read by the pump on its NEXT tick, i.e. at a frame
    /// boundary (flicker-free). Detection/hysteresis (Plans 03/05) and a future cross-posture hot-swap
    /// all ride THIS primitive.
    /// </summary>
    public void UseFallback(bool on) => this.outputState = on ? 1 : 0;

    /// <summary>
    /// Replaces the fallback slot with a validated/generated BGRA frame at the given geometry (D-21).
    /// Plan 04's FallbackProvider calls this to supply the REAL fallback frame; until then the seeded
    /// generated-default placeholder holds the slot. The bytes are copied into a freshly pinned buffer
    /// (the caller's array is not retained). A null/empty frame re-seeds the default (never-null).
    /// </summary>
    public void SetFallbackFrame(byte[]? bgra, int width, int height)
    {
        lock (this.reallocLock)
        {
            if (bgra is null || bgra.Length == 0 || width <= 0 || height <= 0)
            {
                this.SeedDefaultFallback(this.liveWidth, this.liveHeight);
                return;
            }

            var stride = width * 4;
            var needed = height * stride;
            var buf = new PinnedBuffer(needed);
            Array.Copy(bgra, buf.Bytes, Math.Min(bgra.Length, needed));

            var old = this.fallback;
            this.fallback = buf;
            this.fallbackWidth = width;
            this.fallbackHeight = height;
            this.fallbackStride = stride;
            old?.Free();
        }
    }

    /// <summary>
    /// (Re)allocate+pin the live <c>_front</c>/<c>_spare</c> double-buffer at the given geometry (D-30).
    /// Called once at construction and again on a geometry change. Frees the prior pins first.
    /// </summary>
    private void AllocateLiveBuffers(int width, int height, int stride = 0)
    {
        if (stride <= 0)
        {
            stride = width * 4;
        }

        var byteCount = height * stride;

        this.front?.Free();
        this.spare?.Free();

        this.front = new PinnedBuffer(byteCount);
        this.spare = new PinnedBuffer(byteCount);
        this.liveWidth = width;
        this.liveHeight = height;
        this.liveStride = stride;
    }

    /// <summary>
    /// Seed (or re-seed) the never-null fallback placeholder: a solid pinned BGRA frame at output
    /// geometry so a Fallback flip can never hand the pump null before Plan 04 supplies the real frame.
    /// A new byte[] is zero-initialized → fully-transparent BGRA (A=0), which is keyer-safe straight
    /// alpha; the placeholder's CONTENT is Plan 04's concern, this only guarantees non-null + correct
    /// geometry/stride.
    /// </summary>
    private void SeedDefaultFallback(int width, int height)
    {
        var stride = width * 4;
        var buf = new PinnedBuffer(height * stride);
        // Zero-init byte[] == fully-transparent straight-alpha BGRA — a safe placeholder.

        var old = this.fallback;
        this.fallback = buf;
        this.fallbackWidth = width;
        this.fallbackHeight = height;
        this.fallbackStride = stride;
        old?.Free();
    }

    /// <summary>
    /// D-26 (full reset wiring lands in Plan 05): unsubscribe from the source and free every pin so the
    /// monitor can be torn down cleanly. Mirrors the frame-source wrapper's dispose shape.
    /// </summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        // D-26: stop the 5 Hz sampler FIRST so no tick fires after teardown, then unsubscribe + free pins.
        this.sampleTimer?.Dispose();
        this.sampleTimer = null;

        this.source.FrameReady -= this.OnFrameReady;

        lock (this.reallocLock)
        {
            this.front?.Free();
            this.spare?.Free();
            this.fallback?.Free();
        }
    }
}
