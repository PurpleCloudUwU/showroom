using System.Reflection;
using System.Security.Cryptography;

namespace RetroShowroom.Core;

/// <summary>
/// Das abgesegnete Render-Skript „Glasvitrine“ (vitrine.py) ist fest in die App eingebaut.
/// Blender braucht eine Datei, deshalb wird es beim Rendern in den Cache-Ordner geschrieben
/// (nur wenn es dort fehlt oder abweicht).
/// </summary>
public static class RenderScript
{
    private const string ResourceName = "vitrine.py";

    /// <summary>Schreibt das eingebaute Skript in den Cache und gibt den Pfad zurück.</summary>
    public static string Extract()
    {
        using var stream = typeof(RenderScript).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException("Das Render-Skript fehlt in der App (Ressource vitrine.py).");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();

        var dir = AppPaths.Ensure(Path.Combine(AppPaths.CacheDir, "skript"));
        var path = Path.Combine(dir, "vitrine.py");

        if (File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            return path;

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
        return path;
    }
}
