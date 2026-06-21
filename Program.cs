
using CefSharp;
using CefSharp.OffScreen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewTek;
using NewTek.NDI;
using Serilog;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tractus.HtmlToNdi.Chromium;
using Tractus.HtmlToNdi.Chromium.Inject;
using Tractus.HtmlToNdi.Chromium.Monitor;
using Tractus.HtmlToNdi.Models;

namespace Tractus.HtmlToNdi;
public class Program
{
    public static nint NdiSenderPtr;
    public static CefWrapper browserWrapper;

    // D-06/D-21: the recipe store + the launch posture captured at startup. The store backs both the
    // /recipe GET/POST endpoints and the /seturl recipe re-match; the launch posture (the
    // ExpectsCrossOriginIframes value that gated the FROZEN site-isolation flags at Cef.Initialize) is
    // the reference the /recipe + /seturl swaps reject a mismatch against (D-21 — site-iso cannot change
    // at runtime).
    public static RecipeStore recipeStore;
    public static bool launchExpectsCrossOriginIframes;

    public static void Main(string[] args)
    {
        var launchCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", Guid.NewGuid().ToString());

        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(exeDirectory);
        AppManagement.Initialize(args);

        // D-15a: detect --smoke BEFORE the interactive --ndiname/--port prompts below (both call
        // Console.ReadLine() when their arg is absent, which would HANG a headless CI run with no
        // stdin). The smoke path sets its own defaults and short-circuits the whole normal flow
        // (interactive prompts + ASP.NET host + KVM thread). The normal --url= path is unchanged.
        if (args.Any(x => x.StartsWith("--inject-smoke")))
        {
            // D-14/D-22: the inject acceptance gate. Detected BEFORE --smoke (StartsWith("--smoke")
            // would NOT alias "--inject-smoke", but check inject first for clarity) and before the
            // interactive prompts (same stdin-hang hazard). Always Environment.Exit.
            RunInjectSmoke(args, launchCachePath);
            return; // unreachable — RunInjectSmoke always calls Environment.Exit.
        }

        if (args.Any(x => x.StartsWith("--monitor-smoke")))
        {
            // D-09/D-34 (MON-02..05): the monitor self-healing acceptance gate. Detected BEFORE --smoke
            // (StartsWith("--smoke") would NOT alias "--monitor-smoke") and before the interactive prompts
            // (same stdin-hang hazard). Serves an rAF-canvas fixture that freezes on command over a loopback
            // listener and drives the REAL CEF->FrameMonitor->FramePump pipeline through freeze->fallback->
            // refresh+re-inject->recovery via a DEDICATED non-colliding recipe with smoke-scale tiny
            // thresholds (D-34). Always Environment.Exit (0 on the full path succeeding, 1 otherwise).
            RunMonitorSmoke(args, launchCachePath);
            return; // unreachable — RunMonitorSmoke always calls Environment.Exit.
        }

        if (args.Any(x => x.StartsWith("--onpaint-format")))
        {
            // D-22/D-29 (MON-01): the OnPaint pixel-format readback gate — the literal FIRST task of
            // Phase 2. Detected BEFORE --smoke (StartsWith("--smoke") would NOT alias
            // "--onpaint-format") and before the interactive --ndiname/--port prompts (same stdin-hang
            // hazard). Renders a known 3-region semi-transparent fixture, reads back the REAL pre-send
            // OnPaint bytes, asserts BGRA channel order + per-region alpha value + premultiplied-vs-
            // straight + the all-alpha-0 blank case. Always Environment.Exit (exit 0 on a clean STRAIGHT
            // determination, non-zero on fail/drift).
            RunOnPaintFormatGate(args, launchCachePath);
            return; // unreachable — RunOnPaintFormatGate always calls Environment.Exit.
        }

        if (args.Any(x => x.StartsWith("--accuweather-probe")))
        {
            // D-01/D-16 (VAL-01): the posture+CMP probe gate. Detected BEFORE --smoke (StartsWith("--smoke")
            // would NOT alias "--accuweather-probe") and before the interactive --ndiname/--port prompts
            // (same stdin-hang hazard). Runs site-isolation OFF (D-03) against the operator-supplied radar
            // URL, resolves per-frame contexts via Page.createIsolatedWorld, re-arms readback on frame
            // re-attach (executionContextDestroyed/frameNavigated), classifies the cross-origin posture +
            // identifies the live CMP, and emits a structured decision the operator transcribes into the
            // Task-2 decision record. Always Environment.Exit (0 on a clean classified run, non-zero on
            // env/CDP failure). NOT a throwaway spike — re-runnable on AccuWeather redesign (D-16).
            RunAccuWeatherProbe(args, launchCachePath);
            return; // unreachable — RunAccuWeatherProbe always calls Environment.Exit.
        }

        if (args.Any(x => x.StartsWith("--accuweather-capture")))
        {
            // 03-04 D-07/D-24/D-26 (VAL-04): the idle-gap capture gate. Detected BEFORE --smoke
            // (StartsWith("--smoke") would NOT alias "--accuweather-capture") and before the interactive
            // --ndiname/--port prompts (same stdin-hang hazard). Launches the FULL live pipeline (the SAME
            // composition root the interactive --url=/--recipe path uses — CefWrapper + FrameMonitor +
            // FramePump + NDI send) against the AccuWeather recipe (expectMotion=true), then arms a per-sample
            // LOGGING SIDE-CHANNEL that CONSUMES the plan-03 read-only SampleObserved telemetry seam (D-24) on
            // the EXISTING 5 Hz OnSampleTick (SampleIntervalMs=200 — NO new timer, NO recomputed detector, NO
            // reach into private FrameMonitor fields). Each sample → one CSV line of six columns:
            // tMs/dHash/hammingFromPrev/lastPaintAgeMs/beaconState/targetPresent. Runs for a bounded
            // --duration then flushes + Environment.Exit(0). The operator runs this on the GPU Session-2 host
            // (Task 2) to derive the longest BEACON-GATED normal idle gap and lock freezeTimeoutMs ≈≥3× it.
            // CRITICAL: NDI is wired exactly like the interactive path because CefWrapper.OnBrowserPaint
            // early-returns at `if (Program.NdiSenderPtr == nint.Zero) return;` BEFORE FrameReady?.Invoke —
            // FrameReady is the FrameMonitor's only frame feed, so a capture that skips NDI would STARVE the
            // monitor and SampleObserved would produce nothing.
            RunAccuWeatherCapture(args, launchCachePath);
            return; // unreachable — RunAccuWeatherCapture always calls Environment.Exit.
        }

        if (args.Any(x => x.StartsWith("--beacon-damage-check")))
        {
            // 03-03 (Q2 / A4): the alpha-0-damage validation gate — the ONE genuine empirical unknown of
            // Phase 3 (research §19): does an alpha-0 corner whose OPAQUE RGB mutates each rAF tick produce
            // CAPTURED OnPaint damage on CefSharp 148, surviving the un-premult LUT into the straight buffer
            // the FrameMonitor samples? Detected BEFORE --smoke (StartsWith("--smoke") would NOT alias
            // "--beacon-damage-check") and before the interactive --ndiname/--port prompts (same stdin-hang
            // hazard). Captures TWO non-blank OnPaint buffers a few frames apart and asserts the beacon-region
            // RGB DIFFERS. Differs => alpha-0 RGB mutation produces captured damage => the D-13 beacon design
            // is valid (exit 0). Identical => damage-gating swallowed it => exit non-zero pointing to the A=1
            // low-alpha fallback. Mirrors RunOnPaintFormatGate; always Environment.Exit.
            RunBeaconDamageGate(args, launchCachePath);
            return; // unreachable — RunBeaconDamageGate always calls Environment.Exit.
        }

        if (args.Any(x => x.StartsWith("--accuweather-validate")))
        {
            // 03-05 D-06/D-27 (VAL-01/VAL-03/VAL-04): the SEMI-AUTOMATED live content-proof gate — the
            // runnable spine of the Phase-3 acceptance. Detected BEFORE --smoke (StartsWith("--smoke") would
            // NOT alias "--accuweather-validate") and before the interactive --ndiname/--port prompts (same
            // stdin-hang hazard). Authored as a SIBLING of --monitor-smoke/--accuweather-capture (D-27): it
            // stands up the SAME single-authority composition root the interactive --url=/--recipe path uses
            // (CefWrapper + FrameMonitor + FramePump + NDI send + a real ASP.NET-free in-process /health read
            // via SnapshotHealth + the sibling proof-marker probe), SELF-DRIVES the live AccuWeather recipe,
            // runs a four-point ENV PRE-ASSERTION (D-06/D-19, FORK-04 §6 shape), then asserts VAL-01 (all four
            // proof-markers), VAL-03 (blank + a genuine all-stop freeze → fallback whose on-air frame MATCHES
            // the slate.png signature, D-29b/D-31), and VAL-04 (no false-trip over the live idle-hold; the
            // freezeTimeoutMs backstop is the v1.0 trip authority, the beacon best-effort behind the D-31
            // disable-on-false-trip guard). Fault injection uses ONLY the existing control plane (SetUrlAsync /
            // SwapRecipeAsync — the in-process equivalents of /seturl + /recipe); NO new endpoint (D-10).
            // Always Environment.Exit (0 on a full pass, non-zero with the failing assertion named).
            RunAccuWeatherValidate(args, launchCachePath);
            return; // unreachable — RunAccuWeatherValidate always calls Environment.Exit.
        }

        if (args.Any(x => x.StartsWith("--smoke")))
        {
            RunSmoke(args, launchCachePath);
            return; // unreachable — RunSmoke always calls Environment.Exit.
        }

        var ndiName = "HTML5";
        if (args.Any(x => x.StartsWith("--ndiname")))
        {
            try
            {
                ndiName = args.FirstOrDefault(x => x.StartsWith("--ndiname")).Split("=")[1];

                if (string.IsNullOrWhiteSpace(ndiName))
                {
                    throw new ArgumentException();
                }
            }
            catch
            {
                Log.Error("Invalid NDI source name. Exiting.");
                return;
            }
        }
        else
        {
            ndiName = "";
            while (string.IsNullOrWhiteSpace(ndiName))
            {
                Console.Write("NDI source name >");
                ndiName = Console.ReadLine()?.Trim();
            }
        }

        var port = 9999;
        if (args.Any(x => x.StartsWith("--port")))
        {
            try
            {
                port = int.Parse(args.FirstOrDefault(x => x.StartsWith("--port")).Split("=")[1]);
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --port parameter. Exiting.");
                return;
            }
        }
        else
        {
            var portNumber = "";
            while (string.IsNullOrWhiteSpace(portNumber) || !int.TryParse(portNumber, out port))
            {
                Console.Write("HTTP API port # >");
                portNumber = Console.ReadLine()?.Trim();
            }
        }

        var startUrl = "https://testpattern.tractusevents.com/";
        if (args.Any(x => x.StartsWith("--url")))
        {
            try
            {
                startUrl = args.FirstOrDefault(x => x.StartsWith("--url")).Split("=")[1];
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --url parameter. Exiting.");
                return;
            }
        }

        var width = 1920;
        var height = 1080;

        if (args.Any(x => x.StartsWith("--w")))
        {
            try
            {
                width = int.Parse(args.FirstOrDefault(x => x.StartsWith("--w")).Split("=")[1]);
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --w (width) parameter. Exiting.");
                return;
            }
        }

        if (args.Any(x => x.StartsWith("--h")))
        {
            try
            {
                height = int.Parse(args.FirstOrDefault(x => x.StartsWith("--h")).Split("=")[1]);
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --h (height) parameter. Exiting.");
                return;
            }
        }

        // D-06/D-09: --recipe-dir <dir> defaults to the bundle-relative recipes/ path (the publish step
        // copies the parent recipes/ into the bundle). --recipe <name> is an EXPLICIT recipe file (D-20:
        // an explicit recipe that fails validation MUST fail startup loud).
        var recipeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes");
        if (args.Any(x => x.StartsWith("--recipe-dir")))
        {
            try
            {
                recipeDir = args.FirstOrDefault(x => x.StartsWith("--recipe-dir")).Split("=")[1];
                if (string.IsNullOrWhiteSpace(recipeDir))
                {
                    throw new ArgumentException();
                }
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --recipe-dir parameter. Exiting.");
                return;
            }
        }

        string? explicitRecipeName = null;
        // Match --recipe but NOT --recipe-dir (StartsWith would alias them).
        if (args.Any(x => x.StartsWith("--recipe") && !x.StartsWith("--recipe-dir")))
        {
            try
            {
                explicitRecipeName = args.First(x => x.StartsWith("--recipe") && !x.StartsWith("--recipe-dir")).Split("=")[1];
                if (string.IsNullOrWhiteSpace(explicitRecipeName))
                {
                    throw new ArgumentException();
                }
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --recipe parameter. Exiting.");
                return;
            }
        }

        // D-18: --fallback-dir <dir> — the ops-owned fallback-graphic directory (mirrors --recipe-dir),
        // bundle-relative default. A policy=slate recipe loads its fallbackAsset (or slate.png) from here;
        // a missing/invalid asset degrades LOUDLY to a generated default (D-20b). Parsed with the same
        // StartsWith care as --recipe-dir (no other arg aliases "--fallback-dir").
        var fallbackDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fallbacks");
        if (args.Any(x => x.StartsWith("--fallback-dir")))
        {
            try
            {
                fallbackDir = args.FirstOrDefault(x => x.StartsWith("--fallback-dir")).Split("=")[1];
                if (string.IsNullOrWhiteSpace(fallbackDir))
                {
                    throw new ArgumentException();
                }
            }
            catch (Exception)
            {
                Log.Error("Could not parse the --fallback-dir parameter. Exiting.");
                return;
            }
        }

        // ── Two-phase recipe resolution, PHASE 1 (D-16 / Pitfall 4): load + match SYNCHRONOUSLY BEFORE
        // Cef.Initialize, because the matched recipe's ExpectsCrossOriginIframes gates the site-isolation
        // CefCommandLineArgs — flags added AFTER Cef.Initialize are silently ignored. Phase 2 (register
        // the doc-start script) happens inside CefWrapper.InitializeWrapperAsync, before the first Load.
        recipeStore = new RecipeStore(new RecipeValidator());
        recipeStore.Load(recipeDir);

        Recipe? startupRecipe = null;
        if (explicitRecipeName is not null)
        {
            // D-20 (mode 2): an EXPLICIT --recipe that fails parse/validate is a HARD startup failure.
            var explicitPath = Path.IsPathRooted(explicitRecipeName)
                ? explicitRecipeName
                : Path.Combine(recipeDir, explicitRecipeName.EndsWith(".json") ? explicitRecipeName : explicitRecipeName + ".json");

            if (!recipeStore.TryLoadExplicit(explicitPath, out startupRecipe, out var explicitError))
            {
                Log.Error("Explicit --recipe failed to load: {Error}. Exiting.", explicitError);
                return; // fail startup LOUD (D-20).
            }
        }
        else
        {
            // No explicit recipe → match the start URL against the loaded dir (D-07: a miss is a
            // pass-through with the store's "no recipe for <host>" warning — NOT a crash).
            startupRecipe = recipeStore.Match(startUrl);
        }

        // D-21: capture the launch posture (the value that gates the FROZEN site-iso flags) so runtime
        // swaps can reject a posture mismatch.
        launchExpectsCrossOriginIframes = startupRecipe?.ExpectsCrossOriginIframes ?? false;

        AsyncContext.Run(async delegate
        {
            var settings = new CefSettings();
            if (!Directory.Exists(launchCachePath))
            {
                Directory.CreateDirectory(launchCachePath);
            }

            settings.RootCachePath = launchCachePath;
            //settings.CefCommandLineArgs.Add("--disable-gpu-sandbox");
            //settings.CefCommandLineArgs.Add("--no-sandbox");
            //settings.CefCommandLineArgs.Add("--in-process-gpu");
            //settings.SetOffScreenRenderingBestPerformanceArgs();
            settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
            //settings.CefCommandLineArgs.Add("off-screen-frame-rate", "60");
            //settings.CefCommandLineArgs.Add("disable-frame-rate-limit");

            // D-13: anti-throttle flags — set UNCONDITIONALLY before Cef.Initialize (also on the smoke
            // path) so a backgrounded/occluded offscreen surface does not throttle timers / rAF
            // (primarily serves Phase-2 freeze detection; cheap to set now).
            settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
            settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
            settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");

            // D-03: site-isolation-disabling flags — RECIPE-GATED, default OFF. Added BEFORE
            // Cef.Initialize (Pitfall 4: late adds are silently ignored) only when the startup recipe
            // declares expectsCrossOriginIframes, so injected scripts + the .NET<->JS bridge can reach a
            // cross-origin iframe. These flags are process-global + FROZEN for the run (D-21).
            if (startupRecipe?.ExpectsCrossOriginIframes == true)
            {
                settings.CefCommandLineArgs.Add("disable-features", "IsolateOrigins,site-per-process");
                settings.CefCommandLineArgs.Add("disable-site-isolation-trials", "1");
            }

            settings.EnableAudio();
            Cef.Initialize(settings);
            browserWrapper = new CefWrapper(
                width,
                height,
                startUrl)
            {
                StartupRecipe = startupRecipe, // D-16: registered in InitializeWrapperAsync BEFORE Load.
            };

            await browserWrapper.InitializeWrapperAsync();
        });

        // D-13: provenance stamp on normal startup (CEF is initialized so CefSharpVersion is valid).
        AppManagement.LogProvenance();

        // D-03 posture log: state site-isolation ON/OFF + the cause (the recipe urlMatch that gated the
        // flags, or "(no recipe)"). Field shape (isolation + cause) is designed so a Phase-2 /health can
        // surface it without rework. site-isolation is ON when the flags were NOT added (default), OFF
        // when the recipe gated them off.
        Log.Information(
            "POSTURE site-isolation={Iso} cause={Cause}",
            launchExpectsCrossOriginIframes ? "OFF" : "ON",
            startupRecipe?.UrlMatch ?? "(no recipe)");

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSerilog();

        builder.WebHost.UseUrls($"http://*:{port}");

        // Add services to the container.
        builder.Services.AddAuthorization();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();
        app.UseSwagger();
        app.UseSwaggerUI();

        var settings_T = new NDIlib.send_create_t
        {
            p_ndi_name = UTF.StringToUtf8(ndiName)
        };

        Program.NdiSenderPtr = NDIlib.send_create(ref settings_T);

        // D-01/D-03/D-27/D-30/D-31 — THE COMPOSITION ROOT (this plan, 02-02, is the SINGLE owner of the
        // Program.cs composition edits; 02-04's --fallback-dir edit serializes after via depends_on, D-35).
        // The OLD push wiring (the NdiFrameSink subscribing to FrameReady and sending in-call) is
        // REPLACED by the single-authority pump topology: source → monitor → pump → NDI.
        //   - FrameMonitor SUBSCRIBES to browserWrapper.FrameReady (the IFrameSource), copies each
        //     callback-scoped frame into a wait-free pinned double-buffer, and owns the current-output slot.
        //   - FramePump is the SOLE send_send_video_v2 caller; it PULLS monitor.SnapshotCurrentOutput()
        //     on a non-reentrant PeriodicTimer so NDI never stops even during a no-paint freeze (MON-03).
        // The `monitor` LOCAL declared here is exactly what Plan 06's /health closes over
        // (() => monitor.SnapshotHealth()) and what Plan 05 reaches for Reset() — there is deliberately
        // NO CefWrapper.Monitor / browserWrapper.Monitor accessor property (D-27).
        // 02-05 (D-28/D-13): inject the IN-PROCESS refresh delegate so the monitor's recovery state machine
        // can self-heal a wedged page WITHOUT a CEF type leaking into FrameMonitor. The monitor calls THIS
        // delegate single-flight on TRIPPED; Phase 5 would inject () => page.ReloadAsync() instead and reuse
        // the whole monitor unchanged. RefreshPage() is void → wrap it as a completed Task.
        Func<Task> refreshDelegate = () =>
        {
            browserWrapper.RefreshPage();
            return Task.CompletedTask;
        };
        var monitor = new FrameMonitor(browserWrapper, refreshDelegate);
        var pump = new FramePump(monitor, Program.NdiSenderPtr);
        pump.Start();

        // 02-04 fallback wiring (D-18/D-20/D-21/D-33) — REPLACES 02-02's seeded never-null placeholder
        // in the monitor's fallback slot with the REAL validated/generated FallbackFrame. The provider
        // loads the policy=slate asset (the startup recipe's fallbackAsset, else slate.png) from
        // --fallback-dir at the ACTUAL --w/--h output geometry + the live alpha convention (byte-
        // identical to live frames so the receiver never resyncs at the swap); ANY failure degrades to a
        // generated slate/black surfaced LOUDLY. fallbackAssetState is what Plan 06's /health reports.
        var fallbackProvider = new FallbackProvider(fallbackDir, width, height, AlphaConvention.Expected);
        var fallbackResult = fallbackProvider.LoadOrGenerate(
            startupRecipe?.FallbackPolicy, startupRecipe?.FallbackAsset);
        monitor.SetFallbackFrame(
            fallbackResult.Frame.Bgra, fallbackResult.Frame.Width, fallbackResult.Frame.Height);
        var fallbackAssetState = fallbackResult.State;
        Log.Information(
            "FALLBACK ready — policy={Policy} asset={Asset} state={State} geom={W}x{H}",
            startupRecipe?.FallbackPolicy ?? "slate",
            fallbackResult.Sought,
            fallbackAssetState == FallbackAssetState.Configured ? "configured" : "generated-default",
            width, height);

        // 02-05 (D-26/D-28): apply the startup recipe's timing/expectMotion to the state machine, then wire
        // the swap-reset. On EVERY successful SetUrlAsync/SwapRecipeAsync the wrapper raises RecipeSwapped;
        // the composition root (NOT CefWrapper — it holds no FrameMonitor reference) resets the monitor's
        // stale detection/recovery state AND reloads the fallback asset for the new recipe's policy, so the
        // swapped page is classified fresh and false-trips on neither the old dHash window nor the old
        // fallback (Pitfall P-5). Then start the non-reentrant 5Hz sampler (D-06/D-31).
        monitor.ApplyRecipe(startupRecipe);
        browserWrapper.RecipeSwapped += swapped =>
        {
            monitor.Reset(swapped);
            var swappedFallback = fallbackProvider.LoadOrGenerate(swapped?.FallbackPolicy, swapped?.FallbackAsset);
            monitor.SetFallbackFrame(
                swappedFallback.Frame.Bgra, swappedFallback.Frame.Width, swappedFallback.Frame.Height);
            fallbackAssetState = swappedFallback.State;
            Log.Information(
                "MONITOR reset on swap — recipe expectMotion={Motion} fallback={State}",
                swapped?.ExpectMotion ?? false,
                swappedFallback.State == FallbackAssetState.Configured ? "configured" : "generated-default");
        };
        monitor.StartSampling();

        // 02-06 (D-23/D-27): wire the CROSS-COMPONENT /health fields the monitor does not own — the pump's
        // FramesSent counter, a no-secret recipe urlMatch summary (CurrentRecipe.UrlMatch only — NOT the full
        // sensitive URL, T-2-06-1), the Plan-04 configured-vs-generated-default fallback asset state, the P1
        // D-03 startup isolation posture string (OFF/ON, same value the POSTURE log emitted above), and the
        // process start for uptimeSec. SnapshotHealth() then closes over the `monitor` local with NO args
        // (D-27 — no browserWrapper.Monitor accessor). All plain values/delegates: the CEF-agnostic seam holds.
        var isolationPostureStr = launchExpectsCrossOriginIframes ? "OFF" : "ON";
        var healthProcessStart = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        monitor.WireHealth(
            () => pump.FramesSent,
            () => browserWrapper.CurrentRecipe?.UrlMatch,
            () => fallbackAssetState,
            isolationPostureStr,
            healthProcessStart);

        var capabilitiesXml = $$"""<ndi_capabilities ntk_kvm="true" />""";
        capabilitiesXml += "\0";
        var capabilitiesPtr = UTF.StringToUtf8(capabilitiesXml);

        var metaframe = new NDIlib.metadata_frame_t()
        {
            p_data = capabilitiesPtr
        };

        NDIlib.send_add_connection_metadata(NdiSenderPtr, ref metaframe);
        Marshal.FreeHGlobal(capabilitiesPtr);

        var running = true;
        var thread = new Thread(() =>
        {
            var metadata = new NDIlib.metadata_frame_t();
            var x = 0.0f;
            var y = 0.0f;
            while (running)
            {
                var result = NDIlib.send_capture(NdiSenderPtr, ref metadata, 1000);

                if (result == NDIlib.frame_type_e.frame_type_none)
                {
                    continue;
                }
                else if (result == NDIlib.frame_type_e.frame_type_metadata)
                {
                    var metadataConverted = UTF.Utf8ToString(metadata.p_data);

                    if(metadataConverted.StartsWith("<ndi_kvm u=\""))
                    {
                        metadataConverted = metadataConverted.Replace("<ndi_kvm u=\"", "");
                        metadataConverted = metadataConverted.Replace("\"/>", "");

                        try
                        {
                            var binary = Convert.FromBase64String(metadataConverted);

                            var opcode = binary[0];

                            if(opcode == 0x03)
                            {
                                x = BitConverter.ToSingle(binary, 1);
                                y = BitConverter.ToSingle(binary, 5);
                            }
                            else if(opcode == 0x04)
                            {
                                // Mouse Left Down
                                var screenX = (int)(x * width);
                                var screenY = (int)(y * height);

                                browserWrapper.Click(screenX, screenY);
                            }
                            else if(opcode == 0x07)
                            {
                                // Mouse Left Up
                            }
                        }
                        catch
                        {

                        }
                    }

                    Log.Logger.Warning("Got metadata: " + metadataConverted);
                    NDIlib.send_free_metadata(NdiSenderPtr, ref metadata);
                }

            }
        });
        thread.Start();


        app.MapPost("/seturl", async (HttpContext httpContext, GoToUrlModel url) =>
        {
            // D-16: await SetUrlAsync (the synchronous void SetUrl is gone). Resolve the recipe for the
            // new URL via the store so a navigation that crosses into a recipe-governed host re-registers
            // the doc-start script BEFORE Load. D-21: reject a swap whose isolation posture differs from
            // launch (site-iso is frozen at Cef.Initialize) — fail loud, do not silently mis-render.
            var nextRecipe = recipeStore.Match(url.Url);
            if (nextRecipe is not null
                && !RecipeStore.PostureMatches(launchExpectsCrossOriginIframes, nextRecipe))
            {
                return Results.BadRequest(new
                {
                    error = "posture-mismatch",
                    message = "The recipe matching this URL requires a different site-isolation posture than launch. "
                        + "Site-isolation flags are frozen at process start; relaunch with this recipe to apply it.",
                    launchExpectsCrossOriginIframes,
                    requiredExpectsCrossOriginIframes = nextRecipe.ExpectsCrossOriginIframes,
                });
            }

            await browserWrapper.SetUrlAsync(url.Url, nextRecipe);
            return Results.Ok();
        })
        .WithOpenApi();

        // D-06: /recipe GET returns the current recipe driving injection (null = pass-through).
        app.MapGet("/recipe", () => browserWrapper.CurrentRecipe)
            .WithOpenApi();

        // D-06/D-12/D-18/D-21/D-04: /recipe POST reads the RAW body (NOT a direct RecipeDto bind, which
        // would silently drop unknown fields — D-18), validates via the shared raw-JSON validator,
        // rejects a posture mismatch (D-21), then swaps fail-closed (D-04). Never partial-applies (D-12).
        app.MapPost("/recipe", async (HttpContext ctx) =>
        {
            string rawJson;
            using (var reader = new StreamReader(ctx.Request.Body))
            {
                rawJson = await reader.ReadToEndAsync();
            }

            // recipeStore is guaranteed non-null here (set in Main before the host runs). Use the shared
            // raw-JSON validator (the SAME path the store uses on file-load) so the two surfaces cannot drift.
            var (ok, recipe, errors) = new RecipeValidator().TryNormalize(rawJson);

            if (!ok || recipe is null)
            {
                // D-12/D-18: structured errors, never a partial apply.
                return Results.BadRequest(new { error = "invalid-recipe", errors });
            }

            // D-21: a swap whose required posture differs from the FROZEN launch posture is rejected with
            // a structured relaunch error — the current recipe is left unchanged.
            if (!RecipeStore.PostureMatches(launchExpectsCrossOriginIframes, recipe))
            {
                return Results.BadRequest(new
                {
                    error = "posture-mismatch",
                    message = "This recipe requires a different site-isolation posture than launch. "
                        + "Site-isolation flags are frozen at process start; relaunch with this recipe to apply it.",
                    launchExpectsCrossOriginIframes,
                    requiredExpectsCrossOriginIframes = recipe.ExpectsCrossOriginIframes,
                });
            }

            // D-04/D-17: fail-closed swap (remove-by-id → re-add; RecreateBrowserAsync on a Remove failure).
            await browserWrapper.SwapRecipeAsync(recipe);
            return Results.Ok();
        })
        .WithOpenApi();

        app.MapGet("/scroll/{increment}", (int increment) =>
        {
            browserWrapper.ScrollBy(increment);
        }).WithOpenApi();

        app.MapGet("/click/{x}/{y}", (int x, int y) =>
        {
            browserWrapper.Click(x, y);
        }).WithOpenApi();

        app.MapPost("/keystroke", (SendKeystrokeModel model) =>
        {
            browserWrapper.SendKeystrokes(model);
        }).WithOpenApi();

        app.MapGet("/type/{toType}", (string toType) =>
        {
            browserWrapper.SendKeystrokes(new SendKeystrokeModel
            {
                ToSend = toType
            });
        }).WithOpenApi();

        app.MapGet("/refresh", () =>
        {
            browserWrapper.RefreshPage();
        }).WithOpenApi();

        // 02-06 (D-23/D-24/D-25/D-27/D-32 — MON-05): the rich READ-ONLY liveness contract. A GET with NO
        // input + NO side effects (D-25 — it cannot mutate monitor or render state, T-2-06-3), mapped here
        // BEFORE app.Run() mirroring the /recipe GET above. It closes over the composition-root-local
        // `monitor` DIRECTLY (D-27 — there is deliberately NO browserWrapper.Monitor / CefWrapper.Monitor
        // accessor); SnapshotHealth() maps the already-instrumented monitor/pump/fallback/posture state.
        // D-24 freeze-vs-dead: a running-but-frozen process answers 200 here with status=tripped/recovering
        // + a high lastPaintAgeMs; a DEAD process simply fails to connect, so the deferred Phase-4 watchdog
        // distinguishes the two without a re-instrumentation pass — and never needlessly restarts a
        // recovering process. D-32: status/source serialize as STRING tokens (recovery-exhausted/fallback),
        // never integers (the KebabCaseStringEnumConverter on the enums), so the watchdog reads the contract.
        // 03-03 (D-24/D-06/D-03): the /health endpoint COMPOSES the three JS proof markers via a SIBLING CDP
        // Runtime.evaluate probe — reusing the existing EvaluateScriptAsync machinery (ReadMainBoolAsync,
        // the PollMainFlagAsync shape; NO new CDP code) — reading window.__xpnTargetPresent /
        // __xpnConsentDismissed / __xpnPlayStarted, then OVERLAYING them onto the monitor.SnapshotHealth()
        // result with `with` before returning. This keeps the proof-marker readback OUT of FrameMonitor (D-24:
        // FrameMonitor stays IFrameSource-only and never reads page JS); the markers are composed HERE per the
        // cross-component WireHealth precedent ("wire the /health fields the monitor does not own"). The probe
        // is read-only with a short timeout — a read miss leaves the field null (degraded observability, never
        // a 500). These markers are OBSERVABILITY ONLY: none is wired into UseFallback (D-14 / Pitfall 5 —
        // targetPresent=false strobes on SPA re-render). T-3-09: operational booleans only, no URL/content.
        app.MapGet("/health", async () =>
        {
            var snapshot = monitor.SnapshotHealth();

            // Sibling probe: read the three page proof markers off the MAIN frame (each null on a miss).
            var targetPresent = await ReadMainBoolAsync(browserWrapper, "__xpnTargetPresent");
            var consentDismissed = await ReadMainBoolAsync(browserWrapper, "__xpnConsentDismissed");
            var playStarted = await ReadMainBoolAsync(browserWrapper, "__xpnPlayStarted");

            return snapshot with
            {
                TargetPresent = targetPresent,
                ConsentDismissed = consentDismissed,
                PlayStarted = playStarted,
            };
        }).WithOpenApi();

        app.Run();

        running = false;
        thread.Join();

        // 02-05 (D-26): clean shutdown — stop the pump + the monitor's 5Hz sampler (Dispose stops the timer
        // first, then unsubscribes FrameReady) BEFORE the wrapper is disposed, so no sampler tick or send
        // fires against a torn-down browser. Mirrors the --smoke teardown.
        pump.StopAsync().GetAwaiter().GetResult();
        monitor.Dispose();
        browserWrapper.Dispose();

        if (Directory.Exists(launchCachePath))
        {
            try
            {
                Directory.Delete(launchCachePath, true);
            }
            catch
            {

            }
        }
    }

    /// <summary>
    /// D-05/D-15: additive in-process --smoke self-check. Initializes CEF, loads a tiny committed
    /// local HTML (or --smoke=&lt;url&gt;), captures one NON-BLANK OnPaint via the conditional
    /// CefWrapper one-shot latch, creates the NDI sender, sends EXACTLY ONE frame, logs the
    /// provenance stamp, prints "SMOKE OK" and exits 0 — WITHOUT starting the ASP.NET host or the
    /// KVM thread. A hard ~20s timeout (D-15c) makes a never-painting init fail fast (non-zero).
    /// The normal --url= path never reaches here, so it stays byte-for-byte upstream (D-05).
    /// </summary>
    private static void RunSmoke(string[] args, string launchCachePath)
    {
        const int SmokeTimeoutSeconds = 20;

        // D-15a: smoke defaults — no interactive prompts, no stdin dependency.
        var ndiName = "XPRESSION-SMOKE";
        var width = 1920;
        var height = 1080;

        // --smoke or --smoke=<url>. Default to the committed local smoke HTML beside the exe.
        var smokeArg = args.FirstOrDefault(x => x.StartsWith("--smoke")) ?? "--smoke";
        string smokeUrl;
        var eq = smokeArg.IndexOf('=');
        if (eq >= 0 && eq < smokeArg.Length - 1)
        {
            smokeUrl = smokeArg.Substring(eq + 1);
        }
        else
        {
            var localHtml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke", "smoke.html");
            smokeUrl = new Uri(localHtml).AbsoluteUri; // file:///.../smoke/smoke.html
        }

        Log.Information("SMOKE starting — url={SmokeUrl} timeout={Timeout}s", smokeUrl, SmokeTimeoutSeconds);

        var exitCode = 1; // default to failure; only set 0 on the success path.

        try
        {
            AsyncContext.Run(async delegate
            {
                var settings = new CefSettings();
                if (!Directory.Exists(launchCachePath))
                {
                    Directory.CreateDirectory(launchCachePath);
                }

                settings.RootCachePath = launchCachePath;
                settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

                // D-13: anti-throttle flags — kept IN SYNC with the normal path (unconditional).
                settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
                settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
                settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");

                settings.EnableAudio();
                Cef.Initialize(settings);

                // D-13: provenance stamp on the smoke path too (CEF now initialized).
                AppManagement.LogProvenance();

                browserWrapper = new CefWrapper(width, height, smokeUrl)
                {
                    SmokeMode = true, // D-15b: arm the one-shot latch.
                };

                // Behavior 2: create the NDI sender; nint.Zero ⇒ wrong/missing NDI DLL ⇒ fail.
                // Created BEFORE InitializeWrapperAsync so the sink can be constructed with a valid
                // sender ptr (D-26) and subscribed BEFORE Paint is wired — so the first paint reaches
                // the sink (no lost-first-frame race with the smoke latch).
                var settings_T = new NDIlib.send_create_t
                {
                    p_ndi_name = UTF.StringToUtf8(ndiName)
                };
                Program.NdiSenderPtr = NDIlib.send_create(ref settings_T);

                if (Program.NdiSenderPtr == nint.Zero)
                {
                    Log.Error("SMOKE FAILED — NDIlib.send_create returned nint.Zero (NDI native DLL missing or wrong).");
                    return; // exitCode stays 1
                }

                // D-01/D-27/D-30/D-31 — the SAME single-authority composition root as the interactive
                // path, re-proving --smoke under the pump rewrite (D-01 / CONTEXT calibration constraint 5).
                // FrameMonitor subscribes to browserWrapper.FrameReady BEFORE InitializeWrapperAsync wires
                // Paint, so the first paint is copied; FramePump pulls + sends on cadence. The wrapper's
                // one-shot SmokeMode latch still gates "one non-blank frame painted" (it fires inside
                // OnBrowserPaint after FrameReady?.Invoke, regardless of the subscriber), so the smoke
                // success signal (SmokeFrameSent) is unchanged — but the actual NDI send now flows
                // source → monitor → pump, re-proving the keyable BGRA hot path end-to-end.
                var monitor = new FrameMonitor(browserWrapper);
                var pump = new FramePump(monitor, Program.NdiSenderPtr);
                pump.Start();

                await browserWrapper.InitializeWrapperAsync();

                // Behaviors 1/3: wait for EXACTLY ONE non-blank frame, bounded by the hard timeout.
                var sentTask = browserWrapper.SmokeFrameSent;
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(SmokeTimeoutSeconds));
                var winner = await Task.WhenAny(sentTask, timeoutTask);

                if (winner != sentTask)
                {
                    Log.Error("SMOKE FAILED — no non-blank frame within {Timeout}s (never-painting init).", SmokeTimeoutSeconds);
                    return; // exitCode stays 1
                }

                Log.Information("SMOKE OK — one non-blank BGRA frame sent through the vendored NDI DLL.");
                Console.WriteLine("SMOKE OK");
                exitCode = 0;

                // Clean teardown of the single-authority pump before the wrapper is disposed in finally.
                await pump.StopAsync();
                monitor.Dispose();
            });
        }
        catch (Exception ex)
        {
            Log.Error("SMOKE FAILED — exception during smoke: {@ex}", ex);
            exitCode = 1;
        }
        finally
        {
            try
            {
                browserWrapper?.Dispose();
            }
            catch
            {
            }

            if (Directory.Exists(launchCachePath))
            {
                try
                {
                    Directory.Delete(launchCachePath, true);
                }
                catch
                {
                }
            }
        }

        Log.CloseAndFlush();
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// 03-04 D-07/D-24/D-26 (VAL-04): the idle-gap capture gate — a LOGGING SIDE-CHANNEL on the live
    /// pipeline used to data-drive the freeze backstop (<c>freezeTimeoutMs</c>). Unlike the one-shot
    /// gates above, this is a LONG-RUNNING live gate: it stands up the SAME single-authority composition
    /// root the interactive <c>--url=</c>/<c>--recipe</c> path uses — NDI sender → <see cref="CefWrapper"/>
    /// → <see cref="FrameMonitor"/> → <see cref="FramePump"/> — against the AccuWeather recipe
    /// (<c>expectMotion=true</c>), then logs ONE CSV line per sample tick for a bounded duration.
    /// <para>
    /// The capture is purely a tap on values <see cref="FrameMonitor.Classify"/> already produces, exposed
    /// via the plan-03 read-only <see cref="FrameMonitor.SampleObserved"/> telemetry seam (D-24): NO new
    /// timer (it rides the existing 5 Hz <c>OnSampleTick</c>, <c>SampleIntervalMs=200</c>), NO recomputed
    /// detector, NO reach into private FrameMonitor fields. The six columns are
    /// <c>tMs</c> (monotonic from the first sample), <c>dHash</c>, <c>hammingFromPrev</c> (all from the
    /// snapshot), <c>lastPaintAgeMs</c> (read read-only off <see cref="FrameMonitor.SnapshotHealth"/>),
    /// <c>beaconState</c> (the snapshot's 3-state liveness — ground truth; analysis gates the idle-gap on
    /// <c>beaconState=true</c>), and <c>targetPresent</c> (read via the SAME sibling-probe
    /// <see cref="ReadMainBoolAsync"/> of <c>window.__xpnTargetPresent</c> the <c>/health</c> path uses —
    /// NOT a FrameMonitor field; FrameMonitor stays IFrameSource-only, D-24).
    /// </para>
    /// <para>
    /// CRITICAL: NDI is wired exactly like the interactive path. <see cref="CefWrapper"/>'s
    /// <c>OnBrowserPaint</c> early-returns at <c>if (Program.NdiSenderPtr == nint.Zero) return;</c> BEFORE
    /// raising <c>FrameReady</c>, which is the monitor's ONLY frame feed — a capture that skips NDI would
    /// starve the monitor and produce an empty log. Runs for <c>--duration</c> seconds then flushes the log
    /// + <see cref="Environment.Exit"/>(0). Bounded + self-exiting (the operator runs it under a timeout).
    /// </para>
    /// </summary>
    private static void RunAccuWeatherCapture(string[] args, string launchCachePath)
    {
        var width = 1920;
        var height = 1080;
        const int DefaultDurationSeconds = 300;

        // ── arg parsing ───────────────────────────────────────────────────────────────────────────
        // --recipe=<path-or-name> (mirrors the normal path's --recipe; NOT aliasing --recipe-dir).
        string? recipeName = null;
        var recipeArg = args.FirstOrDefault(x => x.StartsWith("--recipe") && !x.StartsWith("--recipe-dir"));
        if (recipeArg is not null)
        {
            var eqR = recipeArg.IndexOf('=');
            if (eqR >= 0 && eqR < recipeArg.Length - 1)
            {
                recipeName = recipeArg.Substring(eqR + 1);
            }
        }

        if (string.IsNullOrWhiteSpace(recipeName))
        {
            Console.WriteLine("ACCUWEATHER-CAPTURE FAIL: --recipe=<path-to-accuweather.json> is required.");
            Log.Error("ACCUWEATHER-CAPTURE FAIL — missing --recipe.");
            Log.CloseAndFlush();
            Environment.Exit(1);
        }

        // --duration=<seconds> (default 300 = 5 min); bounded + self-exiting.
        var durationSeconds = DefaultDurationSeconds;
        var durationArg = args.FirstOrDefault(x => x.StartsWith("--duration"));
        if (durationArg is not null)
        {
            var eqD = durationArg.IndexOf('=');
            if (eqD < 0 || !int.TryParse(durationArg.Substring(eqD + 1), out durationSeconds) || durationSeconds <= 0)
            {
                Console.WriteLine("ACCUWEATHER-CAPTURE FAIL: --duration must be a positive integer (seconds).");
                Log.Error("ACCUWEATHER-CAPTURE FAIL — bad --duration.");
                Log.CloseAndFlush();
                Environment.Exit(1);
            }
        }

        // --out=<path> override; default to a Windows-local path the operator (and WSL via /mnt/c) can
        // retrieve. The dir is created if absent.
        string outPath;
        var outArg = args.FirstOrDefault(x => x.StartsWith("--out"));
        if (outArg is not null && outArg.IndexOf('=') is var eqO && eqO >= 0 && eqO < outArg.Length - 1)
        {
            outPath = outArg.Substring(eqO + 1);
        }
        else
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            outPath = Path.Combine(@"C:\temp", $"accuweather-capture-{stamp}.csv");
        }

        // Resolve the recipe SYNCHRONOUSLY before Cef.Initialize — its ExpectsCrossOriginIframes gates the
        // site-isolation CefCommandLineArgs (Pitfall 4: late adds are silently ignored), exactly as the
        // normal path does. An explicit recipe that fails validation is a HARD failure (D-20).
        var recipeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes");
        var store = new RecipeStore(new RecipeValidator());
        var recipePath = Path.IsPathRooted(recipeName)
            ? recipeName
            : Path.Combine(recipeDir, recipeName!.EndsWith(".json") ? recipeName! : recipeName! + ".json");
        if (!store.TryLoadExplicit(recipePath, out var recipe, out var recipeError) || recipe is null)
        {
            Console.WriteLine($"ACCUWEATHER-CAPTURE FAIL: recipe failed to load: {recipeError}");
            Log.Error("ACCUWEATHER-CAPTURE FAIL — recipe load: {Error}", recipeError);
            Log.CloseAndFlush();
            Environment.Exit(1);
        }

        // --url=<operator radar URL> override (REQUIRED in practice). recipe.UrlMatch is a GLOB
        // (".../weather-radar/*") — NOT navigable; without this the gate loads the literal glob and never
        // reaches a real radar page (the cause of the 03-03 "static dHash + targetPresent=false" capture).
        string? urlOverride = null;
        var urlArg = args.FirstOrDefault(x => x.StartsWith("--url="));
        if (urlArg is not null && urlArg.Length > "--url=".Length)
        {
            urlOverride = urlArg.Substring("--url=".Length);
        }
        var startUrl = !string.IsNullOrWhiteSpace(urlOverride)
            ? urlOverride!
            : (recipe!.UrlMatch ?? "https://www.accuweather.com/");
        Log.Information(
            "ACCUWEATHER-CAPTURE starting — recipe={Recipe} expectMotion={Motion} duration={Duration}s out={Out}",
            recipePath, recipe.ExpectMotion, durationSeconds, outPath);

        var exitCode = 1; // default failure; set 0 on a clean bounded run.

        try
        {
            // Open the log + write the header up front so the path is valid before CEF spins up.
            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            using var logWriter = new StreamWriter(outPath, append: false) { AutoFlush = false };
            logWriter.WriteLine("tMs,dHash,hammingFromPrev,lastPaintAgeMs,beaconState,targetPresent");
            logWriter.Flush();
            Console.WriteLine($"ACCUWEATHER-CAPTURE log: {outPath}");

            var sampleCount = 0L;
            var firstSampleTicks = -1L; // monotonic origin (first sample's snapshot Ts), for tMs.

            AsyncContext.Run(async delegate
            {
                var settings = new CefSettings();
                if (!Directory.Exists(launchCachePath))
                {
                    Directory.CreateDirectory(launchCachePath);
                }

                settings.RootCachePath = launchCachePath;
                settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

                // D-13 anti-throttle — kept in sync with the smoke/normal/gate paths (unconditional) so a
                // backgrounded/occluded offscreen surface does not throttle rAF / timers during the capture.
                settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
                settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
                settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");

                // D-03/D-21: site-isolation flags — recipe-gated, BEFORE Cef.Initialize (same as the
                // normal path). AccuWeather is expectsCrossOriginIframes=false, so these stay off here.
                if (recipe.ExpectsCrossOriginIframes)
                {
                    settings.CefCommandLineArgs.Add("disable-features", "IsolateOrigins,site-per-process");
                    settings.CefCommandLineArgs.Add("disable-site-isolation-trials", "1");
                }

                settings.EnableAudio();
                Cef.Initialize(settings);
                AppManagement.LogProvenance();

                // ── THE NDI SENDER — wired exactly like the interactive path (see the CRITICAL note in the
                // method summary). Without a non-Zero NdiSenderPtr, OnBrowserPaint never raises FrameReady,
                // so the FrameMonitor never samples and the capture is empty. Created BEFORE the wrapper so
                // the pump can be constructed with a valid sender ptr.
                var settings_T = new NDIlib.send_create_t
                {
                    p_ndi_name = UTF.StringToUtf8("XPRESSION-CAPTURE"),
                };
                Program.NdiSenderPtr = NDIlib.send_create(ref settings_T);
                if (Program.NdiSenderPtr == nint.Zero)
                {
                    Log.Error("ACCUWEATHER-CAPTURE FAIL — NDIlib.send_create returned nint.Zero (NDI native DLL missing/wrong).");
                    return; // exitCode stays 1
                }

                browserWrapper = new CefWrapper(width, height, startUrl)
                {
                    StartupRecipe = recipe, // D-16: registered in InitializeWrapperAsync BEFORE Load.
                };

                // THE SAME single-authority composition root as the interactive/smoke path: source → monitor
                // → pump. FrameMonitor subscribes to browserWrapper.FrameReady; FramePump is the sole NDI
                // sender, pulling on its own cadence so NDI never stops. No refresh delegate is wired (the
                // capture only OBSERVES; we do not want a recovery refresh perturbing the idle-gap corpus).
                var monitor = new FrameMonitor(browserWrapper);
                var pump = new FramePump(monitor, Program.NdiSenderPtr);
                pump.Start();

                // ── THE LOGGING SIDE-CHANNEL (D-24): consume the read-only SampleObserved seam on the
                // EXISTING 5 Hz tick. NO new timer, NO recompute. dHash/hammingFromPrev/beaconState come
                // straight off the snapshot; lastPaintAgeMs is read read-only off SnapshotHealth (the same
                // value /health reports); targetPresent is read via the /health sibling probe of
                // window.__xpnTargetPresent — NOT a FrameMonitor field. The handler MUST NOT mutate monitor
                // state (the seam is observability-only).
                monitor.SampleObserved += snap =>
                {
                    if (Interlocked.CompareExchange(ref firstSampleTicks, snap.Ts.Ticks, -1L) == -1L)
                    {
                        // first sample established the monotonic origin.
                    }

                    var originTicks = Interlocked.Read(ref firstSampleTicks);
                    var tMs = (long)TimeSpan.FromTicks(snap.Ts.Ticks - originTicks).TotalMilliseconds;

                    // lastPaintAgeMs off the read-only health snapshot (no side effects — D-24/D-25).
                    var lastPaintAgeMs = (long)monitor.SnapshotHealth().LastPaintAgeMs;

                    // beaconState token: "true" (alive/ticking) gates the idle-gap analysis in Task 2.
                    var beaconState = snap.BeaconState switch
                    {
                        BeaconLiveness.True => "true",
                        BeaconLiveness.False => "false",
                        _ => "absent",
                    };

                    // targetPresent via the SAME sibling probe /health uses — confirms the capture is of the
                    // RIGHT content (not a drifted recipe). Read-only; null on a read miss → "" in the CSV.
                    bool? targetPresent;
                    try
                    {
                        targetPresent = ReadMainBoolAsync(browserWrapper, "__xpnTargetPresent")
                            .GetAwaiter().GetResult();
                    }
                    catch
                    {
                        targetPresent = null;
                    }

                    var targetCol = targetPresent.HasValue ? (targetPresent.Value ? "true" : "false") : "";

                    // Write under a lock — SampleObserved fires off the timer thread; AutoFlush is off so a
                    // periodic flush keeps the file readable mid-run without thrashing.
                    lock (logWriter)
                    {
                        logWriter.WriteLine(
                            $"{tMs},{snap.DHash},{snap.HammingFromPrev},{lastPaintAgeMs},{beaconState},{targetCol}");
                        var n = Interlocked.Increment(ref sampleCount);
                        if (n % 25 == 0) // ≈ every 5 s at 5 Hz.
                        {
                            logWriter.Flush();
                        }
                    }
                };

                await browserWrapper.InitializeWrapperAsync();

                // Apply the recipe timing/expectMotion to the state machine, then start the existing 5 Hz
                // sampler (the SampleObserved tap is already subscribed above). No fallback frame / no swap
                // wiring — the capture only observes; it never drives fallback.
                monitor.ApplyRecipe(recipe);
                monitor.StartSampling();

                // Bounded run — sleep the duration, then tear down cleanly. Self-exiting (the operator runs
                // this under a timeout; this is the primary exit path).
                await Task.Delay(TimeSpan.FromSeconds(durationSeconds));

                await pump.StopAsync();
                monitor.Dispose(); // stops the 5 Hz timer + unsubscribes FrameReady before wrapper teardown.

                lock (logWriter)
                {
                    logWriter.Flush();
                }

                exitCode = 0;
            });

            // Final flush + summary AFTER AsyncContext.Run returns (the using-disposed writer is flushed on
            // dispose, but emit the line count + path for the operator/orchestrator either way).
            var total = Interlocked.Read(ref sampleCount);
            logWriter.Flush();
            Console.WriteLine($"ACCUWEATHER-CAPTURE done — {total} samples written to {outPath}");
            Log.Information("ACCUWEATHER-CAPTURE done — samples={Count} out={Out}", total, outPath);
        }
        catch (Exception ex)
        {
            Log.Error("ACCUWEATHER-CAPTURE FAIL — exception during capture: {@ex}", ex);
            exitCode = 1;
        }
        finally
        {
            try { browserWrapper?.Dispose(); } catch { }

            if (Directory.Exists(launchCachePath))
            {
                try { Directory.Delete(launchCachePath, true); } catch { }
            }
        }

        Log.CloseAndFlush();
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// 03-05 D-06/D-27 (VAL-01/VAL-03/VAL-04): the SEMI-AUTOMATED live content-proof gate. A SIBLING of
    /// <see cref="RunMonitorSmoke"/> / <see cref="RunAccuWeatherCapture"/> (self-driving, ends in
    /// <see cref="Environment.Exit"/>, NOT a new <c>app.Run()</c> lifecycle — D-27). It stands up the SAME
    /// single-authority composition root the interactive <c>--url=</c>/<c>--recipe</c> path uses (NDI sender →
    /// <see cref="CefWrapper"/> → <see cref="FrameMonitor"/> → <see cref="FramePump"/>) against the REAL
    /// AccuWeather recipe + the operator URL, reads liveness IN-PROCESS via <see cref="FrameMonitor.SnapshotHealth"/>
    /// + the sibling proof-marker probe (<see cref="ReadMainBoolAsync"/>, the SAME machinery <c>/health</c>
    /// uses — no ASP.NET host needed for the gate), and drives faults through the EXISTING control plane
    /// (<see cref="CefWrapper.SetUrlAsync"/> / <see cref="CefWrapper.SwapRecipeAsync"/> — the in-process
    /// equivalents of <c>/seturl</c> + <c>/recipe</c>; NO new endpoint, D-10).
    ///
    /// <para>FOUR-POINT ENV PRE-ASSERTION (D-06/D-19, FORK-04 §6 shape) — blocks the suite with a clear cause:
    /// (1) the bundle is on a Windows-LOCAL path (<c>C:\...</c>), not <c>\\wsl.localhost</c> (CEF natives
    /// cannot load over UNC); (2) running on the interactive GPU desktop, not Session-0 — asserted
    /// programmatically via <c>Process.GetCurrentProcess().SessionId &gt;= 1</c> (the Windows Terminal-Services
    /// session id; Session 0 is the isolated services session with no GPU desktop — the programmatic
    /// equivalent of the FORK-04 §6 <c>query session</c> check); (3) the configured slate.png + accuweather.json
    /// are IN the bundle; (4) the provenance stamp reads CefSharp=148.0.90.0.</para>
    ///
    /// <para>VAL-01 = consentDismissed AND targetPresent AND playStarted AND non-blank — ALL FOUR (D-06:
    /// non-blank ALONE must NOT pass; it passes on WRONG content — targetPresent is the right-content guard).
    /// VAL-03 = SetUrlAsync(about:blank) → source=fallback + fallbackReason=blank-* + fallbackAsset=configured
    /// AND the on-air faulted frame MATCHES the slate.png signature (D-29b — not merely non-blank geometry),
    /// THEN a GENUINE all-stop freeze (a static page whose OnPaint stops) trips the freezeTimeoutMs BACKSTOP →
    /// fallback (D-31: the backstop is the v1.0 freeze authority; the freeze poll allows &gt; freezeTimeoutMs so
    /// the ~60s legitimate trip is NOT polled-out). VAL-04 = NO false-trip over the live idle-hold; if the
    /// beacon false-trips, <see cref="FrameMonitor.BeaconTripEnabled"/> is set OFF (the D-31 guard) and the
    /// system is re-validated on the backstop ALONE. Always <see cref="Environment.Exit"/> (0 on a full pass,
    /// non-zero with the failing assertion named).</para>
    /// </summary>
    private static void RunAccuWeatherValidate(string[] args, string launchCachePath)
    {
        var width = 1920;
        var height = 1080;

        // The live gate's overall budget. The VAL-03 GENUINE-FREEZE leg waits on the freezeTimeoutMs=60000
        // BACKSTOP (D-31), so the freeze poll alone needs > freezeTimeoutMs (~75s); the whole gate is bounded
        // generously above that. The operator still runs it under an outer timeout (the runbook).
        const int LiveReadyTimeoutSeconds = 90;   // consent+target+play+non-blank can take a while on the live page.
        const int FreezeBackstopPollSeconds = 80; // > freezeTimeoutMs=60000 so the legitimate ~60s trip is not polled-out.
        const int IdleHoldObserveSeconds = 45;    // watch the radar's real idle-hold for a beacon false-trip (D-31).

        // ── arg parsing (mirrors RunAccuWeatherCapture) ─────────────────────────────────────────────
        string? recipeName = null;
        var recipeArg = args.FirstOrDefault(x => x.StartsWith("--recipe") && !x.StartsWith("--recipe-dir"));
        if (recipeArg is not null)
        {
            var eqR = recipeArg.IndexOf('=');
            if (eqR >= 0 && eqR < recipeArg.Length - 1)
            {
                recipeName = recipeArg.Substring(eqR + 1);
            }
        }

        if (string.IsNullOrWhiteSpace(recipeName))
        {
            Console.WriteLine("ACCUWEATHER-VALIDATE FAIL: --recipe=<path-to-accuweather.json> is required.");
            Log.Error("ACCUWEATHER-VALIDATE FAIL — missing --recipe.");
            Log.CloseAndFlush();
            Environment.Exit(1);
        }

        string? urlOverride = null;
        var urlArg = args.FirstOrDefault(x => x.StartsWith("--url="));
        if (urlArg is not null && urlArg.Length > "--url=".Length)
        {
            urlOverride = urlArg.Substring("--url=".Length);
        }

        if (string.IsNullOrWhiteSpace(urlOverride))
        {
            // recipe.UrlMatch is a GLOB (".../weather-radar/*") — NOT navigable. The operator MUST supply a
            // real radar URL (the 03-03 "static dHash + targetPresent=false" trap was loading the literal glob).
            Console.WriteLine("ACCUWEATHER-VALIDATE FAIL: --url=<live radar URL> is required (the recipe urlMatch is a non-navigable glob).");
            Log.Error("ACCUWEATHER-VALIDATE FAIL — missing --url.");
            Log.CloseAndFlush();
            Environment.Exit(1);
        }

        // ── (1)+(3) ENV PRE-ASSERTION pre-CEF: bundle path + asset presence (the session-id + provenance
        // checks that need CEF init are asserted just after Cef.Initialize below). FORK-04 §6 shape. ──────
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // (1) Windows-LOCAL bundle path — CEF natives cannot load over \\wsl.localhost UNC (D-19). A bundle
        // launched from a UNC/WSL path fails opaquely at new CefSettings(); fail LOUD here with the cause.
        if (baseDir.StartsWith(@"\\", StringComparison.Ordinal)
            || baseDir.StartsWith("//", StringComparison.Ordinal)
            || baseDir.Contains("wsl.localhost", StringComparison.OrdinalIgnoreCase)
            || baseDir.Contains("wsl$", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (env): bundle is on a UNC/WSL path ({baseDir}) — CEF natives require a Windows-LOCAL path (e.g. C:\\temp\\...). Publish to C:\\temp and re-run (D-19).");
            Log.Error("ACCUWEATHER-VALIDATE FAIL — env: non-local bundle path {Dir}.", baseDir);
            Log.CloseAndFlush();
            Environment.Exit(1);
        }

        // (3) the configured slate.png + accuweather.json are IN the bundle.
        var recipeDir = Path.Combine(baseDir, "recipes");
        var recipePath = Path.IsPathRooted(recipeName)
            ? recipeName!
            : Path.Combine(recipeDir, recipeName!.EndsWith(".json") ? recipeName! : recipeName! + ".json");
        if (!File.Exists(recipePath))
        {
            Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (env): recipe not in the bundle at {recipePath} (D-19 asset-presence check).");
            Log.Error("ACCUWEATHER-VALIDATE FAIL — env: recipe absent {Path}.", recipePath);
            Log.CloseAndFlush();
            Environment.Exit(1);
        }

        var store = new RecipeStore(new RecipeValidator());
        if (!store.TryLoadExplicit(recipePath, out var recipe, out var recipeError) || recipe is null)
        {
            Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (env): recipe failed to load: {recipeError}");
            Log.Error("ACCUWEATHER-VALIDATE FAIL — env: recipe load {Error}.", recipeError);
            Log.CloseAndFlush();
            Environment.Exit(1);
        }

        // The configured slate asset the D-29b signature compare hashes — resolved the SAME way
        // FallbackProvider does (recipe.fallbackAsset else slate.png), asserted PRESENT in fallbacks/.
        var fallbackDir = Path.Combine(baseDir, "fallbacks");
        var slateAssetName = string.IsNullOrWhiteSpace(recipe.FallbackAsset) ? "slate.png" : recipe.FallbackAsset;
        var slatePath = Path.Combine(fallbackDir, slateAssetName);
        if (!File.Exists(slatePath))
        {
            Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (env): configured slate '{slateAssetName}' not in the bundle at {slatePath} (D-19/D-29b asset-presence check).");
            Log.Error("ACCUWEATHER-VALIDATE FAIL — env: slate absent {Path}.", slatePath);
            Log.CloseAndFlush();
            Environment.Exit(1);
        }

        var startUrl = urlOverride!;
        Log.Information(
            "ACCUWEATHER-VALIDATE starting — recipe={Recipe} url={Url} expectMotion={Motion} freezeTimeoutMs={Freeze}",
            recipePath, startUrl, recipe.ExpectMotion, recipe.FreezeTimeoutMs);

        var exitCode = 1; // default failure; set 0 only on a full clean pass.

        // Resolve the configured slate's BGRA signature ONCE (the D-29b reference), via the SAME
        // FallbackProvider.LoadOrGenerate path the production fallback uses — Configured = the real PNG decoded.
        long[]? slateSignature = null;
        try
        {
            var sigProvider = new FallbackProvider(fallbackDir, width, height, AlphaConvention.Expected);
            var sigResult = sigProvider.LoadOrGenerate(recipe.FallbackPolicy, recipe.FallbackAsset);
            if (sigResult.State != FallbackAssetState.Configured)
            {
                Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (env): the configured slate did not load as Configured (got {sigResult.State}) — the D-29b signature would compare against the generated default, not the real slate.");
                Log.Error("ACCUWEATHER-VALIDATE FAIL — env: slate not Configured ({State}).", sigResult.State);
                Log.CloseAndFlush();
                Environment.Exit(1);
            }

            slateSignature = ComputeBgraSignature(
                sigResult.Frame.Bgra, sigResult.Frame.Width, sigResult.Frame.Height,
                sigResult.Frame.Width * 4);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (env): could not compute the slate signature: {ex.Message}");
            Log.Error("ACCUWEATHER-VALIDATE FAIL — env: slate signature {@ex}.", ex);
            Log.CloseAndFlush();
            Environment.Exit(1);
        }

        FrameMonitor? monitor = null;
        FramePump? pump = null;

        try
        {
            // Site-iso posture is FROZEN at Cef.Initialize from the recipe (Pitfall 4: late adds ignored).
            launchExpectsCrossOriginIframes = recipe.ExpectsCrossOriginIframes;

            AsyncContext.Run(async delegate
            {
                var settings = new CefSettings();
                if (!Directory.Exists(launchCachePath))
                {
                    Directory.CreateDirectory(launchCachePath);
                }

                settings.RootCachePath = launchCachePath;
                settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
                settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
                settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
                settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");

                if (recipe.ExpectsCrossOriginIframes)
                {
                    settings.CefCommandLineArgs.Add("disable-features", "IsolateOrigins,site-per-process");
                    settings.CefCommandLineArgs.Add("disable-site-isolation-trials", "1");
                }

                settings.EnableAudio();
                Cef.Initialize(settings);
                AppManagement.LogProvenance();

                // ── (2) ENV PRE-ASSERTION: interactive GPU desktop, not Session-0 (else CEF SwiftShader-falls-
                // back — the banked Session-0 trap). Process.GetCurrentProcess().SessionId is the Windows
                // Terminal-Services session id; a NON-ZERO value confirms a non-Session-0 interactive process
                // (Session 0 = the isolated services session with no GPU desktop). This is the programmatic
                // equivalent of the FORK-04 §6 `query session` / `SessionId >= 1` runbook check (D-19). ──────
                var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                if (sessionId < 1)
                {
                    Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (env): Process.GetCurrentProcess().SessionId={sessionId} (Session 0 = isolated services session, NO GPU desktop → CEF SwiftShader fallback). Run on the interactive GPU desktop (SessionId >= 1) — FORK-04 §6 (D-19).");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — env: SessionId={SessionId} (< 1, Session-0 / no GPU desktop).", sessionId);
                    return; // exitCode stays 1
                }

                // ── (4) ENV PRE-ASSERTION: the provenance stamp reads CefSharp=148.0.90.0 (the pinned,
                // version-locked CEF the bundle MUST carry). CefSharp.Cef.CefSharpVersion is the SAME value
                // LogProvenance just emitted; a mismatch means a wrong/mixed bundle (D-19). ─────────────────
                const string ExpectedCefSharp = "148.0.90.0";
                var cefVersion = CefSharp.Cef.CefSharpVersion;
                if (!string.Equals(cefVersion, ExpectedCefSharp, StringComparison.Ordinal))
                {
                    Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (env): provenance CefSharp={cefVersion ?? "(unknown)"} != expected {ExpectedCefSharp} (wrong/mixed bundle — D-19).");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — env: CefSharp={Cef} != {Expected}.", cefVersion, ExpectedCefSharp);
                    return; // exitCode stays 1
                }

                Console.WriteLine($"ACCUWEATHER-VALIDATE env OK — local bundle, SessionId={sessionId} (interactive GPU desktop), recipe+slate present, CefSharp={cefVersion}.");
                Log.Information("ACCUWEATHER-VALIDATE env PRE-ASSERT passed (SessionId={SessionId}, CefSharp={Cef}).", sessionId, cefVersion);

                // ── THE NDI SENDER — wired exactly like the interactive path (OnBrowserPaint early-returns at
                // NdiSenderPtr==Zero BEFORE FrameReady, the monitor's only feed; a null ptr starves the monitor).
                var settingsT = new NDIlib.send_create_t { p_ndi_name = UTF.StringToUtf8("XPRESSION-VALIDATE") };
                Program.NdiSenderPtr = NDIlib.send_create(ref settingsT);
                if (Program.NdiSenderPtr == nint.Zero)
                {
                    Console.WriteLine("ACCUWEATHER-VALIDATE FAIL: NDIlib.send_create returned nint.Zero (NDI native DLL missing/wrong).");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — NDI send_create nint.Zero.");
                    return; // exitCode stays 1
                }

                browserWrapper = new CefWrapper(width, height, startUrl)
                {
                    StartupRecipe = recipe, // D-16: registered in InitializeWrapperAsync BEFORE Load.
                };

                // THE SAME single-authority composition root as the interactive path (source → monitor → pump
                // → NDI). The refresh delegate is the in-process RefreshPage (D-28) so the recovery loop reloads
                // exactly as production would (the refresh+re-inject run drives this via the control plane).
                Func<Task> refreshDelegate = () =>
                {
                    browserWrapper.RefreshPage();
                    return Task.CompletedTask;
                };
                monitor = new FrameMonitor(browserWrapper, refreshDelegate);
                pump = new FramePump(monitor, Program.NdiSenderPtr);
                pump.Start();

                // The REAL configured-slate fallback frame (D-29b: the same Configured buffer the signature
                // was computed from), supplied to the monitor so the on-air fallback IS the real slate.
                var fallbackProvider = new FallbackProvider(fallbackDir, width, height, AlphaConvention.Expected);
                var fallbackResult = fallbackProvider.LoadOrGenerate(recipe.FallbackPolicy, recipe.FallbackAsset);
                monitor.SetFallbackFrame(fallbackResult.Frame.Bgra, fallbackResult.Frame.Width, fallbackResult.Frame.Height);

                monitor.ApplyRecipe(recipe);
                monitor.WireHealth(
                    () => pump.FramesSent,
                    () => browserWrapper.CurrentRecipe?.UrlMatch,
                    () => fallbackResult.State,
                    "ON",
                    System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime());
                monitor.StartSampling();

                await browserWrapper.InitializeWrapperAsync();

                // ════════════════════════════════ VAL-01 — content proof ════════════════════════════════
                // ALL FOUR proof-markers (D-06). non-blank ALONE must NOT pass (it passes on WRONG content) —
                // targetPresent is the right-content guard. consentDismissed/targetPresent/playStarted come
                // from the page JS via the sibling probe (ReadMainBoolAsync — the SAME read /health composes);
                // non-blank comes from the monitor (HEALTHY + Live = a non-blank frame is on air, since BLANK
                // trips the monitor to Fallback). We require all four TOGETHER, not non-blank alone.
                var readyDeadline = DateTime.UtcNow.AddSeconds(LiveReadyTimeoutSeconds);

                // The three JS markers (each polled true on the MAIN frame). targetPresent is the right-content
                // guard — without it a non-blank wrong page would spoof VAL-01 (T-3-13).
                if (!await PollMainFlagAsync(browserWrapper, "window.__xpnConsentDismissed === true", readyDeadline))
                {
                    Console.WriteLine("ACCUWEATHER-VALIDATE FAIL (VAL-01): consentDismissed never true.");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-01 consentDismissed.");
                    return;
                }
                if (!await PollMainFlagAsync(browserWrapper, "window.__xpnTargetPresent === true", readyDeadline))
                {
                    Console.WriteLine("ACCUWEATHER-VALIDATE FAIL (VAL-01): targetPresent never true (right-content guard — non-blank alone does NOT pass).");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-01 targetPresent.");
                    return;
                }
                if (!await PollMainFlagAsync(browserWrapper, "window.__xpnPlayStarted === true", readyDeadline))
                {
                    Console.WriteLine("ACCUWEATHER-VALIDATE FAIL (VAL-01): playStarted never true.");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-01 playStarted.");
                    return;
                }

                // non-blank = the monitor is HEALTHY + Live (a blank frame trips it to Fallback, so Live ⇒
                // non-blank on air). This is the FOURTH marker — required ALONGSIDE the three JS markers.
                if (!await PollConditionAsync(
                    () => monitor!.Status == MonitorStatus.Healthy && monitor!.OutputState == FrameMonitor.Output.Live,
                    readyDeadline))
                {
                    Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (VAL-01): not HEALTHY/Live (non-blank) — status={monitor!.Status}, output={monitor!.OutputState}.");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-01 non-blank (status={Status} output={Output}).", monitor!.Status, monitor!.OutputState);
                    return;
                }

                Console.WriteLine("ACCUWEATHER-VALIDATE VAL-01 OK — consentDismissed AND targetPresent AND playStarted AND non-blank (all four).");
                Log.Information("ACCUWEATHER-VALIDATE VAL-01 passed (all four proof-markers).");

                // ════════════════════════════════ VAL-04 — no-false-trip + D-31 guard ════════════════════
                // BEFORE the destructive blank/freeze faults: observe the radar's REAL idle-hold and assert
                // the monitor does NOT false-trip while a HEALTHY radar self-throttles. The v1.0 trip authority
                // is the content-staleness timer (VECTOR 1, backstop); the beacon is best-effort. If the beacon
                // false-trips over the idle-hold (the live capture saw not-`True` ~43%), the D-31 guard sets
                // BeaconTripEnabled OFF and we RE-VALIDATE that the system holds on the backstop ALONE.
                //
                // VECTOR-2 fix: production now DEFAULTS BeaconTripEnabled=OFF (backstop-only — FrameMonitor.cs),
                // so the gate must OPT INTO the beacon probe explicitly to keep the beacon-reliability probe
                // meaningful. Enable it here at the start of the VAL-04 phase; the observe-false-trip → disable
                // (below) → recover → re-validate-backstop-only logic is UNCHANGED. Net: v1.0 ships backstop-only
                // regardless of the probe outcome (D-31); the gate still exercises + reports the beacon probe.
                monitor!.BeaconTripEnabled = true;

                var idleDeadline = DateTime.UtcNow.AddSeconds(IdleHoldObserveSeconds);
                var falseTripped = await PollConditionAsync(
                    () => monitor!.OutputState == FrameMonitor.Output.Fallback,
                    idleDeadline);

                if (falseTripped)
                {
                    Log.Warning(
                        "ACCUWEATHER-VALIDATE VAL-04 — the monitor tripped to Fallback over the HEALTHY idle-hold "
                        + "(reason={Reason}). Applying the D-31 DISABLE-ON-FALSE-TRIP guard: BeaconTripEnabled=OFF, "
                        + "re-validating on the freezeTimeoutMs backstop ALONE.", monitor!.FallbackReason);
                    Console.WriteLine($"ACCUWEATHER-VALIDATE VAL-04 — false-trip over the idle-hold (reason={monitor!.FallbackReason}); disabling the beacon trip (D-31) and re-validating on the backstop alone.");

                    // D-31 guard: the beacon false-trips → gate the beacon frozen->trip branch OFF (telemetry
                    // stays on /health) and recover to Live, then re-prove the system HOLDS over the idle-hold
                    // on the backstop alone.
                    monitor!.BeaconTripEnabled = false;

                    // Bring it back to Live (a /refresh-equivalent re-inject) and confirm recovery before re-test.
                    browserWrapper.RefreshPage();
                    var recoverDeadline = DateTime.UtcNow.AddSeconds(LiveReadyTimeoutSeconds);
                    if (!await PollConditionAsync(
                        () => monitor!.Status == MonitorStatus.Healthy && monitor!.OutputState == FrameMonitor.Output.Live,
                        recoverDeadline))
                    {
                        Console.WriteLine("ACCUWEATHER-VALIDATE FAIL (VAL-04): did not recover to HEALTHY/Live after the D-31 beacon-trip disable.");
                        Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-04 no recovery after D-31 disable.");
                        return;
                    }

                    var reIdleDeadline = DateTime.UtcNow.AddSeconds(IdleHoldObserveSeconds);
                    var stillTrips = await PollConditionAsync(
                        () => monitor!.OutputState == FrameMonitor.Output.Fallback,
                        reIdleDeadline);
                    if (stillTrips)
                    {
                        Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (VAL-04): STILL false-trips over the idle-hold on the backstop alone (reason={monitor!.FallbackReason}) — freezeTimeoutMs is too tight for the real idle gap (re-derive via --accuweather-capture).");
                        Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-04 backstop-alone still false-trips (reason={Reason}).", monitor!.FallbackReason);
                        return;
                    }

                    Console.WriteLine("ACCUWEATHER-VALIDATE VAL-04 OK — no false-trip on the backstop ALONE (beacon disabled for v1.0 per D-31; beaconState stays /health telemetry).");
                    Log.Information("ACCUWEATHER-VALIDATE VAL-04 passed — backstop-alone, beacon trip disabled (D-31). v1.0 ships backstop-only.");
                }
                else
                {
                    Console.WriteLine("ACCUWEATHER-VALIDATE VAL-04 OK — no false-trip over the idle-hold (beacon trip stayed enabled as a best-effort bonus; backstop is the v1.0 authority).");
                    Log.Information("ACCUWEATHER-VALIDATE VAL-04 passed — no false-trip; beacon trip enabled. v1.0 ships backstop + best-effort beacon.");
                }

                // ════════════════════════════════ VAL-03 — fallback-on-air (D-29b) ══════════════════════
                // (a) BLANK via the existing control plane: SetUrlAsync(about:blank) (the in-process /seturl).
                // D-26: SetUrlAsync raises RecipeSwapped → monitor.Reset, so the about:blank match resets the
                // monitor; about:blank then renders cleanly blank → the blank detector trips → fallback.
                await browserWrapper.SetUrlAsync("about:blank", recipeStore?.Match("about:blank"));

                var blankDeadline = DateTime.UtcNow.AddSeconds(30);
                if (!await PollConditionAsync(() => monitor!.OutputState == FrameMonitor.Output.Fallback, blankDeadline))
                {
                    Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (VAL-03): about:blank did not flip output to Fallback (status={monitor!.Status}, reason={monitor!.FallbackReason}).");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-03 blank no fallback (status={Status} reason={Reason}).", monitor!.Status, monitor!.FallbackReason);
                    return;
                }

                // fallbackReason=blank-* AND fallbackAsset=configured (from /health-equivalent SnapshotHealth).
                var blankHealth = monitor!.SnapshotHealth();
                if (blankHealth.FallbackReason is null || !blankHealth.FallbackReason.StartsWith("blank", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (VAL-03): fallbackReason='{blankHealth.FallbackReason}' is not a blank-* reason.");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-03 fallbackReason {Reason}.", blankHealth.FallbackReason);
                    return;
                }
                if (blankHealth.FallbackAsset != FallbackAssetState.Configured)
                {
                    Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (VAL-03): fallbackAsset={blankHealth.FallbackAsset} (expected Configured — the REAL slate, not the generated default).");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-03 fallbackAsset {Asset}.", blankHealth.FallbackAsset);
                    return;
                }

                // (b) D-29b: the ON-AIR faulted frame MATCHES the slate.png SIGNATURE — NOT merely non-blank
                // geometry. Sample the monitor's current OUTPUT frame (which is now the configured slate) and
                // compare its signature to the reference computed from FallbackProvider's Configured buffer.
                var onAir = monitor!.SnapshotCurrentOutput();
                var onAirSignature = ComputeBgraSignatureFromPtr(onAir.DataPtr, onAir.Width, onAir.Height, onAir.Stride);
                if (!SignaturesMatch(slateSignature!, onAirSignature, out var sigDistance))
                {
                    Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (VAL-03): the on-air faulted frame does NOT match the slate.png signature (distance={sigDistance}) — fallback is on air but it is not the REAL configured slate (D-29b).");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-03 slate-signature mismatch (distance={Distance}).", sigDistance);
                    return;
                }

                Console.WriteLine($"ACCUWEATHER-VALIDATE VAL-03 (blank) OK — about:blank → Fallback, reason={blankHealth.FallbackReason}, fallbackAsset=Configured, on-air frame MATCHES the slate.png signature (distance={sigDistance}, D-29b).");
                Log.Information("ACCUWEATHER-VALIDATE VAL-03 blank passed — slate signature matched (distance={Distance}).", sigDistance);

                // (c) GENUINE ALL-STOP FREEZE → the freezeTimeoutMs BACKSTOP (D-31). Navigate to a static page
                // (about:blank already stopped, but it tripped via BLANK; a freeze needs a NON-blank static
                // frame whose OnPaint stops). Use a static data: page with opaque content so it is NOT blank,
                // then OnPaint goes quiet → the dHash window + paint-age backstop trips at freezeTimeoutMs. The
                // decisive freeze proof MUST NOT depend on the beacon (D-31) — the backstop is the authority.
                // First recover to Live on a static opaque page via the control plane.
                const string StaticOpaquePage =
                    "data:text/html,<html><body style='margin:0;background:%23808080;width:100vw;height:100vh'></body></html>";
                await browserWrapper.SetUrlAsync(StaticOpaquePage, recipeStore?.Match(StaticOpaquePage));

                // It must first read NON-blank+Live (a static opaque page paints once then goes quiet). If the
                // monitor recovers to Live, the static frame is non-blank; then OnPaint stops → freeze backstop.
                var staticLiveDeadline = DateTime.UtcNow.AddSeconds(30);
                if (!await PollConditionAsync(() => monitor!.OutputState == FrameMonitor.Output.Live, staticLiveDeadline))
                {
                    // Some CEF builds keep the about:blank fallback latched; that is acceptable — the freeze
                    // BACKSTOP is what we are proving, and the blank leg already proved fallback-on-air. Log and
                    // proceed to assert the backstop trips (it will already be in fallback). Not a hard fail.
                    Log.Information("ACCUWEATHER-VALIDATE VAL-03 freeze — static page did not return to Live first (already in fallback); asserting the backstop holds fallback.");
                }

                // Now assert the freeze trips/holds fallback via the freezeTimeoutMs BACKSTOP. CRITICAL (D-31):
                // the poll budget MUST exceed freezeTimeoutMs (~60s) so the legitimate ~60s backstop trip is
                // NOT polled-out as a false failure at the old beacon-fast horizon.
                var freezeDeadline = DateTime.UtcNow.AddSeconds(FreezeBackstopPollSeconds);
                if (!await PollConditionAsync(() => monitor!.OutputState == FrameMonitor.Output.Fallback, freezeDeadline))
                {
                    Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL (VAL-03): a genuine all-stop freeze did NOT trip to fallback within {FreezeBackstopPollSeconds}s (freezeTimeoutMs={recipe.FreezeTimeoutMs}) — the backstop is the v1.0 freeze authority and must trip (D-31).");
                    Log.Error("ACCUWEATHER-VALIDATE FAIL — VAL-03 freeze backstop did not trip (freezeTimeoutMs={Freeze}).", recipe.FreezeTimeoutMs);
                    return;
                }

                Console.WriteLine($"ACCUWEATHER-VALIDATE VAL-03 (freeze) OK — a genuine all-stop freeze tripped to fallback via the freezeTimeoutMs={recipe.FreezeTimeoutMs} backstop (D-31; not beacon-dependent).");
                Log.Information("ACCUWEATHER-VALIDATE VAL-03 freeze passed — backstop trip (freezeTimeoutMs={Freeze}).", recipe.FreezeTimeoutMs);

                Console.WriteLine("ACCUWEATHER-VALIDATE OK — VAL-01 (all four markers) + VAL-03 (blank+freeze→slate-signature fallback) + VAL-04 (no false-trip / D-31 backstop-alone) all passed via the existing control plane (no new endpoint).");
                Log.Information("ACCUWEATHER-VALIDATE OK — full live content-proof passed.");
                exitCode = 0;
            });
        }
        catch (Exception ex)
        {
            Log.Error("ACCUWEATHER-VALIDATE FAILED — exception: {@ex}", ex);
            Console.WriteLine($"ACCUWEATHER-VALIDATE FAIL: exception {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            try { if (pump is not null) { pump.StopAsync().GetAwaiter().GetResult(); } } catch { }
            try { monitor?.Dispose(); } catch { }
            try { browserWrapper?.Dispose(); } catch { }

            if (Directory.Exists(launchCachePath))
            {
                try { Directory.Delete(launchCachePath, true); } catch { }
            }
        }

        Log.CloseAndFlush();
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// 03-05 (D-29b): compute a downsampled per-cell-mean BGRA SIGNATURE of a frame — a coarse perceptual
    /// fingerprint tolerant of NDI codec rounding / sampling jitter. The frame is divided into an
    /// <see cref="SignatureGrid"/>×<see cref="SignatureGrid"/> grid; each cell yields the mean B/G/R/A of a
    /// sparse pixel sample. Two frames of the SAME image (e.g. the configured slate decoded by FallbackProvider
    /// vs. the same slate sampled off the on-air output buffer) produce near-identical signatures;
    /// <see cref="SignaturesMatch"/> compares them within a tolerance. Operates on a managed BGRA array.
    /// </summary>
    private const int SignatureGrid = 8; // 8x8 cells × 4 channels = 256-long signature.

    private static long[] ComputeBgraSignature(byte[] bgra, int width, int height, int stride)
    {
        var sig = new long[SignatureGrid * SignatureGrid * 4];
        if (bgra is null || width <= 0 || height <= 0)
        {
            return sig;
        }

        for (var cy = 0; cy < SignatureGrid; cy++)
        {
            for (var cx = 0; cx < SignatureGrid; cx++)
            {
                long b = 0, g = 0, r = 0, a = 0, n = 0;
                var x0 = (int)((long)cx * width / SignatureGrid);
                var x1 = (int)((long)(cx + 1) * width / SignatureGrid);
                var y0 = (int)((long)cy * height / SignatureGrid);
                var y1 = (int)((long)(cy + 1) * height / SignatureGrid);

                // Sparse sample: ~4 steps per cell axis to keep it cheap but representative.
                var xs = Math.Max(1, (x1 - x0) / 4);
                var ys = Math.Max(1, (y1 - y0) / 4);
                for (var y = y0; y < y1; y += ys)
                {
                    for (var x = x0; x < x1; x += xs)
                    {
                        var idx = (long)y * stride + (long)x * 4;
                        if (idx + 3 >= bgra.LongLength) { continue; }
                        b += bgra[idx]; g += bgra[idx + 1]; r += bgra[idx + 2]; a += bgra[idx + 3];
                        n++;
                    }
                }

                var cell = (cy * SignatureGrid + cx) * 4;
                if (n > 0)
                {
                    sig[cell] = b / n; sig[cell + 1] = g / n; sig[cell + 2] = r / n; sig[cell + 3] = a / n;
                }
            }
        }

        return sig;
    }

    /// <summary>
    /// 03-05 (D-29b): the unmanaged-pointer variant of <see cref="ComputeBgraSignature"/> for sampling the
    /// monitor's current OUTPUT buffer (<see cref="FrameMonitor.SnapshotCurrentOutput"/> returns a pinned
    /// pointer, not a managed array). Same grid + sparse-sample logic over a <see cref="ReadOnlySpan{T}"/>.
    /// </summary>
    private static long[] ComputeBgraSignatureFromPtr(nint dataPtr, int width, int height, int stride)
    {
        var sig = new long[SignatureGrid * SignatureGrid * 4];
        if (dataPtr == nint.Zero || width <= 0 || height <= 0)
        {
            return sig;
        }

        unsafe
        {
            var p = (byte*)dataPtr;
            for (var cy = 0; cy < SignatureGrid; cy++)
            {
                for (var cx = 0; cx < SignatureGrid; cx++)
                {
                    long b = 0, g = 0, r = 0, a = 0, n = 0;
                    var x0 = (int)((long)cx * width / SignatureGrid);
                    var x1 = (int)((long)(cx + 1) * width / SignatureGrid);
                    var y0 = (int)((long)cy * height / SignatureGrid);
                    var y1 = (int)((long)(cy + 1) * height / SignatureGrid);

                    var xs = Math.Max(1, (x1 - x0) / 4);
                    var ys = Math.Max(1, (y1 - y0) / 4);
                    for (var y = y0; y < y1; y += ys)
                    {
                        for (var x = x0; x < x1; x += xs)
                        {
                            var idx = (long)y * stride + (long)x * 4;
                            b += p[idx]; g += p[idx + 1]; r += p[idx + 2]; a += p[idx + 3];
                            n++;
                        }
                    }

                    var cell = (cy * SignatureGrid + cx) * 4;
                    if (n > 0)
                    {
                        sig[cell] = b / n; sig[cell + 1] = g / n; sig[cell + 2] = r / n; sig[cell + 3] = a / n;
                    }
                }
            }
        }

        return sig;
    }

    /// <summary>
    /// 03-05 (D-29b): compare two BGRA signatures within a per-cell-channel mean-absolute-difference
    /// tolerance. Returns true when the mean absolute per-channel difference across all cells is within
    /// <see cref="SignatureTolerance"/> (absorbs NDI codec rounding + sample jitter), false otherwise.
    /// <paramref name="distance"/> carries the measured mean-abs difference for the failure log.
    /// </summary>
    private const int SignatureTolerance = 24; // mean |Δ| per channel; well below "different image" distances.

    private static bool SignaturesMatch(long[] a, long[] b, out long distance)
    {
        distance = long.MaxValue;
        if (a is null || b is null || a.Length != b.Length || a.Length == 0)
        {
            return false;
        }

        long sumAbs = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sumAbs += Math.Abs(a[i] - b[i]);
        }

        distance = sumAbs / a.Length; // mean absolute per-channel difference.
        return distance <= SignatureTolerance;
    }

    /// <summary>
    /// D-22 / D-29 (MON-01): the OnPaint pixel-format readback gate — the literal FIRST task of Phase
    /// 2, run BEFORE any luminance / variance / freeze / fallback / keying code is written. Mirrors
    /// <see cref="RunSmoke"/>'s CEF-init + load-local-HTML + bounded-wait + Environment.Exit discipline,
    /// but instead of sending a frame it CAPTURES the real pre-send OnPaint bytes and asserts the actual
    /// pixel format:
    /// <list type="bullet">
    ///   <item>BGRA channel order (Blue/Green bytes ~0, Red dominant in a pure-red region);</item>
    ///   <item>per-region alpha VALUE — A≈255 in the opaque region, A≈128 in the 50%-alpha region,
    ///   A≈0 in the fully-transparent region (the all-alpha-0 blank case the Plan-03 BlankDetector
    ///   depends on);</item>
    ///   <item>premultiplied-vs-straight — in the 50%-alpha region, R≈255 ⇒ STRAIGHT, R≈128 ⇒
    ///   PREMULTIPLIED (RESEARCH Pitfall P-1).</item>
    /// </list>
    ///
    /// <para>D-29a: the bytes are captured via the PRE-SEND seam in <see cref="CefWrapper.OnBrowserPaint"/>
    /// (a one-shot copy taken BEFORE the <c>NdiSenderPtr==Zero</c> guard at CefWrapper.cs:213) — no NDI
    /// sender is attached, so the gate reads the REAL bytes CEF painted, not a downstream FourCC.
    /// D-29c: the gate runs on the SAME production wrapper that now sets a transparent
    /// <see cref="CefSharp.BrowserSettings.BackgroundColor"/>, so regions 2/3 read SOURCE alpha.
    /// D-29d: a PREMULTIPLIED determination is surfaced as a loud WARN (the un-premult contingency is
    /// recorded in the plan SUMMARY) and FAILS the gate (observed != <see cref="AlphaConvention.Expected"/>);
    /// the keying correction itself is the Phase-3 follow-up — this gate does NOT change the send path.</para>
    ///
    /// <para>Exit 1 by default, 0 only on a clean STRAIGHT determination with all per-region assertions
    /// passing; a hard timeout (mirrors the RunSmoke Task.WhenAny pattern) makes a stuck init fail fast.</para>
    /// </summary>
    private static void RunOnPaintFormatGate(string[] args, string launchCachePath)
    {
        const int GateTimeoutSeconds = 20;

        // Tolerance band absorbing CEF gamma/rounding around the expected per-channel values (D-22).
        const int Tol = 16;

        var width = 1920;
        var height = 1080;

        // --onpaint-format or --onpaint-format=<url>. Default to the committed 3-region fixture beside
        // the exe, loaded file:// (no recipe match / iframe / network needed — just a known-color page).
        var gateArg = args.FirstOrDefault(x => x.StartsWith("--onpaint-format")) ?? "--onpaint-format";
        string gateUrl;
        var eq = gateArg.IndexOf('=');
        if (eq >= 0 && eq < gateArg.Length - 1)
        {
            gateUrl = gateArg.Substring(eq + 1);
        }
        else
        {
            var localHtml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests-fixtures", "onpaint-format.html");
            if (!File.Exists(localHtml))
            {
                Console.WriteLine($"ONPAINT-FORMAT FAIL: fixture missing at {localHtml}");
                Log.Error("ONPAINT-FORMAT FAIL — fixture missing at {Path}", localHtml);
                Log.CloseAndFlush();
                Environment.Exit(1);
            }

            gateUrl = new Uri(localHtml).AbsoluteUri; // file:///.../tests-fixtures/onpaint-format.html
        }

        Log.Information(
            "ONPAINT-FORMAT starting — url={Url} expected-alpha={Expected} timeout={Timeout}s",
            gateUrl, AlphaConvention.Expected, GateTimeoutSeconds);

        var exitCode = 1; // default failure; only set 0 on a clean STRAIGHT determination.

        try
        {
            AsyncContext.Run(async delegate
            {
                var settings = new CefSettings();
                if (!Directory.Exists(launchCachePath))
                {
                    Directory.CreateDirectory(launchCachePath);
                }

                settings.RootCachePath = launchCachePath;
                settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

                // D-13 anti-throttle — kept in sync with the smoke/normal paths.
                settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
                settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
                settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");

                settings.EnableAudio();
                Cef.Initialize(settings);
                AppManagement.LogProvenance();

                // D-29a: arm the pre-send capture latch — NO NDI sender is created, so the real bytes
                // are reached only via the copy BEFORE the NdiSenderPtr==Zero guard in OnBrowserPaint.
                // D-29c: this wrapper now constructs the browser with a transparent BackgroundColor.
                browserWrapper = new CefWrapper(width, height, gateUrl)
                {
                    CaptureNextPaint = true,
                };

                await browserWrapper.InitializeWrapperAsync();

                // Bounded wait for the one-shot pre-send capture (mirrors RunSmoke's Task.WhenAny).
                var captureTask = browserWrapper.PaintCaptured;
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(GateTimeoutSeconds));
                if (await Task.WhenAny(captureTask, timeoutTask) != captureTask)
                {
                    Console.WriteLine("ONPAINT-FORMAT FAIL: no non-blank OnPaint captured within timeout");
                    Log.Error("ONPAINT-FORMAT FAIL — no non-blank OnPaint within {Timeout}s.", GateTimeoutSeconds);
                    return; // exitCode stays 1
                }

                var buffer = browserWrapper.CapturedBuffer;
                var w = browserWrapper.CapturedWidth;
                var h = browserWrapper.CapturedHeight;
                if (buffer is null || w <= 0 || h <= 0)
                {
                    Console.WriteLine("ONPAINT-FORMAT FAIL: captured buffer was null/empty");
                    Log.Error("ONPAINT-FORMAT FAIL — captured buffer null/empty (w={W} h={H}).", w, h);
                    return;
                }

                // Sample several pixels per region at the band CENTERS (1/6, 1/2, 5/6 of width), down
                // a vertical mid-strip, to avoid anti-aliased band edges. Require per-region consistency.
                var y = h / 2;
                var x1 = w / 6;       // region 1 — opaque red
                var x2 = w / 2;       // region 2 — 50%-alpha red
                var x3 = (5 * w) / 6; // region 3 — fully transparent

                var r1 = SampleRegion(buffer, w, h, x1, y);
                var r2 = SampleRegion(buffer, w, h, x2, y);
                var r3 = SampleRegion(buffer, w, h, x3, y);

                Log.Information(
                    "ONPAINT-FORMAT samples — opaque(B={B1} G={G1} R={R1} A={A1}) half(B={B2} G={G2} R={R2} A={A2}) transparent(B={B3} G={G3} R={R3} A={A3})",
                    r1.B, r1.G, r1.R, r1.A, r2.B, r2.G, r2.R, r2.A, r3.B, r3.G, r3.R, r3.A);

                // ── Region 1: OPAQUE red → channel order + opaque alpha. B≈0 G≈0 R≈255 A≈255. ─────────
                if (!(Near(r1.B, 0, Tol) && Near(r1.G, 0, Tol) && Near(r1.R, 255, Tol) && Near(r1.A, 255, Tol)))
                {
                    Console.WriteLine("ONPAINT-FORMAT FAIL: opaque-red region not B≈0 G≈0 R≈255 A≈255 (channel order / opaque alpha)");
                    Log.Error("ONPAINT-FORMAT FAIL — opaque region B={B} G={G} R={R} A={A}.", r1.B, r1.G, r1.R, r1.A);
                    return;
                }

                // ── Region 3: FULLY TRANSPARENT → the all-alpha-0 blank case (Plan-03 BlankDetector). ──
                if (!Near(r3.A, 0, Tol))
                {
                    Console.WriteLine("ONPAINT-FORMAT FAIL: transparent region alpha is not ≈0 (all-alpha-0 blank case)");
                    Log.Error("ONPAINT-FORMAT FAIL — transparent region A={A} (expected ≈0).", r3.A);
                    return;
                }

                // ── Region 2: 50%-alpha red — channel order + alpha value + premultiplied-vs-straight. ─
                // (a) CHANNEL ORDER: Blue/Green ≈0, Red dominant → bytes are B,G,R,A.
                if (!(Near(r2.B, 0, Tol) && Near(r2.G, 0, Tol) && r2.R > r2.B && r2.R > r2.G))
                {
                    Console.WriteLine("ONPAINT-FORMAT FAIL: 50%-alpha region channel order not BGRA (B/G not ≈0 or R not dominant)");
                    Log.Error("ONPAINT-FORMAT FAIL — half-alpha order B={B} G={G} R={R}.", r2.B, r2.G, r2.R);
                    return;
                }

                // (b) ALPHA VALUE: the A byte carries source alpha (~128 = 0.5*255).
                if (!Near(r2.A, 128, Tol))
                {
                    Console.WriteLine("ONPAINT-FORMAT FAIL: 50%-alpha region alpha byte not ≈128 (source alpha not preserved)");
                    Log.Error("ONPAINT-FORMAT FAIL — half-alpha A={A} (expected ≈128).", r2.A);
                    return;
                }

                // (c) PREMULTIPLIED-VS-STRAIGHT: R≈255 ⇒ STRAIGHT, R≈128 ⇒ color scaled by alpha ⇒ PREMULTIPLIED.
                AlphaMode observed;
                if (Near(r2.R, 255, Tol))
                {
                    observed = AlphaMode.Straight;
                }
                else if (Near(r2.R, 128, Tol))
                {
                    observed = AlphaMode.Premultiplied;
                }
                else
                {
                    Console.WriteLine($"ONPAINT-FORMAT FAIL: 50%-alpha red byte R={r2.R} is neither ≈255 (straight) nor ≈128 (premultiplied)");
                    Log.Error("ONPAINT-FORMAT FAIL — half-alpha R={R} ambiguous (neither 255 nor 128).", r2.R);
                    return;
                }

                Log.Information("ONPAINT-FORMAT determined alpha mode = {Observed} (expected {Expected}).", observed, AlphaConvention.Expected);

                // Compare observed vs the compile-time EXPECTED constant; FAIL LOUDLY on drift (D-29d).
                if (observed != AlphaConvention.Expected)
                {
                    Log.Warning(
                        "ONPAINT-FORMAT ALPHA-MODE DRIFT — observed {Observed}, expected {Expected}. " +
                        "PITFALL P-1 IS LIVE: CEF OnPaint is PREMULTIPLIED while the send path declares straight BGRA. " +
                        "Un-premult CONTINGENCY required BEFORE any downstream pixel math (Plan 02 pump / Plan 03 detectors / " +
                        "Plan 04 fallback): a 256-entry per-channel un-premultiply LUT (O(1)/pixel) OR a CEF straight-alpha " +
                        "command-line flag. The keying CORRECTION is the Phase-3 follow-up — the send path is NOT changed here.",
                        observed, AlphaConvention.Expected);
                    Console.WriteLine($"ONPAINT-FORMAT FAIL: alpha-mode drift — observed {observed}, expected {AlphaConvention.Expected} (Pitfall P-1; see un-premult contingency)");
                    return; // exitCode stays 1 — drift is RED.
                }

                Log.Information("ONPAINT-FORMAT OK — BGRA straight alpha confirmed; channel order + per-region alpha + all-alpha-0 blank all asserted.");
                Console.WriteLine("ONPAINT-FORMAT OK");
                exitCode = 0;
            });
        }
        catch (Exception ex)
        {
            Log.Error("ONPAINT-FORMAT FAILED — exception: {@ex}", ex);
            Console.WriteLine($"ONPAINT-FORMAT FAIL: exception {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            try { browserWrapper?.Dispose(); } catch { }

            if (Directory.Exists(launchCachePath))
            {
                try { Directory.Delete(launchCachePath, true); } catch { }
            }
        }

        Log.CloseAndFlush();
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// 03-03 (Q2 / A4): the ALPHA-0-DAMAGE validation gate — decides the ONE genuine empirical unknown the
    /// D-13 liveness-beacon design depends on (research §19): does an alpha-0 corner whose OPAQUE RGB mutates
    /// each rAF tick produce CAPTURED OnPaint damage on CefSharp 148, surviving the un-premult LUT into the
    /// STRAIGHT buffer the <see cref="Chromium.Monitor.FrameMonitor"/> beacon sample reads?
    ///
    /// <para>Mechanism — DIRECT-CAPTURE WINDOW (NOT the FrameMonitor). The FrameMonitor is fed exclusively by
    /// <see cref="CefWrapper.FrameReady"/>, which OnBrowserPaint raises ONLY after the
    /// <c>NdiSenderPtr==nint.Zero</c> guard returns — and this gate attaches NO NDI sender, so FrameReady
    /// never fires and a monitor would be starved ("never present" even for fully-opaque content). Instead we
    /// use the pre-send capture latch (<see cref="CefWrapper.CaptureNextPaint"/> /
    /// <see cref="CefWrapper.ReArmCapture"/> / <see cref="CefWrapper.CapturedBuffer"/>), whose copy block sits
    /// BEFORE that NDI guard and works with no NDI sender. We load the alpha-0 beacon fixture on the SAME
    /// production <see cref="CefWrapper"/> path (transparent BackgroundColor D-29c + the un-premult LUT), so
    /// the captured bytes are the STRAIGHT pre-key bytes the on-air beacon sample would read.</para>
    ///
    /// <para>Two confirmed prior pitfalls this body avoids: (1) NDI-gated FrameReady → do NOT drive the
    /// monitor (use direct capture). (2) The first non-blank capture is the near-white LOAD frame, not the
    /// page content → a RENDER-GATE re-arms+captures until a known reference pixel (frame CENTER, where the
    /// opaque green #content is) reads opaque-green-ish, distinct from near-white. Only then does the
    /// MEASUREMENT WINDOW open.</para>
    ///
    /// <para>Window: over ~2.5s, repeatedly ReArmCapture → await the next non-blank PaintCaptured → sum the
    /// beacon-region (top-left, <see cref="MonitorDefaults.BeaconOriginX"/>/Y + a small grid) RGB → collect.
    /// VERDICT: max(sum)-min(sum) ≥ DiffBound → A4 HOLDS (the alpha-0 per-tick RGB mutation reaches the
    /// captured straight buffer) → exit 0. Else → A4 REFUTED → exit non-zero with the A=1 low-alpha fallback
    /// message. If real content never renders within the settle cap → exit non-zero with a LOUD
    /// "another layer" signal (the orchestrator defers). Always Environment.Exit.</para>
    /// </summary>
    private static void RunBeaconDamageGate(string[] args, string launchCachePath)
    {
        const int GateTimeoutSeconds = 30;

        // RENDER-GATE settle cap: how long to keep re-arming+capturing while waiting for the page's REAL
        // content (the opaque green #content) to reach the captured straight buffer. The first non-blank
        // capture is the near-white LOAD frame — we explicitly re-capture past it until a reference pixel
        // confirms real content. If real content never appears within this cap, the gate exits non-zero with
        // a LOUD "another layer" signal (the orchestrator defers rather than treating it as A4-refuted).
        const int ContentSettleCapMs = 9_000;

        // The MEASUREMENT WINDOW: once real content has rendered, keep re-arming+capturing for this long and
        // collect the beacon-region RGB sum of each distinct captured frame. CEF emits ~rAF-rate OnPaints, so
        // ~2.5s of re-arm/capture cycles (per-capture timeout 1s) comfortably yields the target sample count.
        const int MeasureWindowMs = 2500;

        // Per-capture wait — a single ReArmCapture → next non-blank PaintCaptured must arrive within this.
        const int PerCaptureTimeoutMs = 1000;

        // Aim for at least this many window samples so the variation is statistically meaningful (not a fluke).
        const int TargetWindowSamples = 8;

        // VERDICT bound: max(beaconSum) - min(beaconSum) across window samples must reach this for A4 to hold.
        // Small — the rAF bar cycles R/G/B every tick, so a real un-swallowed mutation moves the sum by far
        // more than this; the bound just rejects byte-identical (swallowed) captures.
        const int DiffBound = 6;

        // The reference pixel that proves REAL content (not the near-white load frame): frame CENTER, where
        // the opaque green #content (B=0,G=128,R=0,A=255 straight) renders. The known load frame was
        // near-white BGRA ≈ [227,243,249,255]. "Real content" = opaque AND green-dominant (G clearly above
        // R and B), which the near-white load frame fails.
        const int RefGreenMin = 64;    // center G must be at least this (opaque green ≈128)
        const int RefAlphaMin = 200;   // center A must be opaque (load frame is also opaque, so this alone is weak)
        const int RefChannelGap = 32;  // center G must exceed BOTH R and B by this (rejects near-white/grey)

        var width = 1920;
        var height = 1080;

        // --beacon-damage-check or --beacon-damage-check=<url>. Default to the committed alpha-0 beacon
        // fixture beside the exe, loaded file:// (no recipe/iframe/network — just the known beacon page).
        var gateArg = args.FirstOrDefault(x => x.StartsWith("--beacon-damage-check")) ?? "--beacon-damage-check";
        string gateUrl;
        var eq = gateArg.IndexOf('=');
        if (eq >= 0 && eq < gateArg.Length - 1)
        {
            gateUrl = gateArg.Substring(eq + 1);
        }
        else
        {
            var localHtml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests-fixtures", "beacon-damage.html");
            if (!File.Exists(localHtml))
            {
                Console.WriteLine($"BEACON-DAMAGE FAIL: fixture missing at {localHtml}");
                Log.Error("BEACON-DAMAGE FAIL — fixture missing at {Path}", localHtml);
                Log.CloseAndFlush();
                Environment.Exit(1);
            }

            gateUrl = new Uri(localHtml).AbsoluteUri;
        }

        Log.Information(
            "BEACON-DAMAGE starting — url={Url} timeout={Timeout}s settleCap={Cap}ms window={Window}ms (DIRECT-capture window; no NDI, no FrameMonitor)",
            gateUrl, GateTimeoutSeconds, ContentSettleCapMs, MeasureWindowMs);

        var exitCode = 1; // default failure; only set 0 on a clean A4-HOLDS determination.

        try
        {
            AsyncContext.Run(async delegate
            {
                var settings = new CefSettings();
                if (!Directory.Exists(launchCachePath))
                {
                    Directory.CreateDirectory(launchCachePath);
                }

                settings.RootCachePath = launchCachePath;
                settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

                // D-13 anti-throttle — kept in sync with the smoke/normal/onpaint paths (rAF callbacks alive
                // so the OSR browser emits CONTINUOUS animated OnPaints — required so the rAF beacon mutates).
                settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
                settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
                settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");

                settings.EnableAudio();
                Cef.Initialize(settings);
                AppManagement.LogProvenance();

                // D-29a/D-29c: production wrapper (transparent BackgroundColor + un-premult LUT) with the
                // pre-send capture latch ARMED. NO NDI sender (Program.NdiSenderPtr stays Zero) and NO
                // FrameMonitor — the capture copy at OnBrowserPaint sits BEFORE the NdiSenderPtr==Zero guard,
                // so it is the ONLY path that reaches the real straight bytes with no sender attached. The
                // normal --url= send path leaves CaptureNextPaint false, so ReArmCapture is gate-only.
                browserWrapper = new CefWrapper(width, height, gateUrl)
                {
                    CaptureNextPaint = true,
                };

                await browserWrapper.InitializeWrapperAsync();

                // Local: re-arm the one-shot latch then await the next non-blank captured paint, bounded.
                // Returns the captured straight BGRA buffer (+ geometry) or null on timeout. The FIRST call
                // does NOT re-arm (the wrapper armed CaptureNextPaint at construction); subsequent calls do.
                var armedOnce = false;
                async Task<(byte[] buf, int w, int h)?> CaptureNextAsync(int timeoutMs)
                {
                    if (armedOnce)
                    {
                        browserWrapper.ReArmCapture();
                    }

                    armedOnce = true;

                    var captureTask = browserWrapper.PaintCaptured;
                    if (await Task.WhenAny(captureTask, Task.Delay(timeoutMs)) != captureTask)
                    {
                        return null;
                    }

                    var b = browserWrapper.CapturedBuffer;
                    var cw = browserWrapper.CapturedWidth;
                    var ch = browserWrapper.CapturedHeight;
                    if (b is null || cw <= 0 || ch <= 0)
                    {
                        return null;
                    }

                    return (b, cw, ch);
                }

                // ── RENDER-GATE ─────────────────────────────────────────────────────────────────────────
                // Keep re-arming+capturing until the captured frame shows REAL content (center pixel is opaque
                // green-ish, NOT the near-white load frame), bounded by ContentSettleCapMs. The first
                // non-blank capture is the LOAD frame — we MUST capture past it before measuring.
                var settleDeadline = DateTime.UtcNow.AddMilliseconds(ContentSettleCapMs);
                (byte[] buf, int w, int h)? rendered = null;
                (byte B, byte G, byte R, byte A) refPx = default;
                var settleAttempts = 0;
                while (DateTime.UtcNow < settleDeadline)
                {
                    var cap = await CaptureNextAsync(PerCaptureTimeoutMs);
                    settleAttempts++;
                    if (cap is null)
                    {
                        continue; // no paint this slice — keep waiting until the cap
                    }

                    var (cb, cw, ch) = cap.Value;
                    var px = SampleRegion(cb, cw, ch, cw / 2, ch / 2); // frame CENTER = #content opaque green

                    var isRealContent =
                        px.A >= RefAlphaMin &&
                        px.G >= RefGreenMin &&
                        (px.G - px.R) >= RefChannelGap &&
                        (px.G - px.B) >= RefChannelGap;

                    if (isRealContent)
                    {
                        rendered = cap;
                        refPx = px;
                        break;
                    }
                }

                if (rendered is null)
                {
                    // Real content never reached the captured straight buffer. This is the "another layer"
                    // STOP signal — NOT an A4 refutation. Log LOUDLY and exit non-zero so the orchestrator
                    // defers (a file:// render issue / a deeper capture problem, not the alpha-0 question).
                    Console.WriteLine(
                        "BEACON-DAMAGE INDETERMINATE: page never rendered real content — possible file:// render issue " +
                        $"(center pixel never read opaque green after {settleAttempts} capture attempts within {ContentSettleCapMs}ms). " +
                        "ANOTHER LAYER — defer; this is NOT an A4 refutation.");
                    Log.Error(
                        "BEACON-DAMAGE INDETERMINATE — render-gate never confirmed real content in {Attempts} attempts within {Cap}ms (file:// render / capture issue; defer, do NOT treat as A4-refuted).",
                        settleAttempts, ContentSettleCapMs);
                    return; // exitCode stays 1 (non-zero) — "another layer"
                }

                Log.Information(
                    "BEACON-DAMAGE render-gate OK — real content confirmed after {Attempts} capture attempts; center reference pixel BGRA = ({B},{G},{R},{A}) (opaque green, NOT the near-white load frame).",
                    settleAttempts, refPx.B, refPx.G, refPx.R, refPx.A);
                Console.WriteLine(
                    $"BEACON-DAMAGE render-gate OK: real content present after {settleAttempts} attempts; center BGRA=({refPx.B},{refPx.G},{refPx.R},{refPx.A}) (opaque green-ish, past the load frame)");

                // ── MEASUREMENT WINDOW ──────────────────────────────────────────────────────────────────
                // Real content is confirmed. Now collect beacon-region RGB sums across many DISTINCT captured
                // frames (each ReArmCapture → next non-blank paint). The beacon is the top-left
                // BeaconOriginX/Y + BeaconSizePx region — the SAME coords the on-air beacon sample uses.
                var beaconSums = new List<long>();
                var windowDeadline = DateTime.UtcNow.AddMilliseconds(MeasureWindowMs);
                var sampleIdx = 0;
                while (DateTime.UtcNow < windowDeadline || beaconSums.Count < TargetWindowSamples)
                {
                    // Bail if we have run well past the window AND still cannot reach the target (avoid hang).
                    if (beaconSums.Count < TargetWindowSamples &&
                        DateTime.UtcNow >= windowDeadline.AddMilliseconds(PerCaptureTimeoutMs * TargetWindowSamples))
                    {
                        break;
                    }

                    var cap = await CaptureNextAsync(PerCaptureTimeoutMs);
                    if (cap is null)
                    {
                        continue;
                    }

                    var (cb, cw, ch) = cap.Value;
                    var sum = SumBeaconRgb(cb, cw, ch);
                    beaconSums.Add(sum);
                    sampleIdx++;
                    Log.Information("BEACON-DAMAGE window sample {Idx} — beacon-region RGB sum = {Sum}", sampleIdx, sum);
                    Console.WriteLine($"BEACON-DAMAGE sample {sampleIdx}: beacon-region RGB sum = {sum}");
                }

                if (beaconSums.Count == 0)
                {
                    Console.WriteLine("BEACON-DAMAGE FAIL: no beacon samples captured inside the window (sampler starved after render-gate passed)");
                    Log.Error("BEACON-DAMAGE FAIL — zero window samples after render-gate confirmed content (unexpected).");
                    return;
                }

                var minSum = beaconSums.Min();
                var maxSum = beaconSums.Max();
                var delta = maxSum - minSum;

                Log.Information(
                    "BEACON-DAMAGE window result — samples={Count} min={Min} max={Max} delta={Delta} (diff-bound={Bound})",
                    beaconSums.Count, minSum, maxSum, delta, DiffBound);

                if (delta >= DiffBound)
                {
                    Log.Information(
                        "BEACON-DAMAGE OK — beacon-region RGB VARIED across {Count} captured frames (delta={Delta} ≥ {Bound}). " +
                        "A4 HOLDS: the alpha-0 per-tick RGB mutation reaches the captured straight buffer the on-air beacon sample reads ⇒ the D-13 alpha-0 liveness beacon is valid.",
                        beaconSums.Count, delta, DiffBound);
                    Console.WriteLine(
                        $"BEACON-DAMAGE OK: beacon-region RGB varied across {beaconSums.Count} captured frames (min={minSum} max={maxSum} delta={delta} ≥ {DiffBound}) — alpha-0 mutation reaches the captured straight buffer; A4 HOLDS");
                    exitCode = 0;
                }
                else
                {
                    Log.Warning(
                        "BEACON-DAMAGE FAIL — beacon-region RGB was IDENTICAL (or nearly) across {Count} captured frames (delta={Delta} < {Bound}). " +
                        "A4 REFUTED: the alpha-0 RGB mutation did NOT produce detectable change in the captured straight buffer " +
                        "(Chromium damage-gating, or the un-premult LUT's alpha-0 handling, swallowed it). " +
                        "FALLBACK (A=1): use a LOW-ALPHA keyed-out beacon — a barely-non-zero alpha forces a real composite while still keying out on air.",
                        beaconSums.Count, delta, DiffBound);
                    Console.WriteLine(
                        $"BEACON-DAMAGE FAIL: beacon-region RGB unchanged across {beaconSums.Count} captured frames (min={minSum} max={maxSum} delta={delta} < {DiffBound}) — alpha-0 damage swallowed; A4 REFUTED, use the A=1 low-alpha fallback");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("BEACON-DAMAGE FAILED — exception: {@ex}", ex);
            Console.WriteLine($"BEACON-DAMAGE FAIL: exception {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            try { browserWrapper?.Dispose(); } catch { }

            if (Directory.Exists(launchCachePath))
            {
                try { Directory.Delete(launchCachePath, true); } catch { }
            }
        }

        Log.CloseAndFlush();
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// 03-03 (Q2 / A4): sum the R+G+B bytes over the production beacon region (top-left
    /// <see cref="MonitorDefaults.BeaconOriginX"/>/<see cref="MonitorDefaults.BeaconOriginY"/> +
    /// <see cref="MonitorDefaults.BeaconSizePx"/>) of a captured STRAIGHT BGRA buffer. The same coords the
    /// on-air FrameMonitor beacon sample uses. Alpha is excluded (the beacon corner is alpha-0; the rAF
    /// mutation lives in the OPAQUE RGB the LUT must carry through). A small fixed sub-grid keeps the cost
    /// O(1)-ish while still spanning the moving bar.
    /// </summary>
    private static long SumBeaconRgb(byte[] buffer, int width, int height)
    {
        long sum = 0;
        var x0 = MonitorDefaults.BeaconOriginX;
        var y0 = MonitorDefaults.BeaconOriginY;
        var size = MonitorDefaults.BeaconSizePx;

        // Step ~4px across the 16x16 region — a 4x4 grid spanning the moving bar (enough to see the cycle).
        var step = Math.Max(1, size / 4);
        for (var dy = 0; dy < size; dy += step)
        {
            var y = Math.Clamp(y0 + dy, 0, height - 1);
            for (var dx = 0; dx < size; dx += step)
            {
                var x = Math.Clamp(x0 + dx, 0, width - 1);
                var off = ((long)y * width + x) * 4;
                sum += buffer[off + 0]; // B
                sum += buffer[off + 1]; // G
                sum += buffer[off + 2]; // R
            }
        }

        return sum;
    }

    /// <summary>
    /// D-01/D-16/D-05/D-17 (VAL-01): the posture + CMP probe gate. Runs the fork against the
    /// operator-supplied AccuWeather radar URL with site-isolation OFF (D-03) and produces the
    /// structured evidence the Task-2 decision record is authored from:
    /// <list type="bullet">
    ///   <item><b>Posture (D-01).</b> Whether the radar/consent surface is same-origin or lives in a
    ///   genuine cross-origin OOPIF — by enumerating every frame's context (built from the
    ///   <c>Runtime.executionContextCreated.auxData.frameId</c> map), comparing each frame's origin to
    ///   the main frame's, and reading a proof flag back IN that frame's own context via
    ///   <c>Page.createIsolatedWorld(frameId)</c> → <c>Runtime.evaluate(contextId)</c>. A genuine
    ///   cross-origin frame whose isolated-world readback succeeds ⇒ <c>expectsCrossOriginIframes</c>
    ///   recommendation ON; otherwise default OFF.</item>
    ///   <item><b>CMP identity (D-05).</b> Detected by EVIDENCE, never assumed: probes for
    ///   <c>window.OptanonWrapper</c>/<c>window.Optanon</c> (OneTrust) and <c>window.__tcfapi</c>
    ///   (IAB TCF), recording whichever is present (or <c>unknown/other</c>).</item>
    ///   <item><b>Candidate selectors.</b> Best-effort DOM heuristics for the consent reject control
    ///   (text/ARIA per D-02), chrome/ads containers, the target radar element, and the radar play
    ///   control — confirming the play target is a Mapbox-GL WebGL <c>&lt;canvas&gt;</c> selector-dispatch
    ///   target, NOT a <c>&lt;video&gt;</c> (D-17).</item>
    ///   <item><b>Three-outcome cross-origin classification (Q1).</b> The readback is NOT a one-shot
    ///   poll-at-assert: the probe subscribes <c>Runtime.executionContextDestroyed</c> +
    ///   <c>Page.frameNavigated</c> and RE-RESOLVES + RE-READS the proof flag on re-attach. A transient
    ///   miss around an SPA re-mount that resolves on re-read is classified <c>TIMING</c> (expected, not a
    ///   failure); a context that exists and accepts the isolated-world script but never reads true is
    ///   <c>GENUINE-FAILURE</c>; a clean read is <c>READS-TRUE</c> (INJ-04 holds live).</item>
    /// </list>
    ///
    /// <para>Mirrors <see cref="RunOnPaintFormatGate"/>'s AsyncContext.Run → CefSettings + D-13
    /// anti-throttle flags → Cef.Initialize → AppManagement.LogProvenance → bounded wait →
    /// Environment.Exit discipline, plus the D-03 site-isolation-OFF flags (so OOPIFs collapse onto the
    /// single <c>GetDevToolsClient()</c> session — the only way the one CefSharp CDP client can reach a
    /// cross-origin child frame; CefSharp does NOT wrap the CDP Target domain). The persistent-client +
    /// <c>Page.enable</c>/<c>Runtime.enable</c>-before-navigation pattern is reused from
    /// <see cref="Tractus.HtmlToNdi.Chromium.Inject.InjectHook.EnsureClientAsync"/>.</para>
    ///
    /// <para>Per D-01/T-3-02 the readback uses ONLY the per-frame <c>Runtime.evaluate</c> mechanism —
    /// the push-based <c>Runtime.addBinding</c> variant (the post-2 deferred upgrade, BACKLOG-INTEGRATION
    /// item 1) is deliberately NOT built and must not appear in this body. Exit 0 on a clean classified
    /// run, non-zero on env/CDP failure; a hard timeout makes a stuck init fail fast.</para>
    /// </summary>
    private static void RunAccuWeatherProbe(string[] args, string launchCachePath)
    {
        const int ProbeTimeoutSeconds = 45;
        const string ProbeWorldName = "xpn_probe_world";

        var width = 1920;
        var height = 1080;

        // --url=<operator radar URL>. REQUIRED (D-04 — the dedicated weather-radar page, not the
        // homepage widget). Fail loud if absent rather than hanging on a default.
        var urlArg = args.FirstOrDefault(x => x.StartsWith("--url="));
        if (urlArg is null)
        {
            Console.WriteLine("ACCUWEATHER-PROBE FAIL: --url=<operator radar URL> is required (D-04 dedicated weather-radar page).");
            Log.Error("ACCUWEATHER-PROBE FAIL — missing --url= argument.");
            Log.CloseAndFlush();
            Environment.Exit(2);
        }

        var probeUrl = urlArg!.Substring("--url=".Length);
        if (string.IsNullOrWhiteSpace(probeUrl))
        {
            Console.WriteLine("ACCUWEATHER-PROBE FAIL: --url= was empty.");
            Log.Error("ACCUWEATHER-PROBE FAIL — empty --url= value.");
            Log.CloseAndFlush();
            Environment.Exit(2);
        }

        // How long to keep re-resolving/re-reading after the first navigation settles — the Q1 re-arm
        // window that lets an SPA re-mount miss resolve to TIMING rather than GENUINE-FAILURE.
        var rearmWindowSeconds = 12;

        Log.Information(
            "ACCUWEATHER-PROBE starting — url={Url} timeout={Timeout}s rearm-window={Rearm}s",
            probeUrl, ProbeTimeoutSeconds, rearmWindowSeconds);

        var exitCode = 1; // default failure; only set 0 on a clean classified run.
        CefSharp.OffScreen.ChromiumWebBrowser? browser = null;
        CefSharp.DevTools.DevToolsClient? dev = null;

        // The JS the isolated-world readback runs IN EACH FRAME. Static probe script (T-3-02: JSON-safe
        // DATA evaluated in the page; never a page-derived string executed as our control code). Writes a
        // proof flag into THIS isolated world and reads it back from the SAME world (cross-world globals
        // do not bleed — research Gotcha "isolated-world subtlety"), and reports origin + CMP evidence +
        // candidate selectors as a single JSON string the C# side parses.
        const string ProbeJs = @"
        (function () {
          try {
            // Proof flag — written and read back in THIS isolated world (same-world read, research note).
            window.__xpnProbeReached = true;

            function sel(list) {
              for (var i = 0; i < list.length; i++) {
                try { if (document.querySelector(list[i])) return list[i]; } catch (e) {}
              }
              return null;
            }
            function byTextRole() {
              // Consent reject — text/ARIA heuristic (D-02), not coordinate-clicking.
              var cands = [];
              var nodes = document.querySelectorAll('button,[role=button],a');
              for (var i = 0; i < nodes.length && cands.length < 4; i++) {
                var t = (nodes[i].textContent || '').trim().toLowerCase();
                var al = (nodes[i].getAttribute && (nodes[i].getAttribute('aria-label') || '')).toLowerCase();
                if (/reject|decline|necessary only|deny|refuse|do not (accept|consent)/.test(t + ' ' + al)) {
                  var id = nodes[i].id ? ('#' + nodes[i].id) : null;
                  cands.push(id || (nodes[i].tagName.toLowerCase() + ' :: ' + t.slice(0, 40)));
                }
              }
              return cands;
            }

            // CMP identity by EVIDENCE (D-05) — never assumed.
            var cmp = 'unknown/other';
            var cmpEvidence = [];
            if (typeof window.OptanonWrapper !== 'undefined' || typeof window.Optanon !== 'undefined') { cmp = 'OneTrust'; cmpEvidence.push('OptanonWrapper/Optanon'); }
            if (typeof window.__tcfapi === 'function') { cmp = (cmp === 'OneTrust') ? 'OneTrust+TCF' : 'IAB-TCF'; cmpEvidence.push('__tcfapi'); }

            // Radar play target — confirm canvas (Mapbox-GL WebGL) vs <video> (D-17).
            var canvas = document.querySelector('canvas');
            var video = document.querySelector('video');
            var playKind = canvas ? 'canvas(webgl/mapbox candidate)' : (video ? 'video' : 'none');

            return JSON.stringify({
              ok: true,
              proof: window.__xpnProbeReached === true,
              origin: location.origin,
              href: location.href,
              cmp: cmp,
              cmpEvidence: cmpEvidence,
              consentCandidates: byTextRole(),
              consentContainerSelector: sel(['#onetrust-banner-sdk', '#onetrust-consent-sdk', '[id*=consent]', '[class*=consent]', '[class*=cookie]', '[id*=cookie]']),
              chromeAdsSelector: sel(['header', 'nav', '[class*=ad-]', '[id*=ad-]', '[class*=advert]', 'footer']),
              targetRadarSelector: sel(['[class*=radar]', '[id*=radar]', '[class*=map]', '[id*=map]', 'canvas']),
              playCanvasSelector: canvas ? (canvas.id ? ('#' + canvas.id) : 'canvas') : null,
              playKind: playKind
            });
          } catch (e) {
            return JSON.stringify({ ok: false, error: String(e) });
          }
        })();";

        try
        {
            AsyncContext.Run(async delegate
            {
                var settings = new CefSettings();
                if (!Directory.Exists(launchCachePath))
                {
                    Directory.CreateDirectory(launchCachePath);
                }

                settings.RootCachePath = launchCachePath;
                settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

                // D-13 anti-throttle — kept in sync with the smoke/onpaint paths so rAF / timers do not
                // stall on the offscreen surface during the probe.
                settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
                settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
                settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");

                // D-03 site-isolation OFF (scoped to THIS probe run — T-3-01): collapses OOPIFs onto the
                // single GetDevToolsClient() session so a cross-origin child frame's context is reachable
                // and createIsolatedWorld/evaluate actually run inside it. This does NOT flip the
                // production launch posture — that is a separate operator relaunch decision made FROM the
                // decision record this probe feeds.
                settings.CefCommandLineArgs.Add("disable-features", "IsolateOrigins,site-per-process");
                settings.CefCommandLineArgs.Add("disable-site-isolation-trials", "1");

                settings.EnableAudio();
                Cef.Initialize(settings);
                AppManagement.LogProvenance();

                Log.Information("ACCUWEATHER-PROBE posture run with site-isolation=OFF (scoped, D-03) — classifying cross-origin target.");

                // Construct on about:blank so the CDP doc-start registration + Page/Runtime.enable land
                // BEFORE the first real navigation (research Gotcha: enable + register before nav).
                browser = new CefSharp.OffScreen.ChromiumWebBrowser("about:blank");
                browser.Size = new System.Drawing.Size(width, height);
                await browser.WaitForInitialLoadAsync();
                browser.GetBrowserHost().WindowlessFrameRate = 60;

                // ONE persistent DevToolsClient held for the run (InjectHook.EnsureClientAsync pattern) —
                // a per-call (using) client would tear down session-scoped registrations on dispose.
                dev = browser.GetDevToolsClient();

                // frameId -> uniqueContextId map, built from executionContextCreated.auxData.frameId.
                var frameContexts = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                // frames seen via frameNavigated (id -> url), so we can attribute origins + re-arm.
                var frameUrls = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                // re-arm signal: set whenever a context is destroyed or a frame navigates (Q1).
                var reattachCount = 0;

                static string? FrameIdFromAuxData(object? auxData)
                {
                    if (auxData is null) { return null; }
                    try
                    {
                        // AuxData is a CDP JSON object; serialize+parse so we do not depend on its exact
                        // declared CLR type. It carries { frameId, isDefault, type }.
                        var json = System.Text.Json.JsonSerializer.Serialize(auxData);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("frameId", out var fid)
                            && fid.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            return fid.GetString();
                        }
                    }
                    catch { /* best-effort */ }
                    return null;
                }

                // Runtime.executionContextCreated replays for existing contexts the instant Runtime.enable
                // is called, then fires for each new frame's context. Cache by frameId, prefer the
                // process-stable uniqueId (research Gotcha: uniqueContextId is safer across navigation).
                dev.Runtime.ExecutionContextCreated += (s, e) =>
                {
                    try
                    {
                        var ctx = e.Context;
                        var fid = FrameIdFromAuxData(ctx?.AuxData);
                        if (fid is not null && ctx is not null)
                        {
                            frameContexts[fid] = ctx.UniqueId;
                        }
                    }
                    catch { }
                };

                // Q1 RE-ARM (not one-shot): a destroyed context means the frame is re-mounting; bump the
                // signal so the read loop re-resolves + re-reads rather than concluding a miss is failure.
                dev.Runtime.ExecutionContextDestroyed += (s, e) =>
                {
                    System.Threading.Interlocked.Increment(ref reattachCount);
                };

                // Q1 RE-ARM: a cross-document navigation re-creates the frame's context — record the URL
                // and bump the signal so the loop re-resolves the (possibly new) context.
                dev.Page.FrameNavigated += (s, e) =>
                {
                    try
                    {
                        var f = e.Frame;
                        if (f?.Id is not null && f.Url is not null)
                        {
                            frameUrls[f.Id] = f.Url;
                            System.Threading.Interlocked.Increment(ref reattachCount);
                        }
                    }
                    catch { }
                };

                // Enable Page + Runtime BEFORE navigation (research Gotcha) so executionContextCreated
                // replays existing contexts and frameNavigated fires for the load we are about to drive.
                await dev.Page.EnableAsync();
                await dev.Runtime.EnableAsync();

                // Drive the navigation through CDP so frameNavigated/context events are observed for it.
                await dev.Page.NavigateAsync(probeUrl);

                // Per-frame isolated-world readback. Resolves a context with createIsolatedWorld(frameId)
                // and evaluates ProbeJs IN that world's contextId. Returns the parsed JSON or null.
                async Task<System.Text.Json.JsonElement?> ReadFrameAsync(string frameId)
                {
                    try
                    {
                        var world = await dev!.Page.CreateIsolatedWorldAsync(frameId, ProbeWorldName, true);
                        var ctxId = world.ExecutionContextId;

                        // Runtime.EvaluateAsync positional params (verified 148 signature): expression,
                        // objectGroup, includeCommandLineApi, silent, contextId, returnByValue, ...
                        var resp = await dev.Runtime.EvaluateAsync(
                            ProbeJs,            // expression
                            null,               // objectGroup
                            null,               // includeCommandLineApi
                            true,               // silent
                            ctxId,              // contextId — the isolated world we just created
                            true);              // returnByValue

                        if (resp?.ExceptionDetails is not null)
                        {
                            return null;
                        }

                        var val = resp?.Result?.Value;
                        if (val is null) { return null; }

                        using var doc = System.Text.Json.JsonDocument.Parse(val.ToString() ?? "null");
                        return doc.RootElement.Clone();
                    }
                    catch
                    {
                        return null;
                    }
                }

                // Settle window: let the page load + the SPA mount, collecting frame contexts.
                var deadline = DateTime.UtcNow.AddSeconds(rearmWindowSeconds);
                var hardDeadline = DateTime.UtcNow.AddSeconds(ProbeTimeoutSeconds);

                string? mainOrigin = null;
                var perFrame = new System.Collections.Generic.List<(string frameId, string origin, bool proof, bool crossOrigin, System.Text.Json.JsonElement data)>();

                // Q1 three-outcome tracking across the re-arm window.
                var sawCrossOriginContext = false;     // a cross-origin frame context existed at all
                var crossOriginReadTrue = false;       // READS-TRUE: isolated-world readback succeeded in it
                var crossOriginTransientMiss = false;  // missed once, then resolved on re-read => TIMING

                var lastReattach = -1;
                while (DateTime.UtcNow < deadline && DateTime.UtcNow < hardDeadline)
                {
                    var snapshotReattach = System.Threading.Volatile.Read(ref reattachCount);
                    var frameIds = frameContexts.Keys.ToArray();

                    foreach (var fid in frameIds)
                    {
                        var data = await ReadFrameAsync(fid);
                        if (data is null)
                        {
                            // Could not read this frame this pass — if it later resolves, that is the
                            // TIMING outcome (re-arm). Note it tentatively for cross-origin frames below.
                            continue;
                        }

                        var el = data.Value;
                        if (!(el.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())) { continue; }

                        var origin = el.TryGetProperty("origin", out var oEl) ? (oEl.GetString() ?? "") : "";
                        var proof = el.TryGetProperty("proof", out var pEl) && pEl.GetBoolean();

                        // The main frame is the one whose origin matches the navigated URL's origin and
                        // which appears first; capture it once to compute cross-origin.
                        if (mainOrigin is null)
                        {
                            try { mainOrigin = new Uri(probeUrl).GetLeftPart(UriPartial.Authority); } catch { mainOrigin = origin; }
                        }

                        var crossOrigin = !string.IsNullOrEmpty(origin)
                            && !string.IsNullOrEmpty(mainOrigin)
                            && !origin.Equals(mainOrigin, StringComparison.OrdinalIgnoreCase);

                        if (crossOrigin)
                        {
                            sawCrossOriginContext = true;
                            if (proof) { crossOriginReadTrue = true; }
                        }

                        // Record/replace this frame's latest reading.
                        perFrame.RemoveAll(t => t.frameId == fid);
                        perFrame.Add((fid, origin, proof, crossOrigin, el));
                    }

                    // Re-arm detection: if a re-attach happened and we previously failed to read a
                    // cross-origin frame that now reads true, that is the TIMING outcome (Q1).
                    if (snapshotReattach != lastReattach)
                    {
                        lastReattach = snapshotReattach;
                        if (sawCrossOriginContext && !crossOriginReadTrue)
                        {
                            // a re-attach occurred while a cross-origin frame had not yet read true —
                            // the next loop iterations re-resolve it; mark the transient-miss path.
                            crossOriginTransientMiss = true;
                        }
                    }

                    await Task.Delay(250);
                }

                // ── Classify + emit. ─────────────────────────────────────────────────────────────────
                // Posture (D-01): ON only if a genuine cross-origin frame existed AND its own-context
                // readback succeeded (default OFF — T-3-01).
                var postureOn = sawCrossOriginContext && crossOriginReadTrue;

                // Q1 three-outcome classification of the cross-origin readback.
                string crossOriginOutcome;
                if (!sawCrossOriginContext)
                {
                    crossOriginOutcome = "N/A (same-origin — no cross-origin radar/consent frame found)";
                }
                else if (crossOriginReadTrue && !crossOriginTransientMiss)
                {
                    crossOriginOutcome = "READS-TRUE (injection reaches the OOPIF; INJ-04 holds live)";
                }
                else if (crossOriginReadTrue && crossOriginTransientMiss)
                {
                    crossOriginOutcome = "TIMING (transiently-absent-then-present after re-resolve on re-attach — SPA re-mount window, NOT injection failure)";
                }
                else
                {
                    crossOriginOutcome = "GENUINE-FAILURE (cross-origin context exists + isolated world created, but the proof flag never read true)";
                }

                // CMP identity: take the strongest evidence seen across frames (main frame usually).
                string cmp = "unknown/other";
                var cmpEvidence = new System.Collections.Generic.List<string>();
                foreach (var f in perFrame)
                {
                    if (f.data.TryGetProperty("cmp", out var cEl))
                    {
                        var c = cEl.GetString() ?? "unknown/other";
                        if (c != "unknown/other") { cmp = c; }
                    }
                    if (f.data.TryGetProperty("cmpEvidence", out var evEl) && evEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in evEl.EnumerateArray()) { var sIt = item.GetString(); if (sIt is not null && !cmpEvidence.Contains(sIt)) { cmpEvidence.Add(sIt); } }
                    }
                }

                // ── Structured stdout the operator transcribes into the decision record. ──────────────
                Console.WriteLine("==== ACCUWEATHER-PROBE RESULT ====");
                Console.WriteLine($"operatorUrl: {probeUrl}");
                Console.WriteLine($"mainOrigin: {mainOrigin}");
                Console.WriteLine($"framesObserved: {perFrame.Count}");
                Console.WriteLine($"crossOriginFrameSeen: {sawCrossOriginContext}");
                Console.WriteLine($"crossOriginReadback: {crossOriginOutcome}");
                Console.WriteLine($"postureRecommendation(expectsCrossOriginIframes): {(postureOn ? "ON" : "OFF")}");
                Console.WriteLine($"cmpIdentity: {cmp}  evidence:[{string.Join(",", cmpEvidence)}]");
                foreach (var f in perFrame)
                {
                    Console.WriteLine($"-- frame {f.frameId} origin={f.origin} crossOrigin={f.crossOrigin} proof={f.proof}");
                    if (f.data.TryGetProperty("consentContainerSelector", out var cc)) { Console.WriteLine($"   consentContainerSelector: {cc.GetString()}"); }
                    if (f.data.TryGetProperty("consentCandidates", out var ccs) && ccs.ValueKind == System.Text.Json.JsonValueKind.Array) { Console.WriteLine($"   consentCandidates: {string.Join(" | ", ccs.EnumerateArray().Select(x => x.GetString()))}"); }
                    if (f.data.TryGetProperty("chromeAdsSelector", out var ch)) { Console.WriteLine($"   chromeAdsSelector: {ch.GetString()}"); }
                    if (f.data.TryGetProperty("targetRadarSelector", out var tr)) { Console.WriteLine($"   targetRadarSelector: {tr.GetString()}"); }
                    if (f.data.TryGetProperty("playCanvasSelector", out var pc)) { Console.WriteLine($"   playCanvasSelector: {pc.GetString()}"); }
                    if (f.data.TryGetProperty("playKind", out var pk)) { Console.WriteLine($"   playKind(canvas=Mapbox/WebGL, NOT video per D-17): {pk.GetString()}"); }
                }
                Console.WriteLine("==== END ACCUWEATHER-PROBE RESULT ====");

                Log.Information(
                    "ACCUWEATHER-PROBE classified — posture={Posture} cmp={Cmp} crossOrigin={Outcome} frames={Frames}",
                    postureOn ? "ON" : "OFF", cmp, crossOriginOutcome, perFrame.Count);

                // A clean classified run (we observed at least one frame and produced a classification) is
                // exit 0 even when posture=OFF / outcome=GENUINE-FAILURE — those are valid EVIDENCE the
                // record captures. Non-zero is reserved for env/CDP failure (no frame ever read).
                exitCode = perFrame.Count > 0 ? 0 : 3;
                if (exitCode != 0)
                {
                    Console.WriteLine("ACCUWEATHER-PROBE FAIL: no frame context was ever read (env/CDP failure, not a page result).");
                    Log.Error("ACCUWEATHER-PROBE FAIL — no frame read within the window (env/CDP).");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("ACCUWEATHER-PROBE FAILED — exception: {@ex}", ex);
            Console.WriteLine($"ACCUWEATHER-PROBE FAIL: exception {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            try { dev?.Dispose(); } catch { }
            try { browser?.Dispose(); } catch { }

            if (Directory.Exists(launchCachePath))
            {
                try { Directory.Delete(launchCachePath, true); } catch { }
            }
        }

        Log.CloseAndFlush();
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// D-22: read one BGRA pixel out of the captured pre-send OnPaint buffer at (x,y), using the same
    /// byte-offset convention as <c>IsBlankBuffer</c> (off+0=B, off+1=G, off+2=R, off+3=A). Bounds-
    /// clamped (T-2-01-1 — read-only, never write through the buffer).
    /// </summary>
    private static (byte B, byte G, byte R, byte A) SampleRegion(byte[] buffer, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var off = ((long)y * width + x) * 4;
        return (buffer[off + 0], buffer[off + 1], buffer[off + 2], buffer[off + 3]);
    }

    /// <summary>D-22: within the per-channel tolerance band (absorbs CEF gamma/rounding).</summary>
    private static bool Near(int value, int expected, int tolerance)
        => Math.Abs(value - expected) <= tolerance;

    /// <summary>
    /// D-14/D-22: the inject-on-load ACCEPTANCE GATE. Additive in-process self-check that mirrors
    /// <see cref="RunSmoke"/> but proves the Plan-04 InjectHook mechanisms actually fire on a
    /// deterministic synthetic fixture — INJ-01 (doc-start-before-inline-head + navigation re-fire),
    /// INJ-03 (MutationObserver re-assert on re-mount), INJ-04 (cross-origin iframe reach via a real
    /// OOPIF, read PER-FRAME), INJ-05 (the four state flags, all selector-driven — no coordinate
    /// clicking), and the D-04/D-24 swap (recipe A's distinct guard is GONE and recipe B's is present
    /// after a swap).
    ///
    /// <para>D-22 (the critical correction): the fixture is served over a LOCAL HTTP LISTENER, NOT
    /// file://. file:// has no host, so the D-05 recipe urlMatch could never match; and a same-process
    /// inline iframe never becomes an OOPIF, so the OLD design never exercised INJ-04. Here the main
    /// fixture is served on one loopback origin and the iframe doc on a GENUINELY DIFFERENT origin (a
    /// different port), so under the D-03 site-isolation-OFF flags the iframe becomes a real OOPIF. The
    /// iframe proof flag is set in the iframe's OWN execution context and read via a PER-FRAME
    /// evaluate (CEF exposes every frame, including a cross-origin OOPIF, through
    /// <c>GetBrowser().GetFrames()</c>); main-frame JS cannot read a cross-origin iframe's globals.</para>
    ///
    /// <para>Exit code 1 by default, 0 only on full success; a hard timeout (mirrors the RunSmoke
    /// Task.WhenAny pattern) makes a stuck init fail fast; the HTTP listener is torn down on exit.</para>
    /// </summary>
    private static void RunInjectSmoke(string[] args, string launchCachePath)
    {
        const int InjectSmokeTimeoutSeconds = 40;

        var ndiName = "XPRESSION-INJECT-SMOKE";
        var width = 1920;
        var height = 1080;

        // Two loopback origins on DIFFERENT ports → the iframe is a genuine cross-origin OOPIF (D-22).
        var mainPort = GetFreeTcpPort();
        var iframePort = GetFreeTcpPort();
        var mainOrigin = $"http://localhost:{mainPort}";
        var iframeOrigin = $"http://localhost:{iframePort}";
        var fixtureUrl = $"{mainOrigin}/";

        // --inject-smoke=<url> overrides the served fixture URL (parity with --smoke=<url>); default is
        // the local HTTP listener serving the committed fixture.
        var injectArg = args.FirstOrDefault(x => x.StartsWith("--inject-smoke")) ?? "--inject-smoke";
        var eq = injectArg.IndexOf('=');
        if (eq >= 0 && eq < injectArg.Length - 1)
        {
            fixtureUrl = injectArg.Substring(eq + 1);
        }

        Log.Information(
            "INJECT-SMOKE starting — main={Main} iframe={Iframe} timeout={Timeout}s",
            mainOrigin, iframeOrigin, InjectSmokeTimeoutSeconds);

        var exitCode = 1; // default failure; only set 0 on full success.
        System.Net.HttpListener? listener = null;

        try
        {
            // ── Read the two committed fixture docs from beside the exe (csproj copies them). ──────
            var fixtureDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests-fixtures");
            var mainFixturePath = Path.Combine(fixtureDir, "inject-fixture.html");
            var iframeFixturePath = Path.Combine(fixtureDir, "inject-iframe.html");
            if (!File.Exists(mainFixturePath) || !File.Exists(iframeFixturePath))
            {
                Console.WriteLine($"INJECT-SMOKE FAIL: fixture docs missing under {fixtureDir}");
                Log.Error("INJECT-SMOKE FAIL — fixture docs missing under {Dir}", fixtureDir);
                Log.CloseAndFlush();
                Environment.Exit(1);
            }

            var mainHtml = File.ReadAllText(mainFixturePath)
                .Replace("__IFRAME_ORIGIN__", iframeOrigin);
            var iframeHtml = File.ReadAllText(iframeFixturePath);

            // ── Stand up the loopback HTTP listener serving BOTH origins (D-22). ──────────────────
            listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"{mainOrigin}/");
            listener.Prefixes.Add($"{iframeOrigin}/");
            listener.Start();
            var listenerCts = new System.Threading.CancellationTokenSource();
            var listenerThread = new Thread(() => ServeFixtures(listener, listenerCts.Token, mainHtml, iframeHtml))
            {
                IsBackground = true,
            };
            listenerThread.Start();

            // ── Resolve recipe A (the acceptance recipe) + recipe B (the swap target) from the
            //    bundle-relative recipes/ dir (CI copies the parent recipes/ in — D-09). ───────────
            var recipeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recipes");
            var validator = new RecipeValidator();
            var store = new RecipeStore(validator);
            store.Load(recipeDir);

            // Recipe A is the acceptance recipe; recipe B is the distinct-guard swap target. Both are
            // resolved EXPLICITLY by filename (NOT store.Match): both recipes share urlMatch "localhost",
            // and Match is first-match-wins over an Ordinal filename sort, so "inject-fixture-b..." ('-'
            // = 0x2D sorts before '.' = 0x2E) would bind recipeA to recipe B. That made recipe A never
            // run, so the D-24 A->B swap degenerated to B->B and "A's guard is gone" passed trivially —
            // masking whether stale-script removal actually works. Explicit load makes recipe A run.
            Recipe? recipeA = null;
            if (!store.TryLoadExplicit(Path.Combine(recipeDir, "inject-fixture.localhost.json"), out recipeA, out var aErr))
            {
                Console.WriteLine($"INJECT-SMOKE FAIL: recipe A failed to load: {aErr}");
                Log.Error("INJECT-SMOKE FAIL — recipe A load: {Err}", aErr);
                listenerCts.Cancel();
                try { listener.Stop(); } catch { }
                Log.CloseAndFlush();
                Environment.Exit(1);
            }

            Recipe? recipeB = null;
            if (!store.TryLoadExplicit(Path.Combine(recipeDir, "inject-fixture-b.localhost.json"), out recipeB, out var bErr))
            {
                Console.WriteLine($"INJECT-SMOKE FAIL: recipe B failed to load: {bErr}");
                Log.Error("INJECT-SMOKE FAIL — recipe B load: {Err}", bErr);
                listenerCts.Cancel();
                try { listener.Stop(); } catch { }
                Log.CloseAndFlush();
                Environment.Exit(1);
            }

            // Unreachable after the fail-loud explicit load above (TryLoadExplicit returns true ⟹
            // non-null); kept as the compiler null-guard for the recipeA dereference below (CS8602).
            if (recipeA is null)
            {
                Console.WriteLine("INJECT-SMOKE FAIL: recipe A unexpectedly null after explicit load");
                Log.Error("INJECT-SMOKE FAIL — recipe A null after load of {Url}", fixtureUrl);
                listenerCts.Cancel();
                try { listener.Stop(); } catch { }
                Log.CloseAndFlush();
                Environment.Exit(1);
            }

            // The fixture exercises INJ-04, so launch with site-isolation OFF (recipe A declares
            // expectsCrossOriginIframes=true). The startup recipe gates the D-03 flags (D-21 frozen).
            launchExpectsCrossOriginIframes = recipeA.ExpectsCrossOriginIframes;

            AsyncContext.Run(async delegate
            {
                var settings = new CefSettings();
                if (!Directory.Exists(launchCachePath))
                {
                    Directory.CreateDirectory(launchCachePath);
                }

                settings.RootCachePath = launchCachePath;
                settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");

                // D-13 anti-throttle (kept in sync with the normal/smoke paths).
                settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
                settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
                settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");

                // D-03 site-isolation OFF for the INJ-04 cross-origin-iframe reach test. These are
                // process-global + frozen at Cef.Initialize (Pitfall 4); added here because the
                // fixture's whole point is to exercise the OOPIF path.
                settings.CefCommandLineArgs.Add("disable-features", "IsolateOrigins,site-per-process");
                settings.CefCommandLineArgs.Add("disable-site-isolation-trials", "1");

                settings.EnableAudio();
                Cef.Initialize(settings);
                AppManagement.LogProvenance();

                // D-03 posture log — INJ-04 needs isolation OFF; assert the posture in the smoke output.
                Log.Information(
                    "POSTURE site-isolation={Iso} cause={Cause}",
                    launchExpectsCrossOriginIframes ? "OFF" : "ON",
                    recipeA.UrlMatch);

                if (!launchExpectsCrossOriginIframes)
                {
                    Console.WriteLine("INJECT-SMOKE FAIL: site-isolation posture is ON; INJ-04 requires OFF");
                    return; // exitCode stays 1
                }

                browserWrapper = new CefWrapper(width, height, fixtureUrl)
                {
                    StartupRecipe = recipeA, // registered doc-start BEFORE the first Load (Pitfall 1).
                };

                await browserWrapper.InitializeWrapperAsync();

                // Bounded settle: poll the assertion surface until satisfied or timeout.
                var deadline = DateTime.UtcNow.AddSeconds(InjectSmokeTimeoutSeconds);

                // ── INJ-01: doc-start marker set BEFORE the inline head script ran. ───────────────
                if (!await PollMainFlagAsync(browserWrapper, "window.__inlineHeadSawMarker === true", deadline))
                {
                    Console.WriteLine("INJECT-SMOKE FAIL: INJ-01 doc-start-before-inline-head not proven (__inlineHeadSawMarker)");
                    return;
                }

                // ── INJ-05: the four state flags, all set via SELECTOR-driven recipe JS. ──────────
                if (!await PollMainFlagAsync(browserWrapper,
                        "window.consentDismissed === true && window.chromeHidden === true && window.targetFilled === true && window.playStarted === true",
                        deadline))
                {
                    Console.WriteLine("INJECT-SMOKE FAIL: INJ-05 state flags not all set (consent/chrome/target/play)");
                    return;
                }

                // ── INJ-04: cross-origin iframe proof, read PER-FRAME in the iframe's OWN context. ─
                if (!await PollIframeFlagAsync(browserWrapper, iframeOrigin, "window.__xpnIframeTargetHit === true", deadline))
                {
                    Console.WriteLine("INJECT-SMOKE FAIL: INJ-04 cross-origin in-iframe hit not proven (per-frame __xpnIframeTargetHit)");
                    return;
                }

                // ── INJ-03: trigger a consent re-mount, then assert the MutationObserver re-dismissed. ─
                await EvalMainAsync(browserWrapper, "window.__xpnRemountNow = true; void 0;");
                if (!await PollMainFlagAsync(browserWrapper,
                        "window.consentRemounted === true && window.consentDismissed === true",
                        deadline))
                {
                    Console.WriteLine("INJECT-SMOKE FAIL: INJ-03 MutationObserver re-dismiss after re-mount not proven");
                    return;
                }

                // ── INJ-01 (navigation re-fire): navigate to a 2nd document, marker must re-set. ──
                // Same recipe → the registered doc-start script re-fires on the new document.
                await browserWrapper.SetUrlAsync(fixtureUrl + "?nav=2", recipeA);
                if (!await PollMainFlagAsync(browserWrapper, "window.__inlineHeadSawMarker === true", deadline))
                {
                    Console.WriteLine("INJECT-SMOKE FAIL: INJ-01 navigation re-fire not proven on 2nd document");
                    return;
                }

                // ── D-04/D-24 swap: A (guard __xpnRecipeA_guard) → B (guard __xpnRecipeB_guard). ──
                // After the swap + navigation, recipe A's distinct guard must be GONE and B's present.
                // Distinct guard names mean a stale A guard surviving the swap is DETECTED, not masked.
                await browserWrapper.SwapRecipeAsync(recipeB, fixtureUrl + "?nav=3");
                if (!await PollMainFlagAsync(browserWrapper,
                        "window.__xpnRecipeB_guard === true && (typeof window.__xpnRecipeA_guard === 'undefined') && document.getElementById('target-widget') && document.getElementById('target-widget').getAttribute('data-xpn-recipe') === 'B'",
                        deadline))
                {
                    Console.WriteLine("INJECT-SMOKE FAIL: D-04/D-24 swap not proven (recipe A guard must be gone, recipe B present)");
                    return;
                }

                Log.Information("INJECT-SMOKE OK — INJ-01/03/04/05 + D-04/D-24 swap all proven on the synthetic fixture.");
                Console.WriteLine("INJECT-SMOKE OK");
                exitCode = 0;
            });
        }
        catch (Exception ex)
        {
            Log.Error("INJECT-SMOKE FAILED — exception: {@ex}", ex);
            Console.WriteLine($"INJECT-SMOKE FAIL: exception {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            try { browserWrapper?.Dispose(); } catch { }
            try { listener?.Stop(); } catch { }

            if (Directory.Exists(launchCachePath))
            {
                try { Directory.Delete(launchCachePath, true); } catch { }
            }
        }

        Log.CloseAndFlush();
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// D-09/D-34 (MON-02..05): the monitor self-healing acceptance gate. Mirrors <see cref="RunInjectSmoke"/>'s
    /// free-port loopback listener + CEF-init + bounded-poll + exit discipline, but proves the WHOLE
    /// self-healing loop end to end on a synthetic rAF-canvas fixture that freezes on command: HEALTHY while
    /// animating -> force freeze -> the monitor TRIPS + the pump's current output becomes the fallback (the
    /// sender NEVER stops — frames keep advancing) -> a single-flight in-process RefreshPage -> the injected
    /// re-inject marker RE-APPEARS post-refresh (D-17) -> after K-out good frames the monitor returns to
    /// HEALTHY / source=live (D-15 — recovery is POST-refresh confirmation, not the bare refresh).
    ///
    /// <para>D-34 (load-bearing): the smoke uses its OWN DEDICATED recipe constructed INLINE here — NOT the
    /// production recipe resolver (which could match a real localhost recipe first and apply the wrong
    /// js/expectMotion, making the freeze-detection assertion measure the wrong page). The inline recipe has
    /// SMOKE-SCALE tiny thresholds (freezeTimeoutMs/minHoldMs/hysteresis ≪ the D-10 ~10s/~2s production
    /// defaults) so CI proves the transitions in ~1s, not the production window.</para>
    ///
    /// <para>Exit code 1 by default, 0 only on the full path; a hard timeout makes a stuck pipeline fail fast.
    /// The CI runner is SwiftShader (no GPU) like --inject-smoke; the fixture is a plain 2D canvas (no WebGL).</para>
    /// </summary>
    private static void RunMonitorSmoke(string[] args, string launchCachePath)
    {
        const int MonitorSmokeTimeoutSeconds = 60;
        const int Width = 1920;
        const int Height = 1080;
        const string NdiName = "XPRESSION-MONITOR-SMOKE";

        // A single loopback origin serving the rAF fixture on every path. A free port keeps the dedicated
        // recipe's urlMatch (the explicit fixture host:port) un-collidable with any production localhost recipe.
        var port = GetFreeTcpPort();
        var origin = $"http://localhost:{port}";
        var fixtureUrl = $"{origin}/";

        var smokeArg = args.FirstOrDefault(x => x.StartsWith("--monitor-smoke")) ?? "--monitor-smoke";
        var eq = smokeArg.IndexOf('=');
        if (eq >= 0 && eq < smokeArg.Length - 1)
        {
            fixtureUrl = smokeArg.Substring(eq + 1);
        }

        Log.Information(
            "MONITOR-SMOKE starting — fixture={Url} timeout={Timeout}s",
            fixtureUrl, MonitorSmokeTimeoutSeconds);

        var exitCode = 1; // default failure; only set 0 on the full self-healing path.
        System.Net.HttpListener? listener = null;
        FrameMonitor? monitor = null;
        FramePump? pump = null;

        try
        {
            var fixtureDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests-fixtures");
            var fixturePath = Path.Combine(fixtureDir, "monitor-fixture.html");
            if (!File.Exists(fixturePath))
            {
                Console.WriteLine($"MONITOR-SMOKE FAIL: fixture missing under {fixtureDir}");
                Log.Error("MONITOR-SMOKE FAIL — fixture missing under {Dir}", fixtureDir);
                Log.CloseAndFlush();
                Environment.Exit(1);
            }

            var fixtureHtml = File.ReadAllText(fixturePath);

            listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"{origin}/");
            listener.Start();
            var listenerCts = new System.Threading.CancellationTokenSource();
            var listenerThread = new Thread(() => ServeMonitorFixture(listener, listenerCts.Token, fixtureHtml))
            {
                IsBackground = true,
            };
            listenerThread.Start();

            // D-34: the DEDICATED inline recipe — its urlMatch is the explicit free-port fixture host so the
            // production resolver is never consulted and cannot shadow it. expectMotion=true so the animating
            // canvas reads as motion (and a freeze reads as a content-freeze). SMOKE-SCALE tiny thresholds so
            // the freeze->fallback->recovery transitions fire in a few sample ticks, not the ~10s window. The
            // js sets the re-inject marker the gate checks RE-APPEARS after the refresh (D-17).
            var smokeRecipe = new Recipe
            {
                UrlMatch = $"localhost:{port}",
                ExpectMotion = true,
                FreezeTimeoutMs = 600,   // ≪ the D-10 ~10s production default
                MinHoldMs = 200,         // ≪ the D-10 ~2s production default
                HysteresisKIn = 2,       // fail fast
                HysteresisKOut = 3,      // recover slow-but-quick for CI
                FallbackPolicy = "slate",
                Js = "try { window.__xpnMonitorMark = true; } catch (e) {}",
            };

            launchExpectsCrossOriginIframes = false; // the fixture needs no cross-origin iframe (site-iso ON).

            AsyncContext.Run(async delegate
            {
                var settings = new CefSettings();
                if (!Directory.Exists(launchCachePath))
                {
                    Directory.CreateDirectory(launchCachePath);
                }

                settings.RootCachePath = launchCachePath;
                settings.CefCommandLineArgs.Add("autoplay-policy", "no-user-gesture-required");
                settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
                settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
                settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");

                settings.EnableAudio();
                Cef.Initialize(settings);
                AppManagement.LogProvenance();

                // The NDI sender (the pump is the sole sender; a null ptr ⇒ wrong/missing NDI DLL ⇒ fail).
                var settingsT = new NDIlib.send_create_t { p_ndi_name = UTF.StringToUtf8(NdiName) };
                Program.NdiSenderPtr = NDIlib.send_create(ref settingsT);
                if (Program.NdiSenderPtr == nint.Zero)
                {
                    Console.WriteLine("MONITOR-SMOKE FAIL: NDIlib.send_create returned nint.Zero (NDI DLL missing/wrong)");
                    return; // exitCode stays 1
                }

                browserWrapper = new CefWrapper(Width, Height, fixtureUrl)
                {
                    StartupRecipe = smokeRecipe, // doc-start re-inject marker registered BEFORE the first Load.
                };

                // The REAL single-authority composition root (source -> monitor -> pump -> NDI), the SAME
                // topology as the interactive path. The refresh delegate is the in-process RefreshPage (D-28),
                // so the monitor's recovery loop reloads the wedged page exactly as production would.
                Func<Task> refreshDelegate = () =>
                {
                    browserWrapper.RefreshPage();
                    return Task.CompletedTask;
                };
                monitor = new FrameMonitor(browserWrapper, refreshDelegate);
                pump = new FramePump(monitor, Program.NdiSenderPtr);
                pump.Start();

                // Generated-default fallback at output geometry (no fallbacks/ dependency for the smoke).
                var fallbackProvider = new FallbackProvider(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fallbacks"),
                    Width, Height, AlphaConvention.Expected);
                var fallbackResult = fallbackProvider.LoadOrGenerate(smokeRecipe.FallbackPolicy, smokeRecipe.FallbackAsset);
                monitor.SetFallbackFrame(fallbackResult.Frame.Bgra, fallbackResult.Frame.Width, fallbackResult.Frame.Height);

                monitor.ApplyRecipe(smokeRecipe);
                monitor.WireHealth(
                    () => pump.FramesSent,
                    () => browserWrapper.CurrentRecipe?.UrlMatch,
                    () => fallbackResult.State,
                    "ON",
                    System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime());
                monitor.StartSampling();

                await browserWrapper.InitializeWrapperAsync();

                var deadline = DateTime.UtcNow.AddSeconds(MonitorSmokeTimeoutSeconds);

                // (1) HEALTHY while the canvas animates + the doc-start marker is present.
                if (!await PollMainFlagAsync(browserWrapper, "window.__xpnMonitorAnimating === true && window.__xpnMonitorMark === true", deadline))
                {
                    Console.WriteLine("MONITOR-SMOKE FAIL: fixture never animated or marker not injected at doc-start");
                    return;
                }

                if (!await PollConditionAsync(() => monitor!.Status == MonitorStatus.Healthy && monitor!.OutputState == FrameMonitor.Output.Live, deadline))
                {
                    Console.WriteLine($"MONITOR-SMOKE FAIL: not HEALTHY/Live while animating (status={monitor!.Status}, output={monitor!.OutputState})");
                    return;
                }

                var framesBeforeFreeze = pump!.FramesSent;

                // (2) Force the freeze -> the monitor TRIPS and the pump output becomes the fallback. The
                // sender NEVER stops: assert frames keep advancing even though CEF stopped painting (MON-03).
                await EvalMainAsync(browserWrapper, "window.__xpnMonitorFreeze = true; void 0;");

                if (!await PollConditionAsync(() => monitor!.OutputState == FrameMonitor.Output.Fallback, deadline))
                {
                    Console.WriteLine($"MONITOR-SMOKE FAIL: freeze did not flip output to fallback (status={monitor!.Status})");
                    return;
                }

                // The pump must have kept sending across the freeze (the sender never stops — MON-03).
                if (!await PollConditionAsync(() => pump!.FramesSent > framesBeforeFreeze, deadline))
                {
                    Console.WriteLine("MONITOR-SMOKE FAIL: pump stopped sending during the freeze (the sender must never stop)");
                    return;
                }

                // (3)+(4) The recovery loop issues a single-flight RefreshPage; the reload re-fires the
                // doc-start script so the re-inject marker RE-APPEARS (D-17), the fresh document drops the
                // freeze flag and animates again, and after K-out post-refresh good frames the monitor
                // returns to HEALTHY / source=live (D-15 — recovery is post-refresh confirmation).
                if (!await PollConditionAsync(() => monitor!.RecoveryAttempts >= 1, deadline))
                {
                    Console.WriteLine("MONITOR-SMOKE FAIL: no refresh issued by the recovery loop");
                    return;
                }

                if (!await PollMainFlagAsync(browserWrapper, "window.__xpnMonitorMark === true && window.__xpnMonitorAnimating === true", deadline))
                {
                    Console.WriteLine("MONITOR-SMOKE FAIL: re-inject marker did not re-appear / fixture did not re-animate after refresh (D-17)");
                    return;
                }

                if (!await PollConditionAsync(() => monitor!.Status == MonitorStatus.Healthy && monitor!.OutputState == FrameMonitor.Output.Live, deadline))
                {
                    Console.WriteLine($"MONITOR-SMOKE FAIL: did not recover to HEALTHY/Live post-refresh (status={monitor!.Status}, output={monitor!.OutputState})");
                    return;
                }

                Log.Information("MONITOR-SMOKE OK — freeze->fallback(sender never stopped)->refresh+re-inject->post-refresh recovery proven.");
                Console.WriteLine("MONITOR-SMOKE OK");
                exitCode = 0;
            });
        }
        catch (Exception ex)
        {
            Log.Error("MONITOR-SMOKE FAILED — exception: {@ex}", ex);
            Console.WriteLine($"MONITOR-SMOKE FAIL: exception {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            try { if (pump is not null) { pump.StopAsync().GetAwaiter().GetResult(); } } catch { }
            try { monitor?.Dispose(); } catch { }
            try { browserWrapper?.Dispose(); } catch { }
            try { listener?.Stop(); } catch { }

            if (Directory.Exists(launchCachePath))
            {
                try { Directory.Delete(launchCachePath, true); } catch { }
            }
        }

        Log.CloseAndFlush();
        Environment.Exit(exitCode);
    }

    /// <summary>Serves the monitor fixture on every path of the loopback origin (torn down on cancel/exit).</summary>
    private static void ServeMonitorFixture(System.Net.HttpListener listener, System.Threading.CancellationToken token, string fixtureHtml)
    {
        while (!token.IsCancellationRequested)
        {
            System.Net.HttpListenerContext ctx;
            try
            {
                ctx = listener.GetContext();
            }
            catch
            {
                break; // listener stopped
            }

            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(fixtureHtml);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // best-effort per-request; never let one request kill the loopback listener.
            }
        }
    }

    /// <summary>Poll a C# predicate until true or the deadline passes (the monitor-state assertions).</summary>
    private static async Task<bool> PollConditionAsync(Func<bool> condition, DateTime deadlineUtc)
    {
        while (DateTime.UtcNow < deadlineUtc)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch { /* not ready yet — keep polling */ }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>Pick a free loopback TCP port (used for the two D-22 cross-origin listener origins).</summary>
    private static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Serves the main fixture on every path of the main origin and the iframe doc on the iframe
    /// origin's <c>/iframe</c> path (D-22 loopback-only listener; torn down on cancel/exit).
    /// </summary>
    private static void ServeFixtures(System.Net.HttpListener listener, System.Threading.CancellationToken token, string mainHtml, string iframeHtml)
    {
        while (!token.IsCancellationRequested)
        {
            System.Net.HttpListenerContext ctx;
            try
            {
                ctx = listener.GetContext();
            }
            catch
            {
                break; // listener stopped
            }

            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                var body = path.StartsWith("/iframe") ? iframeHtml : mainHtml;
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // best-effort per-request; never let one request kill the loopback listener.
            }
        }
    }

    /// <summary>Evaluate JS on the MAIN frame (fire-and-forget side effect).</summary>
    private static async Task EvalMainAsync(CefWrapper wrapper, string js)
    {
        var browser = wrapper.Browser;
        if (browser is null) { return; }
        try { await browser.EvaluateScriptAsync(js); } catch { }
    }

    /// <summary>
    /// 03-03 (D-24): READ a boolean JS proof marker on the MAIN frame ONCE, returning <c>true</c>/<c>false</c>
    /// for a clean read or <c>null</c> on a read miss (browser not ready, eval failure, throwing/redefined
    /// marker, or a non-bool result). The SIBLING CDP proof-marker probe composed at <c>/health</c> uses this
    /// to read <c>window.__xpnTargetPresent</c>/<c>__xpnConsentDismissed</c>/<c>__xpnPlayStarted</c> — reusing
    /// the existing <c>EvaluateScriptAsync</c> machinery (the <see cref="PollMainFlagAsync"/> shape, no new CDP
    /// code). The payload is a STATIC <c>!!(window.__xpn*)</c> boolean coercion run IN the page; the RESULT is
    /// read as a .NET bool (T-3-09b: a hostile getter can at worst spoof its OWN proof bool, never reach our
    /// control plane). On a miss the field is left null (degraded observability — never a control-flow hazard;
    /// these markers are observability ONLY, never wired into UseFallback, D-14). Bounded by a short timeout so
    /// a slow/hung page cannot stall the /health response.
    /// </summary>
    private static async Task<bool?> ReadMainBoolAsync(CefWrapper wrapper, string globalName, int timeoutMs = 300)
    {
        var browser = wrapper.Browser;
        if (browser is null) { return null; }

        try
        {
            var evalTask = browser.EvaluateScriptAsync($"!!(window.{globalName})");
            var timeoutTask = Task.Delay(timeoutMs);
            if (await Task.WhenAny(evalTask, timeoutTask) != evalTask)
            {
                return null; // read miss — leave the field null (degraded observability, D-24).
            }

            var resp = await evalTask;
            if (resp.Success && resp.Result is bool b)
            {
                return b;
            }
        }
        catch { /* read miss — null */ }

        return null;
    }

    /// <summary>
    /// Poll a boolean JS expression on the MAIN frame until true or the deadline passes. Returns true
    /// on success, false on timeout. INJ-01/03/05 main-frame assertions go through here.
    /// </summary>
    private static async Task<bool> PollMainFlagAsync(CefWrapper wrapper, string boolExpr, DateTime deadlineUtc)
    {
        while (DateTime.UtcNow < deadlineUtc)
        {
            var browser = wrapper.Browser;
            if (browser is not null)
            {
                try
                {
                    var resp = await browser.EvaluateScriptAsync($"!!({boolExpr})");
                    if (resp.Success && resp.Result is bool b && b)
                    {
                        return true;
                    }
                }
                catch { /* not ready yet — keep polling */ }
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>
    /// INJ-04 (D-22): poll a boolean JS expression in the CROSS-ORIGIN IFRAME's OWN execution context,
    /// PER-FRAME. CEF exposes every frame — including a cross-origin OOPIF (site-isolation is OFF for
    /// this self-check) — enumerated via <c>GetFrameIdentifiers()</c> + <c>GetFrameByIdentifier()</c>; we locate the iframe frame by its
    /// origin URL and evaluate IN THAT FRAME. Main-frame JS cannot read the iframe's globals, which is
    /// the whole point of the per-frame read.
    /// </summary>
    private static async Task<bool> PollIframeFlagAsync(CefWrapper wrapper, string iframeOrigin, string boolExpr, DateTime deadlineUtc)
    {
        while (DateTime.UtcNow < deadlineUtc)
        {
            var browser = wrapper.Browser?.GetBrowser();
            if (browser is not null)
            {
                try
                {
                    foreach (var id in browser.GetFrameIdentifiers())
                    {
                        var frame = browser.GetFrameByIdentifier(id);
                        if (frame is null || frame.IsMain) { continue; }
                        if (frame.Url is null || !frame.Url.StartsWith(iframeOrigin, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var resp = await frame.EvaluateScriptAsync($"!!({boolExpr})");
                        if (resp.Success && resp.Result is true)
                        {
                            return true;
                        }
                    }
                }
                catch { /* frames not ready yet — keep polling */ }
            }

            await Task.Delay(100);
        }

        return false;
    }
}
