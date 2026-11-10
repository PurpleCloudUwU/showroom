namespace RetroShowroom.Core;

public enum AssetKind { Logo, Reference }

/// <summary>
/// Kopiert die vom Nutzer gewählten Assets (Logo, Referenzbild) unverändert
/// in den Datenordner, damit sie nicht verloren gehen, wenn das Original verschoben wird.
/// </summary>
public static class AssetStore
{
    private static string Dir(AssetKind kind) => Path.Combine(AppPaths.AssetsDir, kind.ToString());

    /// <summary>Voller Pfad zum gespeicherten Asset oder null, wenn es fehlt.</summary>
    public static string? Resolve(AssetKind kind, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var path = Path.Combine(Dir(kind), fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Kopiert die Datei in den Assets-Ordner und gibt den Dateinamen zurück.</summary>
    public static string Import(AssetKind kind, string sourcePath)
    {
        var dir = AppPaths.Ensure(Dir(kind));
        var name = Path.GetFileName(sourcePath);
        var target = Path.Combine(dir, name);
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return name;

        var tmp = Path.Combine(dir, ".import.tmp");
        File.Copy(sourcePath, tmp, true);
        foreach (var old in Directory.GetFiles(dir))
            if (!string.Equals(old, tmp, StringComparison.OrdinalIgnoreCase))
                File.Delete(old);
        File.Move(tmp, target);
        return name;
    }

    public static void Remove(AssetKind kind)
    {
        var dir = Dir(kind);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>Kurze Prüfung beim Hinzufügen. Gibt einen Warnhinweis zurück oder null.</summary>
    public static string? Validate(AssetKind kind, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (kind)
        {
            case AssetKind.Logo:
                if (ext != ".png") return Loc.T("Das Logo muss eine PNG-Datei sein.", "The logo must be a PNG file.");
                var info = PngWriter.ReadInfo(path);
                if (info is null) return Loc.T("Die Datei ist keine gültige PNG-Datei.", "The file is not a valid PNG file.");
                if (!info.Value.HasAlpha)
                    return Loc.T("Das Logo hat keinen Alphakanal. Der Hintergrund wird dann nicht transparent.",
                                 "The logo has no alpha channel, so its background will not be transparent.");
                return null;

            case AssetKind.Reference:
                return ext is ".png" or ".jpg" or ".jpeg" ? null : Loc.T("Das Referenzbild muss PNG oder JPG sein.", "The reference image must be PNG or JPG.");
        }
        return null;
    }

    /// <summary>Gilt die Datei als Fehler (nicht nur Warnung)? Dann wird sie nicht übernommen.</summary>
    public static bool IsHardError(AssetKind kind, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return kind switch
        {
            AssetKind.Logo => ext != ".png" || PngWriter.ReadInfo(path) is null,
            AssetKind.Reference => ext is not (".png" or ".jpg" or ".jpeg"),
            _ => false,
        };
    }
}
