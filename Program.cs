
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
using Tractus.HtmlToNdi.Models;

namespace Tractus.HtmlToNdi;
public class Program
{
    public static nint NdiSenderPtr;
    public static CefWrapper browserWrapper;

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
            settings.EnableAudio();
            Cef.Initialize(settings);
            browserWrapper = new CefWrapper(
                width,
                height,
                startUrl);

            await browserWrapper.InitializeWrapperAsync();
        });

        // D-13: provenance stamp on normal startup (CEF is initialized so CefSharpVersion is valid).
        AppManagement.LogProvenance();

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


        app.MapPost("/seturl", (HttpContext httpContext, GoToUrlModel url) =>
        {
            browserWrapper.SetUrl(url.Url);
            return true;
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
}
