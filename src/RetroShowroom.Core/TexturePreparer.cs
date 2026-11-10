using System.Security.Cryptography;
using System.Text;

namespace RetroShowroom.Core;

public sealed record PreparedTexture(
    string SourcePath,
    string PngPath,
    string TextureName,
    int Width,
    int Height,
    string FormatName,
    IReadOnlyList<string> AllTextureNames);

/// <summary>Macht aus einer .ytd (oder .png) eine PNG-Datei, die vitrine.py laden kann.</summary>
public static class TexturePreparer
{
    public static PreparedTexture Prepare(string sourcePath, string? textureName = null)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext == ".png")
        {
            var info = PngWriter.ReadInfo(sourcePath) ?? throw new InvalidDataException(Loc.T("Die Datei ist keine gültige PNG-Datei.", "The file is not a valid PNG file."));
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            return new(sourcePath, sourcePath, name, info.Width, info.Height, "PNG", [name]);
        }
        if (ext != ".ytd")
            throw new NotSupportedException(Loc.T("Die Textur muss eine .ytd- oder .png-Datei sein.", "The texture must be a .ytd or .png file."));

        var textures = YtdReader.Read(sourcePath);
        var pick = (textureName is null ? null : textures.FirstOrDefault(t => t.Name.Equals(textureName, StringComparison.OrdinalIgnoreCase)))
                   ?? PickDefault(textures);

        var fi = new FileInfo(sourcePath);
        var key = ShortHash($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{pick.Name}");
        var dir = AppPaths.Ensure(AppPaths.CacheDir);
        var png = Path.Combine(dir, $"{Safe(pick.Name)}_{key}.png");

        if (!File.Exists(png))
        {
            var rgba = TextureDecoder.ToRgba(pick);
            var tmp = png + ".tmp";
            PngWriter.WriteRgba(tmp, pick.Width, pick.Height, rgba);
            File.Move(tmp, png, true);
        }

        return new(sourcePath, png, pick.Name, pick.Width, pick.Height, pick.FormatName,
            textures.Select(t => t.Name).ToList());
    }

    /// <summary>Diffuse-Textur bevorzugen, Normal-/Spekular-Maps nach hinten.</summary>
    private static YtdTexture PickDefault(IReadOnlyList<YtdTexture> textures)
    {
        return textures.FirstOrDefault(t => t.Name.Contains("_diff_", StringComparison.OrdinalIgnoreCase) && t.DataLength > 0)
               ?? textures.FirstOrDefault(t => t.DataLength > 0 && !IsAuxMap(t.Name))
               ?? textures.FirstOrDefault(t => t.DataLength > 0)
               ?? textures[0];
    }

    private static bool IsAuxMap(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("normal") || n.Contains("_spec") || n.EndsWith("_n") || n.EndsWith("_s");
    }

    /// <summary>Alte Cache-Dateien aufräumen (dekodierte Texturen, abgebrochene Renderings).</summary>
    public static void CleanCache(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(AppPaths.CacheDir)) return;
            foreach (var f in Directory.EnumerateFiles(AppPaths.CacheDir))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > maxAge)
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private static string ShortHash(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..10].ToLowerInvariant();

    private static string Safe(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
