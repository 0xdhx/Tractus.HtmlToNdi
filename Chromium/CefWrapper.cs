
using CefSharp;
using CefSharp.OffScreen;
using NewTek;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private Thread RenderWatchdog;
    private DateTime lastPaint = DateTime.MinValue;

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

        this.browser = new ChromiumWebBrowser(initialUrl)
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);

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

        await this.browser.WaitForInitialLoadAsync();

        this.browser.GetBrowserHost().WindowlessFrameRate = 60;
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
        this.RenderWatchdog.Start();
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

        var videoFrame = new NDIlib.video_frame_v2_t()
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = 60,
            frame_rate_D = 1,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = e.Width * 4,
            picture_aspect_ratio = (float)e.Width / e.Height,
            p_data = e.BufferHandle,
            timecode = NDIlib.send_timecode_synthesize,
            xres = e.Width,
            yres = e.Height,
        };

        NDIlib.send_send_video_v2(Program.NdiSenderPtr, ref videoFrame);

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
                if (this.browser is not null)
                {
                    this.browser.Paint -= this.OnBrowserPaint;
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

    public void SetUrl(string url)
    {
        if (this.browser is null)
        {
            return;
        }

        this.Url = url;

        this.browser.Load(url);
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
