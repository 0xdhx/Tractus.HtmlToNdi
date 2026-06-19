using NewTek;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// The extracted NDI send (D-01). SUBSCRIBES to an <see cref="IFrameSource"/>'s
/// <see cref="IFrameSource.FrameReady"/> event and owns <c>NDIlib.send_send_video_v2</c>. The NDI
/// sender pointer is injected via the CONSTRUCTOR (D-26 — <c>new NdiFrameSink(senderPtr)</c>), NOT
/// read from the composition-root global, so the sink is injectable and unit-testable.
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
