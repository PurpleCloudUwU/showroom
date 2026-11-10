using Microsoft.UI.Dispatching;
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
            // Direkt nach dem Start: sofort den Stand der letzten Prüfung zeigen, damit „Rendern starten“ nicht
            // 3 bis 7 Sekunden gesperrt ist. Die echte Prüfung läuft gleich danach und ersetzt ihn.
            if (Setup is null && await Task.Run(() => SetupCache.TryLoad(Settings)) is { } remembered && Setup is null)
            {
                Setup = remembered;
                NotifyChanged();
            }
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

    public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);

    // ---------------------------------------------------------------- Einstellungen speichern

    private static readonly object SaveLock = new();
    private static DispatcherQueueTimer? _saveTimer;
    private static bool _saveQueued;
    private static long _saveSequence, _savedSequence;

    /// <summary>
    /// Einstellungen speichern (aus dem UI-Thread). Mehrere Änderungen kurz hintereinander, etwa beim Halten der
    /// Pfeiltaste im Zahlenfeld, ergeben einen einzigen Schreibvorgang, und der läuft nicht im UI-Thread.
    /// Vor dem Beenden oder einem Neustart <see cref="FlushSettings"/> aufrufen.
    /// </summary>
    public static void SaveSettings()
    {
        _saveQueued = true;
        if (_saveTimer is null)
        {
            if (DispatcherQueue.GetForCurrentThread() is not { } dispatcher)
            {
                FlushSettings();
                return;
            }
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => WriteQueued(background: true);
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Noch nicht geschriebene Einstellungen sofort speichern.</summary>
    public static void FlushSettings()
    {
        _saveTimer?.Stop();
        WriteQueued(background: false);
    }

    private static void WriteQueued(bool background)
    {
        if (!_saveQueued) return;
        _saveQueued = false;

        string json;
        try { json = Settings.ToJson(); } catch { return; }
        long sequence = ++_saveSequence;

        if (background) Task.Run(Write);
        else Write();

        void Write()
        {
            lock (SaveLock)
            {
                // Ein älterer Stand darf einen neueren nicht überschreiben
                if (sequence < _savedSequence) return;
                try
                {
                    AppSettings.WriteJson(json);
                    _savedSequence = sequence;
                }
                catch { }
            }
        }
    }
}
