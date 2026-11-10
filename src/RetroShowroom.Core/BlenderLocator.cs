using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RetroShowroom.Core;

/// <summary>Sucht eine Blender-Installation (Programme, Steam, Dateizuordnung, PATH).</summary>
public static class BlenderLocator
{
    public static string? Find(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred)) return preferred;
        foreach (var c in Candidates())
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    public static IEnumerable<string> Candidates()
    {
        var list = new List<string>();

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                 })
        {
            var bf = Path.Combine(root, "Blender Foundation");
            if (!Directory.Exists(bf)) continue;
            list.AddRange(Directory.GetDirectories(bf)
                .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
                .Select(d => Path.Combine(d, "blender.exe")));
        }

        foreach (var lib in SteamLibraries())
            list.Add(Path.Combine(lib, "steamapps", "common", "Blender", "blender.exe"));

        if (FromFileAssociation() is { } assoc) list.Add(assoc);

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { list.Add(Path.Combine(p.Trim().Trim('"'), "blender.exe")); } catch { }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Version ParseVersion(string folderName)
    {
        var m = Regex.Match(folderName, @"(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : new Version(0, 0);
    }

    private static string? FromFileAssociation()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"blendfile\shell\open\command");
            if (key?.GetValue("") is not string cmd) return null;
            var m = Regex.Match(cmd, "^\\s*\"([^\"]+)\"|^\\s*(\\S+)");
            var exe = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (exe.EndsWith("blender-launcher.exe", StringComparison.OrdinalIgnoreCase))
                exe = Path.Combine(Path.GetDirectoryName(exe)!, "blender.exe");
            return exe;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var result = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            if (string.IsNullOrEmpty(steam)) return result;
            result.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    result.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }
        catch { }
        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
