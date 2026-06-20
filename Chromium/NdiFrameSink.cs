using NewTek;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// SUPERSEDED by the single-authority pump (Plan 02-02 / D-01/D-03). This PUSH sink (it auto-sent
/// in-call from <see cref="IFrameSource.FrameReady"/>) is NO LONGER WIRED into the composition root:
/// <see cref="FramePump"/> is now the SOLE <c>NDIlib.send_send_video_v2</c> caller, PULLING the
/// monitor's current-output buffer on a steady cadence so NDI never stops during a no-paint freeze
/// (MON-03). This class is retained as the canonical reference for the BGRA <c>video_frame_v2_t</c>
/// build the pump mirrors byte-identically (<see cref="OnFrame"/>); it is intentionally DEAD CODE in
/// the live pipeline — do NOT re-attach it (double-send + loss of freeze-survival, the listed
/// anti-pattern). Originally: the extracted NDI send (D-01), sender ptr ctor-injected (D-26).
/// </summary>
/// <remarks>
/// D-01 SEAM CONTRACT — the frame buffer is CALLBACK-SCOPED. This sink sends IN-CALL (as the
/// upstream <c>OnBrowserPaint</c> did) and never stashes the pointer past the handler (Pitfall 3).
/// This file MUST NOT reference any render-engine binding type (INJ-06).
/// </remarks>
public sealed class NdiFrameSink
{
    private readonly nint senderPtr;

    /// <summary>
    /// Constructs the sink with its NDI sender pointer (D-26 — injected, not the global).
    /// </summary>
    /// <param name="senderPtr">The <c>NDIlib.send_create</c> sender pointer to send frames through.</param>
    public NdiFrameSink(nint senderPtr)
    {
        this.senderPtr = senderPtr;
    }

    /// <summary>
    /// Subscribes this sink to a frame source so each raised frame is sent over NDI.
    /// </summary>
    public void AttachTo(IFrameSource source)
    {
        source.FrameReady += this.OnFrame;
    }

    /// <summary>
    /// Detaches this sink from a frame source (mirror of <see cref="AttachTo"/>).
    /// </summary>
    public void DetachFrom(IFrameSource source)
    {
        source.FrameReady -= this.OnFrame;
    }

    /// <summary>
    /// Frame-ready handler — builds the BGRA <c>video_frame_v2_t</c> and sends it IN-CALL through
    /// the ctor-injected sender. Identical frame math to the extracted CefWrapper.cs:116-130 block.
    /// </summary>
    private void OnFrame(FrameView view)
    {
        if (this.senderPtr == nint.Zero)
        {
            return;
        }

        var videoFrame = new NDIlib.video_frame_v2_t()
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = 60,
            frame_rate_D = 1,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = view.Stride,
            picture_aspect_ratio = (float)view.Width / view.Height,
            p_data = view.Buffer,
            timecode = NDIlib.send_timecode_synthesize,
            xres = view.Width,
            yres = view.Height,
        };

        NDIlib.send_send_video_v2(this.senderPtr, ref videoFrame);
    }
}
