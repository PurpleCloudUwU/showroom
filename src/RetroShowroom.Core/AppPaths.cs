namespace RetroShowroom.Core;

/// <summary>Ordner, in denen die App ihre Daten ablegt (%LOCALAPPDATA%\RetroShowroom).</summary>
public static class AppPaths
{
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("RETROSHOWROOM_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetroShowroom");

    public static string AssetsDir => Path.Combine(DataDir, "Assets");
    public static string CacheDir => Path.Combine(DataDir, "Cache");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
