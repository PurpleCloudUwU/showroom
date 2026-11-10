using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroShowroom.Core;

public static class Quality
{
    public const string Test = "TEST", Final = "FINAL", Gross = "GROSS";
}

/// <summary>Alle Angaben für einen Aufruf von vitrine.py.</summary>
public sealed record RenderRequest
{
    public required string BlenderPath { get; init; }
    public required string ScriptPath { get; init; }
    public required string PartPath { get; init; }
    public required string TexturePng { get; init; }
    public required string LogoPath { get; init; }
    /// <summary>Wohin Blender schreibt (temporär, ohne Sonderzeichen wie #).</summary>
    public required string RenderTarget { get; init; }
    /// <summary>Endgültiger Speicherort.</summary>
    public required string OutputPath { get; init; }
    public required string Title { get; init; }
    public required char Gender { get; init; }
    public required string Kind { get; init; }
    public bool LongTop { get; init; }
    public string Quality { get; init; } = RenderDefaults.Quality;
    public string Style { get; init; } = RenderDefaults.Style;
    public bool Spots { get; init; } = RenderDefaults.Spots;
    public double SpotWatt { get; init; } = RenderDefaults.SpotWatt;
    public bool Kontur { get; init; } = RenderDefaults.Kontur;

    /// <summary>Grafikkarte verwenden (mit automatischem Rückfall auf die CPU).</summary>
    public bool UseGpu { get; init; } = true;
    /// <summary>Pfad zum GPU-Skript (setzt <see cref="RenderRunner"/>).</summary>
    public string? GpuHookPath { get; init; }

    /// <summary>
    /// Blender 5+: Fortschritt („Sample x/y“) und Geräte-Infos werden nur mit Log-Level info ausgegeben.
    /// Ältere Versionen kennen die Option nicht (dort kommt der Fortschritt ohnehin).
    /// </summary>
    public bool DetailedLog { get; init; }

    /// <summary>
    /// blender.exe -b --python vitrine.py -- &lt;TEST|FINAL|GROSS&gt; weiss &lt;teil&gt; &lt;textur.png&gt; &lt;logo.png&gt; &lt;ausgabe.png&gt; "&lt;TITEL&gt;" &lt;m|f&gt; &lt;oben|hose&gt; --spots --spot_w=250 --kontur
    /// </summary>
    public IReadOnlyList<string> BuildArguments()
    {
        var a = new List<string> { "-b" };
        if (DetailedLog)
            a.AddRange(["--log-level", "info", "--log", "render,cycles"]);
        a.AddRange(
        [
            "--python-exit-code", "1",
            // Python-Ausgaben sofort durchreichen (sonst kommen sie erst am Ende)
            "--python-expr", "import sys; getattr(sys.stdout, 'reconfigure', lambda **k: None)(line_buffering=True)",
        ]);
        if (UseGpu && GpuHookPath is not null)
        {
            a.Add("--python");
            a.Add(GpuHookPath);
        }
        a.AddRange([
            "--python", ScriptPath,
            "--",
            Quality, Style, PartPath, TexturePng, LogoPath, RenderTarget, Title,
            Gender.ToString(), Kind,
        ]);
        if (Spots)
        {
            a.Add("--spots");
            a.Add("--spot_w=" + SpotWatt.ToString("0.##", CultureInfo.InvariantCulture));
        }
        if (Kontur) a.Add("--kontur");
        if (LongTop) a.Add("--lang");
        return a;
    }

    public string CommandLinePreview() =>
        string.Join(" ", new[] { BlenderPath }.Concat(BuildArguments()).Select(x => x.Contains(' ') || x.Length == 0 ? $"\"{x}\"" : x));
}

public sealed class RenderProgress
{
    public string Phase { get; init; } = "";
    public int Sample { get; init; }
    public int TotalSamples { get; init; }
    public TimeSpan? Remaining { get; init; }
    /// <summary>z. B. „Grafikkarte (OptiX): NVIDIA GeForce RTX 4070“ oder „CPU“.</summary>
    public string? Device { get; init; }
}

public sealed record RenderResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public int ExitCode { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
    public string? Hint { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Device { get; init; }
    /// <summary>Grafikkarte ist gescheitert, das Bild kam von der CPU.</summary>
    public bool FellBackToCpu { get; init; }
}

/// <summary>Startet Blender im Hintergrund und wertet die Ausgabe aus.</summary>
public sealed partial class RenderRunner
{
    public event Action<string>? Output;
    public event Action<RenderProgress>? Progress;
    /// <summary>Hinweise für die Oberfläche, z. B. „neuer Versuch auf der CPU“.</summary>
    public event Action<string>? Notice;

    [GeneratedRegex(@"Sample\s+(\d+)\s*/\s*(\d+)")]
    private static partial Regex SampleRegex();

    [GeneratedRegex(@"Remaining:\s*((?:\d+:)?\d+:\d+(?:\.\d+)?)")]
    private static partial Regex RemainingRegex();

    [GeneratedRegex(@"^RS_GPU gefunden (\w+): (.+)$")]
    private static partial Regex GpuFoundRegex();

    private static string NoGpuText => Loc.T("CPU (keine kompatible Grafikkarte gefunden)", "CPU (no compatible graphics card found)");

    private readonly Queue<string> _tail = new();
    private string _phase = "";
    private string? _device;
    private string? _gpuName;
    private bool _gpuActive;
    private bool _noGpu;

    public async Task<RenderResult> RunAsync(RenderRequest req, CancellationToken ct)
    {
        if (!req.UseGpu)
            return await RunOnceAsync(req with { GpuHookPath = null }, ct);

        var gpu = await RunOnceAsync(req with { GpuHookPath = GpuHook.Write() }, ct);
        if (gpu.Success || gpu.Cancelled || !_gpuActive || IsSetupProblem(Tail()))
            return gpu;

        // Grafikkarte war aktiv und ist gescheitert (z. B. Grafikspeicher voll) → einmal auf der CPU wiederholen.
        Notice?.Invoke(Loc.T("Die Grafikkarte ist gescheitert. Neuer Versuch auf der CPU …", "The graphics card failed. Retrying on the CPU …"));
        Output?.Invoke(Loc.T("---- Grafikkarte fehlgeschlagen, neuer Versuch auf der CPU ----", "---- Graphics card failed, retrying on the CPU ----"));
        var cpu = await RunOnceAsync(req with { UseGpu = false, GpuHookPath = null }, ct);
        return cpu with { FellBackToCpu = true, Duration = gpu.Duration + cpu.Duration };
    }

    private async Task<RenderResult> RunOnceAsync(RenderRequest req, CancellationToken ct)
    {
        lock (_tail) _tail.Clear();
        _phase = Loc.T("Blender startet …", "Starting Blender …");
        _gpuActive = false;
        _noGpu = false;
        _gpuName = null;
        _device = req.UseGpu ? Loc.T("Grafikkarte wird gesucht …", "Looking for graphics card …") : "CPU";

        foreach (var (path, what) in new[]
                 {
                     (req.BlenderPath, "Blender"),
                     (req.ScriptPath, Loc.T("Render-Skript", "Render script")),
                     (req.PartPath, Loc.T("Kleidungsstück", "Clothing item")),
                     (req.TexturePng, Loc.T("Textur", "Texture")),
                     (req.LogoPath, "Logo"),
                 })
        {
            if (!File.Exists(path))
                return new RenderResult { Error = Loc.T($"{what} nicht gefunden: {path}", $"{what} not found: {path}") };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(req.RenderTarget)!);
        Directory.CreateDirectory(Path.GetDirectoryName(req.OutputPath)!);
        if (File.Exists(req.RenderTarget)) File.Delete(req.RenderTarget);

        var psi = new ProcessStartInfo(req.BlenderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(req.RenderTarget)!,
        };
        foreach (var a in req.BuildArguments()) psi.ArgumentList.Add(a);

        Report();
        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) HandleLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) HandleLine(e.Data); };

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new RenderResult { Error = Loc.T("Blender konnte nicht gestartet werden: ", "Blender could not be started: ") + ex.Message };
        }

        // Rechner bleibt benutzbar, während gerendert wird
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        bool cancelled = false;
        using (ct.Register(() =>
               {
                   cancelled = true;
                   try { p.Kill(entireProcessTree: true); } catch { }
               }))
        {
            await p.WaitForExitAsync(CancellationToken.None);
        }
        sw.Stop();

        var device = _gpuActive ? _device : !req.UseGpu ? "CPU" : _noGpu ? NoGpuText : "CPU";

        if (cancelled)
        {
            try { if (File.Exists(req.RenderTarget)) File.Delete(req.RenderTarget); } catch { }
            return new RenderResult { Cancelled = true, ExitCode = -1, Duration = sw.Elapsed, Device = device };
        }

        var tail = Tail();
        if (p.ExitCode != 0 || !File.Exists(req.RenderTarget))
        {
            return new RenderResult
            {
                ExitCode = p.ExitCode,
                Duration = sw.Elapsed,
                Device = device,
                Error = p.ExitCode != 0
                    ? Loc.T($"Blender wurde mit Fehler beendet (Exit-Code {p.ExitCode}).", $"Blender exited with an error (exit code {p.ExitCode}).")
                    : Loc.T("Blender ist fertig, hat aber kein Bild gespeichert.", "Blender finished but did not save an image."),
                Hint = Diagnose(tail),
            };
        }

        File.Move(req.RenderTarget, req.OutputPath, overwrite: false);
        return new RenderResult { Success = true, OutputPath = req.OutputPath, Duration = sw.Elapsed, Device = device };
    }

    private string[] Tail()
    {
        lock (_tail) return _tail.ToArray();
    }

    private void Report(int sample = 0, int total = 0, TimeSpan? remaining = null) =>
        Progress?.Invoke(new RenderProgress { Phase = _phase, Sample = sample, TotalSamples = total, Remaining = remaining, Device = _device });

    private void HandleLine(string line)
    {
        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > 300) _tail.Dequeue();
        }
        Output?.Invoke(line);

        if (line.StartsWith(GpuHook.Marker, StringComparison.Ordinal))
        {
            var gm = GpuFoundRegex().Match(line);
            if (gm.Success)
            {
                _gpuName = Loc.T("Grafikkarte", "Graphics card") + $" ({GpuHook.BackendName(gm.Groups[1].Value)}): {gm.Groups[2].Value}";
                _device = _gpuName;
            }
            else if (line.StartsWith("RS_GPU aktiv", StringComparison.Ordinal))
            {
                _gpuActive = true;
                _device = _gpuName ?? Loc.T("Grafikkarte", "Graphics card");
            }
            else if (line.Contains("keine", StringComparison.Ordinal))
            {
                _noGpu = true;
                _device = NoGpuText;
            }
            Report();
            return;
        }

        int sample = 0, total = 0;
        TimeSpan? remaining = null;
        var sm = SampleRegex().Match(line);
        if (sm.Success)
        {
            sample = int.Parse(sm.Groups[1].Value);
            total = int.Parse(sm.Groups[2].Value);
            _phase = Loc.T("Rendern", "Rendering");
        }
        var rm = RemainingRegex().Match(line);
        if (rm.Success) remaining = ParseTime(rm.Groups[1].Value);

        if (!sm.Success)
        {
            var phase = MapPhase(line);
            if (phase is null) return;
            _phase = phase;
        }
        Report(sample, total, remaining);
    }

    private static string? MapPhase(string line)
    {
        if (line.StartsWith("Teil ", StringComparison.Ordinal)) return Loc.T("Kleidungsstück importiert", "Clothing item imported");
        if (line.Contains("Loading render kernels") || line.Contains("Compiling"))
            return Loc.T("Render-Kernels werden geladen (beim ersten Mal länger)", "Loading render kernels (takes longer the first time)");
        if (line.Contains("Fra:") && line.Contains("Denoising") && !line.Contains("kernels")) return Loc.T("Entrauschen", "Denoising");
        if (line.Contains("Fra:") && line.TrimEnd().EndsWith("| Finished"))
            return Loc.T("Fertig gerendert, Bild wird gespeichert", "Rendering finished, saving image");
        if (line.Contains("Saved:")) return Loc.T("Bild gespeichert", "Image saved");
        if (line.Contains("VITRINE FERTIG")) return Loc.T("Fertig", "Done");
        if (line.Contains("Fra:") && (line.Contains("Updating") || line.Contains("Synchroniz") || line.Contains("Loading")))
            return Loc.T("Szene wird vorbereitet", "Preparing scene");
        return null;
    }

    private static TimeSpan? ParseTime(string s)
    {
        var parts = s.Split(':');
        try
        {
            double sec = double.Parse(parts[^1], CultureInfo.InvariantCulture);
            int min = int.Parse(parts[^2]);
            int h = parts.Length > 2 ? int.Parse(parts[^3]) : 0;
            return new TimeSpan(h, min, 0) + TimeSpan.FromSeconds(sec);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fehler, die mit der Einrichtung zu tun haben (dann hilft ein CPU-Versuch nicht).</summary>
    public static bool IsSetupProblem(IReadOnlyCollection<string> lines)
    {
        var all = string.Join("\n", lines);
        return (all.Contains("bl_ext.user_default.sollumz") && all.Contains("KeyError"))
               || all.Contains("SK = next")
               || (all.Contains("StopIteration") && all.Contains("external_skeleton_paths"))
               || (all.Contains("PyMateria", StringComparison.OrdinalIgnoreCase) && all.Contains("licen", StringComparison.OrdinalIgnoreCase))
               || (all.Contains("root = [o for o in neu") && all.Contains("IndexError"));
    }

    /// <summary>Bekannte Fehlerbilder in eine verständliche Erklärung übersetzen.</summary>
    public static string? Diagnose(IReadOnlyCollection<string> lines)
    {
        var all = string.Join("\n", lines);
        if (all.Contains("bl_ext.user_default.sollumz") && all.Contains("KeyError"))
            return Loc.T("Sollumz ist in Blender nicht installiert oder nicht aktiviert. Siehe „Einrichtung“.",
                         "Sollumz is not installed or not enabled in Blender. See \"Setup\".");
        if (all.Contains("SK = next") || (all.Contains("StopIteration") && all.Contains("external_skeleton_paths")))
            return Loc.T("Das Freemode-Skelett für dieses Geschlecht fehlt in den Sollumz-Einstellungen. Siehe „Einrichtung“.",
                         "The freemode skeleton for this gender is missing from the Sollumz preferences. See \"Setup\".");
        if (all.Contains("PyMateria", StringComparison.OrdinalIgnoreCase) && all.Contains("licen", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Die PyMateria-Lizenz wurde in den Sollumz-Einstellungen noch nicht angenommen.",
                         "The PyMateria license has not been accepted in the Sollumz preferences yet.");
        if (all.Contains("root = [o for o in neu") && all.Contains("IndexError"))
            return Loc.T("Das Kleidungsstück konnte nicht importiert werden (keine Objekte). Ist die Datei in Ordnung?",
                         "The clothing item could not be imported (no objects). Is the file intact?");
        if (Regex.IsMatch(all, @"CUDA error|OptiX error|HIP error|CUDA_ERROR|cuMem|Out of memory in Cycles|out of device memory", RegexOptions.IgnoreCase))
            return Loc.T("Der Grafikkartenspeicher reicht nicht. Andere Blender-Fenster oder Spiele schließen, oder in den Einstellungen „Grafikkarte verwenden“ ausschalten.",
                         "Not enough graphics memory. Close other Blender windows or games, or turn off \"Use graphics card\" in the settings.");
        if (all.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || all.Contains("bad_alloc"))
            return Loc.T("Zu wenig Speicher. Andere Blender-Fenster schließen und erneut versuchen.",
                         "Not enough memory. Close other Blender windows and try again.");
        if (all.Contains("Traceback"))
        {
            var last = lines.LastOrDefault(l => Regex.IsMatch(l, @"^\w*(Error|Exception)\b"));
            if (last is not null) return Loc.T("Python-Fehler im Render-Skript: ", "Python error in the render script: ") + last;
        }
        return null;
    }
}

public static partial class OutputNaming
{
    // Wird bei jedem Tastendruck im Titelfeld gebraucht: Zeichensatz und Muster nur einmal anlegen
    private static readonly SearchValues<char> Invalid = SearchValues.Create([.. Path.GetInvalidFileNameChars(), '#']);

    [GeneratedRegex("_+")]
    private static partial Regex Underscores();

    public static string Safe(string title)
    {
        var chars = title.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Invalid.Contains(chars[i]) || char.IsWhiteSpace(chars[i]))
                chars[i] = '_';
        var s = Underscores().Replace(new string(chars), "_").Trim('_', '.');
        return s.Length == 0 ? "SHOWROOM" : s;
    }

    public static string BaseName(string title, string quality) => $"{Safe(title)}_{quality.ToUpperInvariant()}";

    /// <summary>Nächster freier Dateiname im Ordner (…_FINAL.png, …_FINAL_2.png, …).</summary>
    public static string Next(string folder, string title, string quality)
    {
        var b = BaseName(title, quality);
        var p = Path.Combine(folder, b + ".png");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(folder, $"{b}_{i}.png");
        return p;
    }
}
