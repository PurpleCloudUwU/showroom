using System.Text.Json;

namespace RetroShowroom.Core;

/// <summary>Was von einer Einrichtungsprüfung gespeichert wird (alles, wofür Blender gestartet werden muss).</summary>
public sealed class SetupSnapshot
{
    public string? BlenderPath { get; set; }
    public long BlenderSize { get; set; }
    public long BlenderWriteTicks { get; set; }
    public string? BlenderVersion { get; set; }
    public int[]? BlenderVersionParts { get; set; }
    public string? SollumzKey { get; set; }
    public string? SollumzVersion { get; set; }
    public List<string> SkeletonPaths { get; set; } = [];
    public bool? PyMateriaFound { get; set; }
    public List<GpuDevice> Gpus { get; set; } = [];
    public DateTime CheckedAt { get; set; }
}

/// <summary>
/// Merkt sich das Ergebnis der letzten Einrichtungsprüfung. Die Prüfung startet Blender und dauert 3 bis 7 Sekunden,
/// so lange wäre „Rendern starten“ nach jedem Start der App gesperrt. Mit dem gemerkten Stand ist die App sofort
/// bereit, die echte Prüfung läuft trotzdem jedes Mal im Hintergrund und ersetzt ihn.
/// Der Stand gilt nur, solange dieselbe blender.exe (Pfad, Größe, Änderungsdatum) verwendet wird.
/// </summary>
public static class SetupCache
{
    public static void Save(SetupReport report)
    {
        try
        {
            if (!report.BlenderOk || report.BlenderPath is null)
            {
                Clear();
                return;
            }
            var exe = new FileInfo(report.BlenderPath);
            var snapshot = new SetupSnapshot
            {
                BlenderPath = report.BlenderPath,
                BlenderSize = exe.Length,
                BlenderWriteTicks = exe.LastWriteTimeUtc.Ticks,
                BlenderVersion = report.BlenderVersion,
                BlenderVersionParts = report.BlenderVersionParts,
                SollumzKey = report.SollumzKey,
                SollumzVersion = report.SollumzVersion,
                SkeletonPaths = [.. report.SkeletonPaths],
                PyMateriaFound = report.PyMateriaFound,
                Gpus = [.. report.Gpus],
                CheckedAt = report.CheckedAt,
            };
            AppPaths.Ensure(AppPaths.DataDir);
            var tmp = AppPaths.SetupCacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.SetupSnapshot));
            File.Move(tmp, AppPaths.SetupCacheFile, true);
        }
        catch { }
    }

    public static void Clear()
    {
        try { File.Delete(AppPaths.SetupCacheFile); } catch { }
    }

    /// <summary>Gemerkter Stand, wenn er noch zur gefundenen blender.exe passt. Sonst null.</summary>
    public static SetupReport? TryLoad(AppSettings settings)
    {
        try
        {
            if (!File.Exists(AppPaths.SetupCacheFile)) return null;
            var s = JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SetupCacheFile), CoreJsonContext.Default.SetupSnapshot);
            if (s?.BlenderPath is null || s.BlenderVersionParts is null) return null;

            var blender = BlenderLocator.Find(settings.BlenderPath);
            if (!string.Equals(blender, s.BlenderPath, StringComparison.OrdinalIgnoreCase)) return null;
            var exe = new FileInfo(blender!);
            if (!exe.Exists || exe.Length != s.BlenderSize || exe.LastWriteTimeUtc.Ticks != s.BlenderWriteTicks) return null;

            var report = new SetupReport
            {
                CheckedAt = s.CheckedAt,
                FromCache = true,
                BlenderPath = blender,
                BlenderProbed = true,
                BlenderVersion = s.BlenderVersion,
                BlenderVersionParts = s.BlenderVersionParts,
                SollumzKey = s.SollumzKey,
                SollumzVersion = s.SollumzVersion,
                PyMateriaFound = s.PyMateriaFound,
            };
            report.SkeletonPaths.AddRange(s.SkeletonPaths);
            report.Gpus.AddRange(s.Gpus);
            SetupChecker.ResolveSkeletons(report);
            SetupChecker.UpdateAssets(report, settings);
            return report.BlenderOk ? report : null;
        }
        catch
        {
            return null;
        }
    }
}
