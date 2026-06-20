using NewTek;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// D-01/D-03/D-30/D-31: the SINGLE-AUTHORITY pump — the SOLE caller of
/// <c>NDIlib.send_send_video_v2</c>. It runs a steady-cadence NON-REENTRANT loop that PULLS
/// <see cref="FrameMonitor.SnapshotCurrentOutput"/> each tick and sends it, REGARDLESS of whether CEF
/// is painting. This is the only topology that keeps a (live-last-good or fallback) frame on air during
/// a no-paint freeze (MON-03) — a buffer-flip-only design cannot, because when paints stop there is
/// nothing to flip.
/// </summary>
/// <remarks>
/// <para>NON-REENTRANT cadence (D-31): the loop <c>await</c>s
/// <see cref="PeriodicTimer.WaitForNextTickAsync"/> at the top of each iteration, so the prior tick's
/// send completes before the next tick begins — overlapping ticks cannot stack (no thread-pool
/// starvation, drift-free). The dedicated-thread + explicit reentrancy-guard fallback is documented for
/// Phase-3 if PeriodicTimer jitter shows (RESEARCH Pitfall P-3); the requirement is NEVER-STOPS, not a
/// perfect metronome.</para>
/// <para>PINNED p_data (D-30): the pump passes the monitor's PINNED buffer address straight as
/// <c>p_data</c> — the pin is held by the monitor across the send (the pump does NOT pin-then-release a
/// local mid-send), so the address is stable across <c>send_send_video_v2</c> (T-2-02-4).</para>
/// <para>The <c>video_frame_v2_t</c> is built BYTE-IDENTICALLY to <see cref="NdiFrameSink.OnFrame"/>
/// (same FourCC_type_BGRA, 60/1 rate, progressive, synthesize timecode, stride, xres/yres) so the
/// <c>--smoke</c> keyable-BGRA gate is re-proven under the rewrite (D-01).</para>
/// <para>Ctor-injected sender ptr (no <c>Program.NdiSenderPtr</c> global read) — injectable + testable,
/// mirroring <see cref="NdiFrameSink"/>.</para>
/// </remarks>
public sealed class FramePump : IAsyncDisposable
{
    private readonly FrameMonitor monitor;
    private readonly nint senderPtr;
    private readonly int frameRateN;
    private readonly int frameRateD;

    private PeriodicTimer? timer;
    private Task? loop;
    private CancellationTokenSource? cts;
    private long framesSent;

    /// <summary>
    /// Constructs the pump over the ctor-injected monitor (the pull source, D-03) and the ctor-injected
    /// NDI sender ptr (D-26 — not the global). Cadence defaults to 60/1 (the locked output rate); a
    /// caller may override for a different output geometry's rate.
    /// </summary>
    public FramePump(FrameMonitor monitor, nint senderPtr, int frameRateN = 60, int frameRateD = 1)
    {
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.senderPtr = senderPtr;
        this.frameRateN = frameRateN;
        this.frameRateD = frameRateD;
    }

    /// <summary>Total frames the pump has sent — Plan 06's <c>/health</c> reads this.</summary>
    public long FramesSent => Interlocked.Read(ref this.framesSent);

    /// <summary>
    /// Start the steady-cadence pull-and-send loop. Idempotent (a second call is a no-op). The loop
    /// runs until <see cref="StopAsync"/> / <see cref="DisposeAsync"/>.
    /// </summary>
    public void Start()
    {
        if (this.loop is not null)
        {
            return;
        }

        if (this.senderPtr == nint.Zero)
        {
            Log.Warning("FramePump.Start — sender ptr is Zero; the pump will not send (no NDI sender).");
        }

        // Period = frameRateD / frameRateN seconds (e.g. 1/60). Guard against a zero rate.
        var period = this.frameRateN > 0
            ? TimeSpan.FromSeconds((double)this.frameRateD / this.frameRateN)
            : TimeSpan.FromMilliseconds(16);

        this.cts = new CancellationTokenSource();
        this.timer = new PeriodicTimer(period);
        this.loop = Task.Run(() => this.RunAsync(this.cts.Token));
    }

    /// <summary>
    /// The NON-REENTRANT pump loop (D-31): await the next tick at the TOP of each iteration so ticks
    /// cannot overlap, then pull-and-send. Per-tick exceptions are caught and logged so a single bad
    /// frame never stalls the sender (never-stops, MON-03 / T-2-02-3).
    /// </summary>
    private async Task RunAsync(CancellationToken token)
    {
        var t = this.timer!;
        try
        {
            while (await t.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    this.SendOneTick();
                }
                catch (Exception ex)
                {
                    // never-stops: log and continue to the next tick (MON-03).
                    Log.Error(ex, "FramePump tick failed — continuing (never-stops, MON-03).");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown.
        }
    }

    /// <summary>
    /// One pump tick: PULL the monitor's current-output buffer and send it. The <c>p_data</c> is the
    /// monitor's PINNED address (D-30); the frame build matches <see cref="NdiFrameSink.OnFrame"/>.
    /// </summary>
    private void SendOneTick()
    {
        if (this.senderPtr == nint.Zero)
        {
            return;
        }

        var frame = this.monitor.SnapshotCurrentOutput(); // never null (FrameMonitor contract)
        if (frame.DataPtr == nint.Zero || frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        var videoFrame = new NDIlib.video_frame_v2_t
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = this.frameRateN,
            frame_rate_D = this.frameRateD,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = frame.Stride,
            picture_aspect_ratio = (float)frame.Width / frame.Height,
            p_data = frame.DataPtr, // the monitor's PINNED buffer — stable across the send (D-30)
            timecode = NDIlib.send_timecode_synthesize,
            xres = frame.Width,
            yres = frame.Height,
        };

        NDIlib.send_send_video_v2(this.senderPtr, ref videoFrame);
        Interlocked.Increment(ref this.framesSent);
    }

    /// <summary>Stop the loop and await its completion.</summary>
    public async Task StopAsync()
    {
        this.cts?.Cancel();
        this.timer?.Dispose();

        if (this.loop is not null)
        {
            try
            {
                await this.loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        this.loop = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        this.cts?.Dispose();
        this.timer = null;
        this.cts = null;
    }
}
