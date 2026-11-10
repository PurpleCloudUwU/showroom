using RetroShowroom.Core;

namespace RetroShowroom;

/// <summary>Gemeinsamer Zustand der App (Einstellungen, letzte Einrichtungsprüfung, laufendes Rendern).</summary>
internal static class AppState
{
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static SetupReport? Setup { get; private set; }
    public static bool IsCheckingSetup { get; private set; }
    public static bool IsRendering { get; set; }
    public static Action? CancelRender { get; set; }
    public static List<string> StartupFiles { get; } = [];

    /// <summary>Wird ausgelöst, wenn sich Einrichtung, Assets oder Geräte-Einstellung ändern.</summary>
    public static event EventHandler? Changed;

    private static Task? _check;

    public static Task RefreshSetupAsync()
    {
        if (_check is { IsCompleted: false }) return _check;
        IsCheckingSetup = true;
        NotifyChanged();
        return _check = RunCheckAsync();
    }

    private static async Task RunCheckAsync()
    {
        try
        {
            Setup = await Task.Run(() => SetupChecker.RunAsync(Settings));
        }
        catch (Exception ex)
        {
            var r = new SetupReport { BlenderProbed = true, BlenderError = ex.Message };
            SetupChecker.UpdateAssets(r, Settings);
            Setup = r;
        }
        finally
        {
            IsCheckingSetup = false;
            NotifyChanged();
        }
    }

    /// <summary>Nur Assets neu einlesen (ohne Blender zu starten).</summary>
    public static void RefreshAssets()
    {
        if (Setup is not null) SetupChecker.UpdateAssets(Setup, Settings);
        NotifyChanged();
    }

    public static void SaveSettings()
    {
        try { Settings.Save(); } catch { }
    }

    public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);
}
