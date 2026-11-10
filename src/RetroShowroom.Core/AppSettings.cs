using System.Text.Json;
using System.Text.Json.Serialization;

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

    // Updates
    /// <summary>Wie oft nach einer neuen Version gesucht wird (siehe <see cref="UpdateSchedule"/>).</summary>
    public string UpdateCheck { get; set; } = UpdateSchedule.Daily;
    /// <summary>Neue Versionen im Hintergrund laden und beim Schließen der App installieren. false = vorher fragen.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Zeitpunkt der letzten erfolgreichen Abfrage (UTC).</summary>
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>Version beim letzten Start (für den Hinweis „wurde aktualisiert“).</summary>
    public string? LastRunVersion { get; set; }

    public static string DefaultOutputFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Retro Showrooms");

    [JsonIgnore]
    public string EffectiveOutputFolder =>
        string.IsNullOrWhiteSpace(OutputFolder) ? DefaultOutputFolder : OutputFolder;

    private static readonly object WriteLock = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SettingsFile), CoreJsonContext.Default.AppSettings) ?? new();
        }
        catch
        {
            // Kaputte Datei: mit Standardwerten weitermachen, alte Datei sichern.
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".defekt", true); } catch { }
        }
        return new();
    }

    public string ToJson() => JsonSerializer.Serialize(this, CoreJsonContext.Default.AppSettings);

    public void Save() => WriteJson(ToJson());

    /// <summary>Schreibt bereits serialisierte Einstellungen (darf aus einem Hintergrund-Thread kommen).</summary>
    public static void WriteJson(string json)
    {
        lock (WriteLock)
        {
            AppPaths.Ensure(AppPaths.DataDir);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
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

/// <summary>
/// JSON ohne Reflexion (vom Compiler erzeugt). Das spart beim Start das Aufwärmen des Serialisierers:
/// Die Einstellungen werden gelesen, bevor das Fenster erscheint.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ReleaseInfo))]
[JsonSerializable(typeof(SetupSnapshot))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
