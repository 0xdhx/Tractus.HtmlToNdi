using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// D-18/D-20/D-21/D-33 (MON-02/MON-03): produces the clean BGRA frame the <see cref="FrameMonitor"/>
/// fallback slot holds on air while the live source misbehaves — the LAYERED safety the product's
/// unowned-page resilience rides on.
///
/// <para><b>Layered safety (D-20):</b>
/// <list type="number">
///   <item>(a) the CONFIGURED asset is validated on load — resolved within <c>--fallback-dir</c>,
///   path-traversal guarded (<see cref="Path.GetFullPath(string)"/> containment, threat T-2-04-1),
///   decoded via <see cref="Bitmap"/>, dims asserted == the ACTUAL <c>--w</c>/<c>--h</c> output
///   geometry (D-21), and size-bounded (decode-bomb guard, T-2-04-2);</item>
///   <item>(b) ANY failure (missing / decode error / wrong dims / path-escape / oversized) degrades to
///   an IN-MEMORY generated solid slate/black frame at output geometry — a CLEAN slate, NEVER a
///   blackout, never a crash, never an unhandled throw (D-20b);</item>
///   <item>(c) the substitution is surfaced LOUDLY — a startup Serilog WARN naming the sought asset +
///   a <see cref="FallbackAssetState"/> (<c>configured</c> | <c>generated-default</c>) Plan 06's
///   <c>/health</c> reads (D-20c).</item>
/// </list></para>
///
/// <para><b>Geometry + alpha pin (D-21):</b> the produced <see cref="FallbackFrame"/> is BGRA at the
/// EXACT live output geometry and carries the Plan-01 <see cref="AlphaConvention.Expected"/> alpha mode
/// — byte-identical in format to the live frames so the receiver never resyncs at the switch.</para>
///
/// <para><b>Asset identity (D-33):</b> for <c>policy=slate</c> the chosen file is the recipe's optional
/// <c>fallbackAsset</c> filename when present, else the convention <c>slate.png</c>. <c>black</c>
/// generates an opaque-black frame (no asset). <c>hold-last</c> uses no separate asset — the monitor
/// holds the last-good live frame, so this provider supplies the generated slate only as a never-null
/// seed; the recovery state machine (Plan 05) drives the hold.</para>
///
/// CEF-agnostic (imports no CefSharp type): a pure config/pixel component, ctor-injected with the dir +
/// geometry + alpha mode (no <c>Program</c> global read).
/// </summary>
public sealed class FallbackProvider
{
    /// <summary>D-33 convention: the default slate asset filename when a recipe names none.</summary>
    public const string DefaultSlateAssetName = "slate.png";

    /// <summary>
    /// Decode-bomb guard (T-2-04-2): reject a fallback PNG whose on-disk size exceeds this before
    /// decoding. A legitimate 1080p BGRA PNG is a few MB at most; 64 MiB is a generous ceiling that
    /// still bounds a maliciously-crafted decode bomb well below a DoS-relevant size.
    /// </summary>
    public const long MaxAssetBytes = 64L * 1024 * 1024;

    private readonly string fallbackDir;
    private readonly int outputWidth;
    private readonly int outputHeight;
    private readonly AlphaMode alphaMode;

    /// <param name="fallbackDir">The resolved <c>--fallback-dir</c> (bundle-relative default).</param>
    /// <param name="outputWidth">The ACTUAL live output width (<c>--w</c>, D-21).</param>
    /// <param name="outputHeight">The ACTUAL live output height (<c>--h</c>, D-21).</param>
    /// <param name="alphaMode">The Plan-01 determined alpha convention the live frames carry (D-21).</param>
    public FallbackProvider(string fallbackDir, int outputWidth, int outputHeight, AlphaMode alphaMode)
    {
        if (outputWidth <= 0 || outputHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputWidth), "fallback output geometry must be positive");
        }

        this.fallbackDir = fallbackDir ?? string.Empty;
        this.outputWidth = outputWidth;
        this.outputHeight = outputHeight;
        this.alphaMode = alphaMode;
    }

    /// <summary>
    /// Resolve + validate the configured asset for the given policy, else GENERATE the ultimate
    /// fallback. NEVER throws and NEVER returns a null frame — the worst case is a generated slate
    /// surfaced as <see cref="FallbackAssetState.GeneratedDefault"/> (D-20b).
    /// </summary>
    /// <param name="fallbackPolicy">The recipe <c>fallbackPolicy</c> keyword (slate|black|hold-last,
    /// normalized to <c>slate</c> when null by the validator).</param>
    /// <param name="recipeAsset">The recipe's optional <c>fallbackAsset</c> filename (validated bare
    /// filename), or <c>null</c> to use the <c>slate.png</c> convention.</param>
    public FallbackResult LoadOrGenerate(string? fallbackPolicy, string? recipeAsset)
    {
        var policy = string.IsNullOrWhiteSpace(fallbackPolicy)
            ? "slate"
            : fallbackPolicy.Trim().ToLowerInvariant();

        // black: a generated opaque-black frame, no asset. hold-last: the monitor holds the live
        // last-good frame; supply a generated slate purely as a never-null seed for the slot.
        if (policy == "black")
        {
            return new FallbackResult(
                this.GenerateSolid(0, 0, 0, 255),
                FallbackAssetState.GeneratedDefault,
                sought: "(generated black — policy=black)");
        }

        if (policy == "hold-last")
        {
            return new FallbackResult(
                this.GenerateSlate(),
                FallbackAssetState.GeneratedDefault,
                sought: "(generated slate seed — policy=hold-last holds the live last-good frame)");
        }

        // policy=slate (default): the recipe fallbackAsset filename if present, else slate.png (D-33).
        var assetName = string.IsNullOrWhiteSpace(recipeAsset) ? DefaultSlateAssetName : recipeAsset;

        if (this.TryLoadAsset(assetName, out var loaded, out var reason) && loaded.HasValue)
        {
            return new FallbackResult(loaded.Value, FallbackAssetState.Configured, sought: assetName);
        }

        // D-20b/c: degrade to the generated slate, LOUDLY.
        Log.Warning(
            "FALLBACK ASSET substituted — sought '{Asset}' in '{Dir}' but {Reason}; using the generated "
            + "default slate ({Width}x{Height}). /health will report fallbackAsset=generated-default.",
            assetName, this.fallbackDir, reason, this.outputWidth, this.outputHeight);

        return new FallbackResult(this.GenerateSlate(), FallbackAssetState.GeneratedDefault, sought: assetName);
    }

    /// <summary>
    /// D-20a/D-33: resolve <paramref name="assetName"/> within <see cref="fallbackDir"/> (path-traversal
    /// guarded), size-bound it, decode it, and assert dims == output geometry. Returns <c>false</c> with
    /// a <paramref name="reason"/> on ANY failure (the caller degrades to the generated default).
    /// </summary>
    private bool TryLoadAsset(string assetName, out FallbackFrame? frame, out string reason)
    {
        frame = null;

        if (string.IsNullOrWhiteSpace(this.fallbackDir) || !Directory.Exists(this.fallbackDir))
        {
            reason = "the fallback directory does not exist";
            return false;
        }

        // PATH-TRAVERSAL GUARD (T-2-04-1, defence-in-depth on top of the Task-1 recipe-field sanity):
        // canonicalize both the dir and the candidate, then assert the candidate stays inside the dir.
        string dirFull, candidateFull;
        try
        {
            dirFull = Path.GetFullPath(this.fallbackDir);
            candidateFull = Path.GetFullPath(Path.Combine(dirFull, assetName));
        }
        catch (Exception ex)
        {
            reason = $"the asset path could not be resolved: {ex.Message}";
            return false;
        }

        var dirWithSep = dirFull.EndsWith(Path.DirectorySeparatorChar)
            ? dirFull
            : dirFull + Path.DirectorySeparatorChar;
        if (!candidateFull.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
        {
            reason = "the resolved asset path escapes the fallback directory (traversal rejected)";
            return false;
        }

        if (!File.Exists(candidateFull))
        {
            reason = "the asset file does not exist";
            return false;
        }

        // DECODE-BOMB GUARD (T-2-04-2): bound the on-disk size before handing it to the decoder.
        try
        {
            var len = new FileInfo(candidateFull).Length;
            if (len > MaxAssetBytes)
            {
                reason = $"the asset is oversized ({len} bytes > {MaxAssetBytes}-byte cap)";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"the asset size could not be read: {ex.Message}";
            return false;
        }

        try
        {
            // System.Drawing is in-box on net8.0-windows (the locked TFM); already used by CefWrapper.
            using var bitmap = new Bitmap(candidateFull);

            if (bitmap.Width != this.outputWidth || bitmap.Height != this.outputHeight)
            {
                reason =
                    $"the asset dims ({bitmap.Width}x{bitmap.Height}) != the output geometry "
                    + $"({this.outputWidth}x{this.outputHeight})";
                return false;
            }

            frame = this.ToFallbackFrame(bitmap);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            // ANY decode failure (corrupt PNG, unsupported format, GDI+ error) → degrade (D-20b).
            reason = $"the asset failed to decode: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Convert a decoded <see cref="Bitmap"/> into a BGRA <see cref="FallbackFrame"/> at output geometry.
    /// <see cref="PixelFormat.Format32bppArgb"/> LockBits yields B,G,R,A byte order on little-endian
    /// Windows — the same BGRA the live send path uses. The bytes are STRAIGHT alpha (the live
    /// convention, <see cref="AlphaConvention.Expected"/>); GDI+ does not premultiply
    /// <c>Format32bppArgb</c>.
    /// </summary>
    private FallbackFrame ToFallbackFrame(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = this.outputWidth * 4;
            var bytes = new byte[this.outputHeight * stride];
            // GDI+ stride may be padded/negative; copy row-by-row into the tight BGRA buffer.
            var srcStride = data.Stride;
            var scan0 = data.Scan0;
            for (var y = 0; y < this.outputHeight; y++)
            {
                var srcRow = scan0 + (y * srcStride);
                Marshal.Copy(srcRow, bytes, y * stride, stride);
            }

            return new FallbackFrame(bytes, this.outputWidth, this.outputHeight, stride, this.alphaMode);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// D-20b: the generated ultimate fallback for <c>policy=slate</c> — a solid muted slate-grey opaque
    /// BGRA frame at output geometry. A clean, obviously-non-program "slate" the operator recognizes,
    /// never a blackout. (Exact slate appearance is Claude's Discretion.)
    /// </summary>
    private FallbackFrame GenerateSlate() => this.GenerateSolid(64, 64, 64, 255);

    /// <summary>Generate a solid-color BGRA frame at output geometry (straight alpha, D-21).</summary>
    private FallbackFrame GenerateSolid(byte b, byte g, byte r, byte a)
    {
        var stride = this.outputWidth * 4;
        var bytes = new byte[this.outputHeight * stride];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = b;
            bytes[i + 1] = g;
            bytes[i + 2] = r;
            bytes[i + 3] = a;
        }

        return new FallbackFrame(bytes, this.outputWidth, this.outputHeight, stride, this.alphaMode);
    }
}

/// <summary>
/// D-20c: whether the fallback frame is the operator-CONFIGURED asset or the in-memory
/// GENERATED-DEFAULT substitution. Plan 06's <c>/health</c> reports this read-only (threat T-2-04-4 —
/// no path, no secret).
/// <para>RC-2 (02-07 / D-32): serialize as a kebab-lower STRING token (<c>configured</c> /
/// <c>generated-default</c>), never the System.Text.Json numeric default — completing the 3-of-3
/// <c>/health</c> string-enums (MonitorStatus + FrameSource already carry this converter). A watchdog
/// reads the string, not the integer 1 the live 02-UAT.md capture leaked.</para>
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(KebabCaseStringEnumConverter))]
public enum FallbackAssetState
{
    /// <summary>The configured asset loaded + validated cleanly.</summary>
    Configured,

    /// <summary>The asset was missing/invalid → the generated ultimate fallback was substituted (D-20b).</summary>
    GeneratedDefault,
}

/// <summary>
/// D-33: a self-describing fallback BGRA frame carrying its geometry + the alpha convention (NOT raw
/// bytes alone), consistent with the Plan-02 <see cref="FrameMonitor.FrameBuffer"/> shape (D-21/D-29).
/// The bytes are copied into the <see cref="FrameMonitor"/>'s pinned fallback slot via
/// <see cref="FrameMonitor.SetFallbackFrame"/>.
/// </summary>
public readonly struct FallbackFrame
{
    /// <summary>The tight BGRA bytes (<see cref="Height"/> * <see cref="Stride"/>).</summary>
    public readonly byte[] Bgra;

    /// <summary>Frame width in pixels (== the live output width, D-21).</summary>
    public readonly int Width;

    /// <summary>Frame height in pixels (== the live output height, D-21).</summary>
    public readonly int Height;

    /// <summary>Row stride in bytes (<see cref="Width"/> * 4 for BGRA).</summary>
    public readonly int Stride;

    /// <summary>The alpha convention these bytes carry — MUST equal the live convention (D-21).</summary>
    public readonly AlphaMode AlphaMode;

    public FallbackFrame(byte[] bgra, int width, int height, int stride, AlphaMode alphaMode)
    {
        this.Bgra = bgra;
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
        this.AlphaMode = alphaMode;
    }
}

/// <summary>The produced fallback frame + its substitution state + the asset name sought (for logging).</summary>
public readonly struct FallbackResult
{
    /// <summary>The clean BGRA fallback frame (NEVER null — generated when no asset, D-20b).</summary>
    public readonly FallbackFrame Frame;

    /// <summary>Whether <see cref="Frame"/> is the configured asset or the generated default (D-20c).</summary>
    public readonly FallbackAssetState State;

    /// <summary>The asset name/description sought (surfaced in the startup log).</summary>
    public readonly string Sought;

    public FallbackResult(FallbackFrame frame, FallbackAssetState state, string sought)
    {
        this.Frame = frame;
        this.State = state;
        this.Sought = sought;
    }
}
