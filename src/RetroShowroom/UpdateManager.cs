using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RetroShowroom.Core;

namespace RetroShowroom;

internal enum UpdateStage
{
    /// <summary>Noch nicht geprüft.</summary>
    Idle,
    Checking,
    UpToDate,
    /// <summary>Neue Version gefunden, noch nicht geladen.</summary>
    Available,
    Downloading,
    /// <summary>Neue Version liegt geprüft im Update-Ordner.</summary>
    Ready,
    /// <summary>Der Installer ist gestartet, die App beendet sich gleich.</summary>
    Installing,
}

/// <summary>
/// Steuert die Updates: prüft nach Zeitplan bei GitHub, lädt neue Versionen und übergibt an den Installer
/// (<see cref="UpdateInstaller"/>). Alle Zustände werden im UI-Thread geändert, <see cref="Changed"/> kommt dort an.
/// </summary>
internal static class UpdateManager
{
    public static Version Current { get; } = UpdateClient.Normalize(typeof(UpdateManager).Assembly.GetName().Version);
    public static string CurrentText => Current.ToString(3);

    public static UpdateStage Stage { get; private set; }
    public static ReleaseInfo? Latest { get; private set; }
    /// <summary>Fortschritt des Downloads, 0 bis 1.</summary>
    public static double Progress { get; private set; }
    public static string? Error { get; private set; }
    /// <summary>Der Fehler stammt von einer Abfrage, die im Hintergrund lief (dann nur in den Einstellungen zeigen).</summary>
    public static bool ErrorIsQuiet { get; private set; }
    /// <summary>Der Nutzer hat Laden oder Installieren selbst angestoßen (dann zeigt das Hauptfenster den Fortschritt).</summary>
    public static bool UserInitiated { get; private set; }
    /// <summary>Die App wurde gerade erst von dieser älteren Version aktualisiert.</summary>
    public static string? UpdatedFrom { get; private set; }
    /// <summary>Der letzte Installationsversuch ist gescheitert (die App wurde unverändert neu gestartet).</summary>
    public static bool LastInstallFailed { get; private set; }

    /// <summary>
    /// Warum sich diese Kopie der App nicht selbst ersetzen kann (z. B. Ordner ohne Schreibrechte). Null = geht.
    /// Dann wird nichts automatisch geladen, die App verweist auf die Release-Seite.
    /// </summary>
    public static string? InstallProblem { get; private set; }

    public static bool Busy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Installing;

    /// <summary>Seite mit den Versionshinweisen. Es werden nur Adressen des eigenen Repositorys geöffnet.</summary>
    public static string NotesUrl =>
        Latest?.PageUrl is { } url && url.StartsWith(UpdateSource.GitHub.ReleasesPage + "/", StringComparison.Ordinal)
            ? url
            : UpdateSource.GitHub.LatestPage;

    public static event Action? Changed;

    private static readonly UpdateClient Client = new();
    private static string? _readyPath;
    private static CancellationTokenSource? _downloadCts;
    private static DispatcherQueueTimer? _timer;
    private static DateTime _lastAttemptUtc;
    private static bool _started;
    private static bool _handedOver;

    /// <summary>Beim Start der App, noch vor dem Fenster: Hat sich die Version geändert? Kam die App vom Installer zurück?</summary>
    public static void NoteStartup(IEnumerable<string> args)
    {
        LastInstallFailed = args.Contains(UpdateInstaller.FailedFlag);
        var s = AppState.Settings;
        if (UpdateClient.TryParseVersion(s.LastRunVersion, out var last) && last < Current)
            UpdatedFrom = last.ToString(3);
        if (s.LastRunVersion != CurrentText)
        {
            s.LastRunVersion = CurrentText;
            AppState.SaveSettings();
        }
    }

    /// <summary>Hinweis „wurde aktualisiert“ bzw. „Installation gescheitert“ wurde gezeigt.</summary>
    public static void DismissStartupNotice()
    {
        UpdatedFrom = null;
        LastInstallFailed = false;
    }

    /// <summary>Nach dem Öffnen des Fensters: liegen gebliebenes Update übernehmen, nach Zeitplan prüfen, aufräumen.</summary>
    public static async void Start(DispatcherQueue dispatcher)
    {
        if (_started) return;
        _started = true;
        bool justUpdated = UpdatedFrom is not null;

        // Solange die App offen ist, alle 30 Minuten nachsehen, ob eine Abfrage fällig ist
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(30);
        _timer.Tick += async (_, _) => await AutoCheckAsync(atStart: false);
        _timer.Start();

        try
        {
            InstallProblem = await Task.Run(() => UpdateInstaller.CheckTarget(Environment.ProcessPath));
            await RestoreReadyAsync();
            // Den Start der App nicht ausbremsen
            await Task.Delay(TimeSpan.FromSeconds(3));
            await AutoCheckAsync(atStart: true);
            _ = Task.Run(() =>
            {
                UpdateStore.Cleanup(Current);
                if (justUpdated) UpdateStore.RemoveOldUnpackedVersions();
            });
        }
        catch { }
    }

    private static void Notify() => Changed?.Invoke();

    /// <summary>Ein Download aus einer früheren Sitzung, der noch nicht installiert wurde.</summary>
    private static async Task RestoreReadyAsync()
    {
        var found = await Task.Run(async () =>
        {
            var release = UpdateStore.LoadReady(Current);
            if (release is null) return null;
            return await UpdateClient.VerifyFileAsync(UpdateStore.PathFor(release), release) ? release : null;
        });
        if (found is null || Stage != UpdateStage.Idle) return;
        Latest = found;
        _readyPath = UpdateStore.PathFor(found);
        Stage = UpdateStage.Ready;
        Notify();
    }

    private static async Task AutoCheckAsync(bool atStart)
    {
        var s = AppState.Settings;
        if (Busy || !UpdateSchedule.IsDue(s.UpdateCheck, s.LastUpdateCheck, DateTime.UtcNow, atStart)) return;
        // Nach einem Fehlschlag (z. B. offline) nicht alle 30 Minuten erneut fragen
        if (!atStart && DateTime.UtcNow - _lastAttemptUtc < TimeSpan.FromHours(2)) return;
        await CheckAsync(manual: false);
    }

    /// <summary>Fragt GitHub nach der neuesten Version. Ist „automatisch installieren“ an, beginnt danach der Download.</summary>
    public static async Task CheckAsync(bool manual)
    {
        if (Busy) return;
        var before = Stage;
        Stage = UpdateStage.Checking;
        Error = null;
        Notify();
        _lastAttemptUtc = DateTime.UtcNow;

        try
        {
            // Im Hintergrund-Thread: Der erste Aufruf lädt die Netzwerk-Bibliotheken, das soll die Oberfläche nicht merken
            var release = await Task.Run(() => Client.GetLatestAsync());
            AppState.Settings.LastUpdateCheck = DateTime.UtcNow;
            AppState.SaveSettings();

            if (release is null || release.Version <= Current)
            {
                Latest = null;
                _readyPath = null;
                Stage = UpdateStage.UpToDate;
            }
            else if (before == UpdateStage.Ready && _readyPath is not null && Latest?.Version == release.Version)
            {
                Latest = release;
                Stage = UpdateStage.Ready;
            }
            else
            {
                Latest = release;
                _readyPath = null;
                Stage = UpdateStage.Available;
            }
        }
        catch (Exception ex)
        {
            // Der vorherige Stand bleibt gültig (z. B. ein schon geladenes Update)
            Stage = before == UpdateStage.Checking ? UpdateStage.Idle : before;
            Error = Loc.T("Die Suche nach Updates ist fehlgeschlagen: ", "Checking for updates failed: ") + ex.Message;
            ErrorIsQuiet = !manual;
        }
        Notify();

        if (Stage == UpdateStage.Available && AppState.Settings.AutoUpdate && InstallProblem is null)
            await DownloadAsync(userInitiated: false);
    }

    /// <summary>Lädt die gefundene Version in den Update-Ordner.</summary>
    public static async Task DownloadAsync(bool userInitiated)
    {
        if (Latest is not { } release || Stage != UpdateStage.Available) return;
        UserInitiated = userInitiated;
        Stage = UpdateStage.Downloading;
        Progress = 0;
        Error = null;
        Notify();

        var cts = _downloadCts = new CancellationTokenSource();
        try
        {
            // Progress<T> meldet im UI-Thread (der Kern drosselt auf wenige Meldungen pro Sekunde)
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (Stage != UpdateStage.Downloading) return;
                Progress = p.Fraction;
                Notify();
            });
            _readyPath = await Task.Run(() => Client.DownloadAsync(release, progress, cts.Token));
            Stage = UpdateStage.Ready;
        }
        catch (OperationCanceledException)
        {
            Stage = UpdateStage.Available;
        }
        catch (Exception ex)
        {
            Stage = UpdateStage.Available;
            Error = Loc.T("Der Download ist fehlgeschlagen: ", "The download failed: ") + ex.Message;
            ErrorIsQuiet = !userInitiated;
        }
        finally
        {
            _downloadCts = null;
            cts.Dispose();
        }
        Notify();
    }

    public static void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { }
    }

    /// <summary>Knopf „Aktualisieren“: laden (falls nötig), installieren und die App neu starten.</summary>
    public static async Task UpdateNowAsync()
    {
        if (InstallProblem is not null)
        {
            // Diese Kopie kann sich nicht selbst ersetzen: zur Release-Seite
            if (Stage is UpdateStage.Available or UpdateStage.Ready) Ui.OpenUrl(NotesUrl);
            return;
        }
        if (Stage == UpdateStage.Available) await DownloadAsync(userInitiated: true);
        if (Stage == UpdateStage.Ready) await InstallAndRestartAsync();
    }

    /// <summary>Wurde „automatisch installieren“ eingeschaltet, während ein Update schon gefunden war: jetzt laden.</summary>
    public static async Task ApplySettingsAsync()
    {
        Notify();
        if (Stage == UpdateStage.Available && AppState.Settings.AutoUpdate && InstallProblem is null && Error is null)
            await DownloadAsync(userInitiated: false);
    }

    private static void Fail(string message)
    {
        Error = message;
        ErrorIsQuiet = false;
        Notify();
    }

    private static async Task InstallAndRestartAsync()
    {
        if (Stage != UpdateStage.Ready || _readyPath is not { } newExe) return;
        if (AppState.IsRendering)
        {
            Fail(Loc.T("Es wird noch gerendert. Bitte warten, bis das Rendern fertig ist, oder es abbrechen.",
                       "Rendering is still running. Please wait until it has finished, or cancel it."));
            return;
        }
        var target = Environment.ProcessPath;
        if (UpdateInstaller.CheckTarget(target) is { } problem)
        {
            Fail(problem);
            return;
        }

        UserInitiated = true;
        Stage = UpdateStage.Installing;
        Error = null;
        Notify();
        AppState.FlushSettings();

        try
        {
            using var handoff = await Task.Run(() => UpdateInstaller.Launch(newExe, target!, restart: true));
            // Die neue .exe entpackt beim ersten Start erst ihre nativen Teile. Diese App bleibt so lange sichtbar.
            if (!await handoff.WaitReadyAsync(TimeSpan.FromMinutes(3)))
            {
                try { handoff.Process.Kill(); } catch { }
                Stage = UpdateStage.Ready;
                Fail(Loc.T("Die neue Version ließ sich nicht starten. Details stehen in update.log im Datenordner.",
                           "The new version could not be started. Details are in update.log in the data folder."));
                return;
            }
            _handedOver = true;
            AppState.CancelRender?.Invoke();
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Stage = UpdateStage.Ready;
            Fail(Loc.T("Das Update konnte nicht gestartet werden: ", "The update could not be started: ") + ex.Message);
        }
    }

    /// <summary>
    /// Beim Schließen der App: Liegt ein Update bereit und ist „automatisch installieren“ an, übernimmt der Installer.
    /// Er wartet, bis diese App beendet ist, und ersetzt dann die .exe (ohne Neustart).
    /// </summary>
    public static void InstallOnExit()
    {
        if (_handedOver || Stage != UpdateStage.Ready || _readyPath is not { } newExe || !AppState.Settings.AutoUpdate) return;
        var target = Environment.ProcessPath;
        if (UpdateInstaller.CheckTarget(target) is not null) return;
        try
        {
            UpdateInstaller.Launch(newExe, target!, restart: false).Dispose();
            _handedOver = true;
        }
        catch (Exception ex)
        {
            UpdateInstaller.Log("Installation beim Schließen nicht gestartet: " + ex.Message);
        }
    }
}
