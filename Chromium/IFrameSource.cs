namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// A render-engine-agnostic view of a single rendered frame, carried by the
/// <see cref="IFrameSource.FrameReady"/> event (D-25). The fields are primitives only —
/// no render-engine paint-args type crosses this seam (INJ-06 / Pitfall 2).
/// </summary>
/// <remarks>
/// D-01 SEAM CONTRACT — the <see cref="Buffer"/> pointer is CALLBACK-SCOPED: it is only valid
/// for the duration of the <see cref="IFrameSource.FrameReady"/> invocation. Subscribers that
/// send/inspect IN-CALL (e.g. <see cref="NdiFrameSink"/>) may use it directly. Any subscriber
/// that RETAINS the frame past the call (e.g. the Phase-2 monitor, which compares across frames)
/// MUST copy the bytes out before returning (Pitfall 3).
/// </remarks>
public readonly struct FrameView
{
    /// <summary>Pointer to the BGRA pixel buffer. CALLBACK-SCOPED — see the type remarks.</summary>
    public readonly nint Buffer;

    /// <summary>Frame width in pixels.</summary>
    public readonly int Width;

    /// <summary>Frame height in pixels.</summary>
    public readonly int Height;

    /// <summary>Row stride in bytes (typically <c>Width * 4</c> for BGRA).</summary>
    public readonly int Stride;

    public FrameView(nint buffer, int width, int height, int stride)
    {
        this.Buffer = buffer;
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
    }
}

/// <summary>
/// The CEF-agnostic SOURCE seam (D-08 / D-25 / INJ-06). A frame source RAISES
/// <see cref="FrameReady"/> for each rendered frame; consumers (<see cref="NdiFrameSink"/> and the
/// future Phase-2 monitor) SUBSCRIBE. The source has NO consume method — receiving a frame is a
/// SINK responsibility (D-25 corrects the earlier muddled polarity). A deferred Phase-5
/// PlaywrightFrameSource implements this same interface, so the seam stays engine-agnostic.
/// </summary>
/// <remarks>
/// This file MUST NOT import the render-engine binding namespace — that import is the Pitfall 2
/// canary for an engine type leaking across the seam (INJ-06).
/// </remarks>
public interface IFrameSource
{
    /// <summary>
    /// Raised once per rendered frame, carrying a primitive <see cref="FrameView"/>.
    /// </summary>
    /// <remarks>
    /// D-01 SEAM CONTRACT — the <see cref="FrameView.Buffer"/> pointer is CALLBACK-SCOPED: it is
    /// valid ONLY for the duration of the handler invocation. Subscribers must send/inspect in-call;
    /// any subscriber that retains the frame past the call MUST copy the bytes first (Pitfall 3 —
    /// this is exactly why the Phase-2 monitor, which compares across frames, must copy).
    /// </remarks>
    event Action<FrameView> FrameReady;

    /// <summary>Current frame width in pixels.</summary>
    int Width { get; }

    /// <summary>Current frame height in pixels.</summary>
    int Height { get; }

    /// <summary>
    /// Liveness clock: the timestamp of the most recent paint. Rides the existing <c>lastPaint</c>
    /// clock (NOT <c>NDIlib_send_get_no_connections</c>) — see RESEARCH "State of the Art".
    /// </summary>
    DateTime LastPaint { get; }

    /// <summary>Navigate the source to a new URL.</summary>
    void SetUrl(string url);
}
