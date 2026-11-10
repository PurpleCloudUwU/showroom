using System.Text.RegularExpressions;

namespace RetroShowroom.Core;

/// <summary>Infos zum Kleidungsstück, aus dem Dateinamen abgeleitet.</summary>
public sealed partial class PartInfo
{
    public required string FullPath { get; init; }
    public string FileName => Path.GetFileName(FullPath);
    public string BaseName => Path.GetFileNameWithoutExtension(FullPath);
    public bool IsBlend => FullPath.EndsWith(".blend", StringComparison.OrdinalIgnoreCase);

    /// <summary>'m' oder 'f', null wenn nicht erkennbar.</summary>
    public char? Gender { get; init; }
    /// <summary>"oben" oder "hose" (wie in vitrine.py: „lowr“ im Namen = Hose).</summary>
    public required string Kind { get; init; }
    public string? Component { get; init; }
    public string? Index { get; init; }
    public string? Prefix { get; init; }

    public static readonly string[] PartExtensions = [".ydd", ".blend"];
    public static readonly string[] TextureExtensions = [".ytd", ".png"];

    public static bool IsPartFile(string path) =>
        PartExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsTextureFile(string path) =>
        TextureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    [GeneratedRegex(@"(?:^|[_^\-])mp_([mf])_", RegexOptions.IgnoreCase)]
    private static partial Regex GenderRegex();

    [GeneratedRegex(@"(?<comp>[a-z]+)_(?<idx>\d{3})_(?<suf>[a-z]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComponentRegex();

    public static PartInfo FromPath(string path)
    {
        var full = Path.GetFullPath(path);
        var b = Path.GetFileNameWithoutExtension(full);
        var gm = GenderRegex().Match(b);
        var cm = ComponentRegex().Match(b);
        return new PartInfo
        {
            FullPath = full,
            Gender = gm.Success ? char.ToLowerInvariant(gm.Groups[1].Value[0]) : null,
            Kind = b.Contains("lowr", StringComparison.OrdinalIgnoreCase) ? "hose" : "oben",
            Component = cm.Success ? cm.Groups["comp"].Value.ToLowerInvariant() : null,
            Index = cm.Success ? cm.Groups["idx"].Value : null,
            Prefix = cm.Success ? b[..cm.Index] : null,
        };
    }

    /// <summary>
    /// Passende Texturen im selben Ordner, z. B. zu „…^jbib_016_u.ydd“ alle „…^jbib_diff_016_*.ytd/.png“.
    /// </summary>
    public IReadOnlyList<string> FindTextureCandidates()
    {
        var dir = Path.GetDirectoryName(FullPath);
        if (dir is null || !Directory.Exists(dir)) return [];

        var files = Directory.EnumerateFiles(dir).Where(IsTextureFile).ToList();
        List<string> hits;
        if (Component is not null && Index is not null)
        {
            var exact = (Prefix ?? "") + Component + "_diff_" + Index + "_";
            hits = files.Where(f => Path.GetFileName(f).StartsWith(exact, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
            {
                var loose = Component + "_diff_" + Index + "_";
                hits = files.Where(f => Path.GetFileName(f).Contains(loose, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
        else
        {
            hits = files.Where(f => Path.GetFileName(f).Contains("_diff_", StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
        }

        // Alphabetisch (Variante a, b, c …), bei gleichem Namen .ytd vor .png
        return hits
            .OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => Path.GetExtension(f).Equals(".ytd", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }
}
