
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

        // D-01/D-26: construct the extracted NDI sink with the ctor-injected sender ptr and subscribe
        // it to the CefWrapper IFrameSource. OnBrowserPaint early-returns while NdiSenderPtr == Zero,
        // and the watchdog re-invalidates paint every second, so subscribing here (right after the
        // sender exists) catches every subsequent frame — identical timing to the upstream send.
        var ndiSink = new NdiFrameSink(Program.NdiSenderPtr);
        ndiSink.AttachTo(browserWrapper);

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

        app.Run();

        running = false;
        thread.Join();
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

                // D-01/D-26: construct the extracted NDI sink with the ctor-injected sender ptr and
                // subscribe it to the CefWrapper IFrameSource BEFORE InitializeWrapperAsync wires Paint.
                var ndiSink = new NdiFrameSink(Program.NdiSenderPtr);
                ndiSink.AttachTo(browserWrapper);

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
