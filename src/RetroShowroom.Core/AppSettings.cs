using System.Text.Json;

namespace RetroShowroom.Core;

/// <summary>Einstellungen der App, gespeichert als JSON im Datenordner.</summary>
public sealed class AppSettings
{
    /// <summary>„de“ (Standard) oder „en“.</summary>
    public string Language { get; set; } = Loc.German;

    public string? BlenderPath { get; set; }
    public string? OutputFolder { get; set; }

    /// <summary>Dateinamen der Assets im Assets-Ordner (siehe <see cref="AssetStore"/>).</summary>
    public string? LogoAsset { get; set; }
    public string? ReferenceAsset { get; set; }

    // Zuletzt verwendete Render-Optionen
    public string Quality { get; set; } = RenderDefaults.Quality;
    public string Style { get; set; } = RenderDefaults.Style;
    public bool Spots { get; set; } = RenderDefaults.Spots;
    public double SpotWatt { get; set; } = RenderDefaults.SpotWatt;
    public bool Kontur { get; set; } = RenderDefaults.Kontur;
    public string? LastPartFolder { get; set; }

    /// <summary>Grafikkarte zum Rendern verwenden (Rückfall auf CPU bei Fehlern). false = nur CPU.</summary>
    public bool UseGpu { get; set; } = true;

    public static string DefaultOutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Retro Showrooms");

    public string EffectiveOutputFolder =>
        string.IsNullOrWhiteSpace(OutputFolder) ? DefaultOutputFolder : OutputFolder;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new();
        }
        catch
        {
            // Kaputte Datei: mit Standardwerten weitermachen, alte Datei sichern.
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".defekt", true); } catch { }
        }
        return new();
    }

    public void Save()
    {
        AppPaths.Ensure(AppPaths.DataDir);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, AppPaths.SettingsFile, true);
    }
}

/// <summary>Abgesegneter Standard (siehe Prompt „Glasvitrine“).</summary>
public static class RenderDefaults
{
    public const string Quality = "FINAL";
    public const string Style = "weiss";
    public const bool Spots = true;
    public const double SpotWatt = 250;
    public const bool Kontur = true;
}
