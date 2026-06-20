using System.Runtime.InteropServices;

namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// D-01/D-02/D-05/D-30: the single-authority pump's CONSUMER side. <see cref="FrameMonitor"/>
/// subscribes to an <see cref="IFrameSource"/> (the CefWrapper, ctor-injected — NOT a global,
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

    /// <summary>
    /// Constructs the monitor over a ctor-injected frame source (D-02/D-26 — NOT a global) and
    /// subscribes to <see cref="IFrameSource.FrameReady"/>. The source's current
    /// <see cref="IFrameSource.Width"/>/<see cref="IFrameSource.Height"/> seed the default-frame and the
    /// initial live-buffer geometry; the first paint resizes if CEF reports a different size.
    /// </summary>
    public FrameMonitor(IFrameSource source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));

        var w = source.Width > 0 ? source.Width : 1920;
        var h = source.Height > 0 ? source.Height : 1080;

        this.AllocateLiveBuffers(w, h);
        this.SeedDefaultFallback(w, h);

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
    /// monitor can be torn down cleanly. Mirrors the CefWrapper dispose shape.
    /// </summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.source.FrameReady -= this.OnFrameReady;

        lock (this.reallocLock)
        {
            this.front?.Free();
            this.spare?.Free();
            this.fallback?.Free();
        }
    }
}
