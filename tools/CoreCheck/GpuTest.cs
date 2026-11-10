using System.Diagnostics;
using RetroShowroom.Core;

static class GpuTest
{
    public static async Task RunAsync(string ydd, string ytd, string outDir)
    {
        Console.WriteLine("== Einrichtungsprüfung ==");
        var settings = new AppSettings();
        var sw = Stopwatch.StartNew();
        var rep = await SetupChecker.RunAsync(settings);
        Console.WriteLine($"  Dauer {sw.Elapsed.TotalSeconds:0.0} s");
        Console.WriteLine($"  Blender: {rep.BlenderPath} | Version {rep.BlenderVersion} | ok {rep.BlenderOk} | Fehler {rep.BlenderError}");
        Console.WriteLine($"  Sollumz: key {rep.SollumzKey ?? "-"} | Version {rep.SollumzVersion ?? "-"} | ok {rep.SollumzOk}");
        Console.WriteLine($"  Skelette: m {rep.SkeletonMale ?? "-"} | f {rep.SkeletonFemale ?? "-"}");
        Console.WriteLine($"  GPU: {rep.GpuSummary ?? "keine"}");
        Console.WriteLine($"  Offen (männlich): {string.Join(", ", rep.BlockingIssues('m'))}");
        if (!rep.BlenderOk) return;

        var tex = TexturePreparer.Prepare(ytd);
        var script = Path.Combine(AppContext.BaseDirectory, "gpu_test_szene.py");
        foreach (var gpu in Environment.GetEnvironmentVariable("NUR_GPU") == "1" ? new[] { true } : new[] { true, false })
        {
            Console.WriteLine($"\n== Testrender {(gpu ? "Grafikkarte" : "nur CPU")} ==");
            var req = new RenderRequest
            {
                BlenderPath = rep.BlenderPath!,
                ScriptPath = script,
                PartPath = ydd,
                TexturePng = tex.PngPath,
                LogoPath = tex.PngPath,
                RenderTarget = Path.Combine(AppPaths.CacheDir, $"render_{Guid.NewGuid():N}.png"),
                OutputPath = OutputNaming.Next(outDir, gpu ? "GPU TEST" : "CPU TEST", Quality.Test),
                Title = "TEST",
                Gender = 'm',
                Kind = "oben",
                Quality = Quality.Test,
                UseGpu = gpu,
                DetailedLog = rep.SupportsDetailedLog,
            };
            var runner = new RenderRunner();
            int lastPct = -1;
            string? lastDevice = null, lastPhase = null;
            runner.Progress += p =>
            {
                if (p.Device != lastDevice) { lastDevice = p.Device; Console.WriteLine($"  Gerät: {p.Device}"); }
                if (p.Phase != lastPhase) { lastPhase = p.Phase; Console.WriteLine($"  Phase: {p.Phase}"); }
                if (p.TotalSamples > 0)
                {
                    int pct = p.Sample * 100 / p.TotalSamples;
                    if (pct / 25 != lastPct / 25) { lastPct = pct; Console.WriteLine($"  Sample {p.Sample}/{p.TotalSamples}, noch {p.Remaining}"); }
                }
            };
            runner.Output += l => { if (l.StartsWith("RS_GPU") || l.StartsWith("GERAET") || l.Contains("Error") || l.Contains("error")) Console.WriteLine("  > " + l); };
            runner.Notice += n => Console.WriteLine("  HINWEIS: " + n);

            using var cts = new CancellationTokenSource();
            var maxUtil = 0;
            var poll = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
                        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                        using var p = Process.Start(psi)!;
                        var s = (await p.StandardOutput.ReadToEndAsync()).Trim();
                        if (int.TryParse(s.Split('\n')[0], out var u)) maxUtil = Math.Max(maxUtil, u);
                    }
                    catch { }
                    await Task.Delay(500);
                }
            });

            var res = await runner.RunAsync(req, CancellationToken.None);
            cts.Cancel();
            await poll;
            Console.WriteLine($"  Ergebnis: ok {res.Success}, Dauer {res.Duration.TotalSeconds:0.0} s, Gerät {res.Device}, Rückfall {res.FellBackToCpu}, max. GPU-Last {maxUtil} %");
            if (!res.Success) Console.WriteLine($"  Fehler: {res.Error} | {res.Hint}");
            else Console.WriteLine($"  Bild: {res.OutputPath}");
        }
    }
}
