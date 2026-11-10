using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RetroShowroom.Core;

public sealed record GpuDevice(string Backend, string Name);

/// <summary>Ergebnis der Einrichtungsprüfung.</summary>
public sealed class SetupReport
{
    /// <summary>vitrine.py liest die Sollumz-Einstellungen genau unter diesem Namen.</summary>
    public const string ExpectedSollumzKey = "bl_ext.user_default.sollumz";

    public DateTime CheckedAt { get; init; } = DateTime.Now;
    /// <summary>
    /// Stand der letzten Prüfung aus <see cref="SetupCache"/>. Wird angezeigt, bis die laufende Prüfung fertig ist.
    /// </summary>
    public bool FromCache { get; init; }

    public string? BlenderPath { get; set; }
    public bool BlenderProbed { get; set; }
    public string? BlenderVersion { get; set; }
    public int[]? BlenderVersionParts { get; set; }
    public string? BlenderError { get; set; }

    public string? SollumzKey { get; set; }
    public string? SollumzVersion { get; set; }
    public List<string> SkeletonPaths { get; } = [];
    public string? SkeletonMale { get; set; }
    public string? SkeletonFemale { get; set; }
    public bool? PyMateriaFound { get; set; }
    public List<GpuDevice> Gpus { get; } = [];
    public string? GpuSummary => Gpus.Count == 0 ? null : $"{string.Join(", ", Gpus.Select(g => g.Name).Distinct())} ({Gpus[0].Backend})";

    public string? LogoPath { get; set; }
    public string? ReferencePath { get; set; }

    public bool BlenderFound => BlenderPath is not null;
    public bool BlenderVersionOk => BlenderVersionParts is { Length: >= 2 } v && (v[0] > 4 || (v[0] == 4 && v[1] >= 2));
    public bool BlenderOk => BlenderFound && BlenderProbed && BlenderError is null && BlenderVersionOk;
    /// <summary>Neues Log-System (--log-level info) ab Blender 5.</summary>
    public bool SupportsDetailedLog => BlenderVersionParts is { Length: >= 1 } v && v[0] >= 5;
    public bool SollumzOk => BlenderOk && SollumzKey == ExpectedSollumzKey;
    public bool SkeletonOk(char gender) => SollumzOk && (gender == 'f' ? SkeletonFemale : SkeletonMale) is not null;

    /// <summary>Was fehlt noch, um ein Teil dieses Geschlechts zu rendern? (kurze Stichworte)</summary>
    public List<string> BlockingIssues(char gender)
    {
        var l = new List<string>();
        if (!BlenderFound) l.Add("Blender");
        else if (!BlenderOk) l.Add(Loc.T("Blender (startet nicht oder zu alt)", "Blender (does not start or too old)"));
        else if (!SollumzOk) l.Add("Sollumz");
        else if (!SkeletonOk(gender))
            l.Add(gender == 'f' ? Loc.T("Skelett weiblich", "female skeleton") : Loc.T("Skelett männlich", "male skeleton"));
        if (LogoPath is null) l.Add("Logo");
        return l;
    }

    /// <summary>Anzahl offener Punkte (für das Abzeichen in der Navigation).</summary>
    public int OpenPoints =>
        (BlenderOk ? 0 : 1) + (SollumzOk ? 0 : 1) + (SkeletonOk('m') ? 0 : 1) + (SkeletonOk('f') ? 0 : 1) +
        (LogoPath is null ? 1 : 0);
}

/// <summary>Prüft Blender, Sollumz, Skelette und Assets.</summary>
public static class SetupChecker
{
    private const string Marker = "RS_CHECK ";

    private const string ProbeScript = """
        import bpy, sys, os, re, json, importlib.util
        r = {"blender": bpy.app.version_string, "version": list(bpy.app.version)}
        KEY = "bl_ext.user_default.sollumz"
        addons = bpy.context.preferences.addons
        key = KEY if KEY in addons else next((k for k in addons.keys() if k.split(".")[-1].lower() == "sollumz"), None)
        r["sollumz_key"] = key
        r["skeletons"] = []
        if key:
            a = addons[key]
            try:
                r["skeletons"] = [p.name for p in a.preferences.external_skeleton_paths]
            except Exception as e:
                r["skeletons_error"] = repr(e)
            ver = None
            try:
                m = sys.modules.get(key)
                d = os.path.dirname(m.__file__)
                p = os.path.join(d, "blender_manifest.toml")
                if os.path.exists(p):
                    with open(p, encoding="utf-8") as f:
                        mm = re.search(r'^version\s*=\s*"([^"]+)"', f.read(), re.M)
                    ver = mm.group(1) if mm else None
                if ver is None and hasattr(m, "bl_info"):
                    ver = ".".join(str(x) for x in m.bl_info.get("version", ()))
            except Exception:
                pass
            r["sollumz_version"] = ver
        try:
            r["pymateria"] = importlib.util.find_spec("pymateria") is not None
        except Exception:
            r["pymateria"] = None
        gpus = []
        try:
            cp = bpy.context.preferences.addons["cycles"].preferences
            orig = cp.compute_device_type
            for typ in ("OPTIX", "CUDA", "HIP", "ONEAPI"):
                try:
                    cp.compute_device_type = typ
                except Exception:
                    continue
                try:
                    cp.refresh_devices()
                except Exception:
                    pass
                names = [d.name for d in cp.devices if d.type == typ]
                if names:
                    gpus.append({"type": typ, "names": names})
                    break
            try:
                cp.compute_device_type = orig
            except Exception:
                pass
        except Exception as e:
            r["gpu_error"] = repr(e)
        r["gpus"] = gpus
        print("RS_CHECK " + json.dumps(r), flush=True)
        """;

    public static async Task<SetupReport> RunAsync(AppSettings settings, CancellationToken ct = default)
    {
        var report = new SetupReport { BlenderPath = BlenderLocator.Find(settings.BlenderPath) };
        UpdateAssets(report, settings);
        if (report.BlenderPath is not null)
        {
            try
            {
                await ProbeBlenderAsync(report, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.BlenderProbed = true;
                report.BlenderError = ex.Message;
            }
        }
        // Für den nächsten Start merken (oder den alten Stand verwerfen, wenn Blender nicht mehr passt)
        SetupCache.Save(report);
        return report;
    }

    /// <summary>Nur die Asset-Pfade neu ermitteln (schnell, ohne Blender zu starten).</summary>
    public static void UpdateAssets(SetupReport report, AppSettings settings)
    {
        report.LogoPath = AssetStore.Resolve(AssetKind.Logo, settings.LogoAsset);
        report.ReferencePath = AssetStore.Resolve(AssetKind.Reference, settings.ReferenceAsset);
    }

    private static async Task ProbeBlenderAsync(SetupReport report, CancellationToken ct)
    {
        var dir = AppPaths.Ensure(AppPaths.CacheDir);
        var script = Path.Combine(dir, "einrichtung_pruefen.py");
        await File.WriteAllTextAsync(script, ProbeScript.Replace("\r\n", "\n"), new UTF8Encoding(false), ct);

        var psi = new ProcessStartInfo(report.BlenderPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = dir,
        };
        foreach (var a in new[] { "-b", "--python-exit-code", "1", "--python", script })
            psi.ArgumentList.Add(a);

        var lines = new List<string>();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            report.BlenderProbed = true;
            report.BlenderError = Loc.T("Blender hat innerhalb von 2 Minuten nicht geantwortet.", "Blender did not respond within 2 minutes.");
            return;
        }

        report.BlenderProbed = true;
        string? json;
        lock (lines) json = lines.LastOrDefault(l => l.StartsWith(Marker))?[Marker.Length..];
        if (json is null)
        {
            string tail;
            lock (lines) tail = string.Join("\n", lines.TakeLast(6));
            report.BlenderError = (Loc.T($"Blender ist gestartet, hat aber kein Ergebnis geliefert (Exit-Code {p.ExitCode}).",
                                         $"Blender started but returned no result (exit code {p.ExitCode}).") + "\n" + tail).Trim();
            return;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        report.BlenderVersion = root.GetProperty("blender").GetString();
        report.BlenderVersionParts = root.GetProperty("version").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        report.SollumzKey = root.TryGetProperty("sollumz_key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        report.SollumzVersion = root.TryGetProperty("sollumz_version", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() : null;
        report.PyMateriaFound = root.TryGetProperty("pymateria", out var pm) && pm.ValueKind is JsonValueKind.True or JsonValueKind.False ? pm.GetBoolean() : null;

        if (root.TryGetProperty("skeletons", out var sk))
            foreach (var e in sk.EnumerateArray())
                if (e.GetString() is { Length: > 0 } s)
                    report.SkeletonPaths.Add(s);

        ResolveSkeletons(report);

        if (root.TryGetProperty("gpus", out var gpus))
            foreach (var g in gpus.EnumerateArray())
            {
                var type = g.GetProperty("type").GetString() ?? "";
                foreach (var n in g.GetProperty("names").EnumerateArray())
                    report.Gpus.Add(new GpuDevice(GpuHook.BackendName(type), n.GetString() ?? "?"));
            }
    }

    /// <summary>Sucht in den eingetragenen Pfaden die Skelette für beide Geschlechter.</summary>
    internal static void ResolveSkeletons(SetupReport report)
    {
        report.SkeletonMale = FindSkeleton(report.SkeletonPaths, 'm');
        report.SkeletonFemale = FindSkeleton(report.SkeletonPaths, 'f');
    }

    private static string? FindSkeleton(IEnumerable<string> paths, char gender)
    {
        var name = $"mp_{gender}_freemode_01.yft";
        return paths.FirstOrDefault(p => p.Contains(name, StringComparison.OrdinalIgnoreCase) && SkeletonSourceExists(p));
    }

    /// <summary>
    /// Sollumz speichert Skelette aus den Spieldateien als Pfad in ein RPF-Archiv hinein,
    /// z. B. „D:\GTA V\x64v.rpf\models\cdimages\streamedpeds_mp.rpf\mp_m_freemode_01.yft“.
    /// Dann muss das äußere Archiv (x64v.rpf) existieren, nicht die .yft als Datei.
    /// </summary>
    internal static bool SkeletonSourceExists(string path)
    {
        try
        {
            if (File.Exists(path)) return true;
            var parts = path.Split('\\', '/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (!parts[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) continue;
                var archive = string.Join('\\', parts.Take(i + 1));
                return File.Exists(archive);
            }
        }
        catch { }
        return false;
    }
}
