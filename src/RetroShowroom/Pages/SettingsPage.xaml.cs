using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using RetroShowroom.Core;

namespace RetroShowroom.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading;
    // Welche Vorschaubilder gerade angezeigt werden (Pfad und Änderungsdatum), damit sie nicht bei jedem Öffnen neu dekodiert werden
    private string? _logoShown, _referenceShown;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        AppState.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateBlenderText);
        UpdateManager.Changed += RefreshUpdates;
    }

    private async Task RefreshAsync()
    {
        var s = AppState.Settings;

        _loading = true;
        LanguageCombo.SelectedIndex = Loc.English ? 1 : 0;
        GpuToggle.IsOn = s.UseGpu;
        _loading = false;
        RefreshUpdates();

        var logo = AssetStore.Resolve(AssetKind.Logo, s.LogoAsset);
        LogoText.Text = Describe(logo, Loc.T("Noch nicht hinzugefügt. Kommt an die Rückwand und auf die Sockel.",
                                             "Not added yet. Appears on the back wall and on the bases."));
        RemoveLogoButton.IsEnabled = logo is not null;
        if (Stamp(logo) is var logoStamp && logoStamp != _logoShown)
        {
            _logoShown = logoStamp;
            LogoThumb.Source = logo is null ? null : await SafeBitmap(logo, 88);
        }
        LogoPlaceholder.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;

        var reference = AssetStore.Resolve(AssetKind.Reference, s.ReferenceAsset);
        RefText.Text = Describe(reference, Loc.T("Optional. Wird in der Vorschau zum Vergleichen angezeigt.",
                                                 "Optional. Shown in the preview for comparison."));
        RemoveRefButton.IsEnabled = reference is not null;
        if (Stamp(reference) is var referenceStamp && referenceStamp != _referenceShown)
        {
            _referenceShown = referenceStamp;
            RefThumb.Source = reference is null ? null : await SafeBitmap(reference, 88);
        }
        RefPlaceholder.Visibility = reference is null ? Visibility.Visible : Visibility.Collapsed;

        UpdateBlenderText();
        OutputText.Text = s.EffectiveOutputFolder;
        VersionText.Text = $"Retro Showroom {UpdateManager.CurrentText}";
        DataText.Text = Loc.T("Datenordner: ", "Data folder: ") + AppPaths.DataDir;
        AboutLogo.Source ??= await Ui.LoadAppLogoAsync(64);
    }

    private static string? Stamp(string? path) =>
        path is null ? null : $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";

    private static async Task<BitmapImage?> SafeBitmap(string path, int width)
    {
        try { return await Ui.LoadBitmapAsync(path, width); } catch { return null; }
    }

    private void UpdateBlenderText()
    {
        var s = AppState.Settings;
        var found = AppState.Setup?.BlenderPath;
        BlenderText.Text = !string.IsNullOrWhiteSpace(s.BlenderPath)
            ? s.BlenderPath
            : found is not null
                ? Loc.T("Automatisch gefunden: ", "Detected automatically: ") + found
                : Loc.T("Automatisch (noch nicht gefunden)", "Automatic (not found yet)");
    }

    private static string Describe(string? path, string empty)
    {
        if (path is null) return empty;
        var t = File.GetLastWriteTime(path);
        return Path.GetFileName(path) + Loc.T($" · Datei vom {t:dd.MM.yyyy, HH:mm} Uhr", $" · file from {t:yyyy-MM-dd HH:mm}");
    }

    // ---------------------------------------------------------------- Sprache

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string lang }) return;
        AppState.Settings.Language = lang;
        AppState.SaveSettings();

        if (lang == Loc.Language)
        {
            SettingsInfoBar.IsOpen = false;
            return;
        }

        // Hinweis in der neu gewählten Sprache
        bool en = lang == Loc.EnglishCode;
        var restart = new Button { Content = en ? "Restart now" : "Jetzt neu starten" };
        restart.Click += (_, _) => Restart();
        SettingsInfoBar.Severity = InfoBarSeverity.Informational;
        SettingsInfoBar.Title = en ? "Language changed" : "Sprache geändert";
        SettingsInfoBar.Message = en
            ? "The app switches to English after a restart."
            : "Die App wechselt nach einem Neustart auf Deutsch.";
        SettingsInfoBar.ActionButton = restart;
        SettingsInfoBar.IsOpen = true;
    }

    private void Restart()
    {
        if (AppState.IsRendering)
        {
            ShowInfo(InfoBarSeverity.Warning, Loc.T("Es wird noch gerendert", "Rendering is still running"),
                Loc.T("Bitte warten, bis das Rendern fertig ist, oder es abbrechen.", "Please wait until rendering has finished, or cancel it."));
            return;
        }
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        // Die neue Instanz liest die Einstellungen sofort: vorher sicher schreiben
        AppState.FlushSettings();
        Process.Start(new ProcessStartInfo(exe, "--seite=einstellungen") { UseShellExecute = true });
        Application.Current.Exit();
    }

    // ---------------------------------------------------------------- Updates

    private void RefreshUpdates()
    {
        var s = AppState.Settings;
        _loading = true;
        var mode = UpdateSchedule.Normalize(s.UpdateCheck);
        UpdateCheckCombo.SelectedItem = UpdateCheckCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == mode);
        AutoUpdateToggle.IsOn = s.AutoUpdate;
        _loading = false;

        var stage = UpdateManager.Stage;
        var release = UpdateManager.Latest;
        var v = release?.Version.ToString(3);
        var problem = UpdateManager.InstallProblem;
        int percent = (int)Math.Round(UpdateManager.Progress * 100);

        string title;
        string? action = null;
        var detail = Loc.T($"Installiert ist Version {UpdateManager.CurrentText}.", $"Version {UpdateManager.CurrentText} is installed.");
        if (s.LastUpdateCheck is { } last)
            detail += Loc.T($" Zuletzt geprüft am {last.ToLocalTime():dd.MM.yyyy} um {last.ToLocalTime():HH:mm} Uhr.",
                            $" Last checked on {last.ToLocalTime():yyyy-MM-dd} at {last.ToLocalTime():HH:mm}.");

        switch (stage)
        {
            case UpdateStage.Checking:
                title = Loc.T("Suche nach Updates …", "Checking for updates …");
                break;
            case UpdateStage.UpToDate:
                title = Loc.T("Retro Showroom ist aktuell", "Retro Showroom is up to date");
                break;
            case UpdateStage.Available:
                title = Loc.T($"Version {v} ist verfügbar", $"Version {v} is available");
                if (release is { Size: > 0 })
                    detail = Loc.T($"Download {release.Size / 1048576.0:0} MB. ", $"Download {release.Size / 1048576.0:0} MB. ") + detail;
                action = problem is null ? Loc.T("Herunterladen und installieren", "Download and install") : Loc.T("Release-Seite öffnen", "Open release page");
                break;
            case UpdateStage.Downloading:
                title = Loc.T($"Version {v} wird heruntergeladen … {percent} %", $"Downloading version {v} … {percent}%");
                break;
            case UpdateStage.Ready:
                title = Loc.T($"Version {v} ist bereit", $"Version {v} is ready");
                detail = s.AutoUpdate
                    ? Loc.T("Sie wird beim Schließen der App installiert.", "It will be installed when you close the app.")
                    : Loc.T("Zum Installieren startet die App neu.", "The app restarts to install it.");
                action = problem is not null ? Loc.T("Release-Seite öffnen", "Open release page")
                    : s.AutoUpdate ? Loc.T("Jetzt neu starten", "Restart now") : Loc.T("Jetzt installieren", "Install now");
                break;
            case UpdateStage.Installing:
                title = Loc.T($"Version {v} wird installiert …", $"Installing version {v} …");
                detail = Loc.T("Die App startet gleich neu. Beim ersten Start einer neuen Version dauert das einmalig etwa 20 Sekunden.",
                               "The app will restart shortly. The first start of a new version takes about 20 seconds, once.");
                break;
            default:
                title = Loc.T("Noch nicht nach Updates gesucht", "Not checked for updates yet");
                break;
        }

        bool failed = UpdateManager.Error is not null;
        if (failed) detail = UpdateManager.Error!;
        else if (problem is not null && stage is UpdateStage.Available or UpdateStage.Ready) detail = problem;

        UpdateTitle.Text = title;
        UpdateDetail.Text = detail;
        UpdateDetail.Foreground = Ui.Brush(failed ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        UpdateProgress.Visibility = stage == UpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = percent;
        UpdateActionButton.Content = action;
        UpdateActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateCancelButton.Visibility = stage == UpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotesLink.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateCheckButton.IsEnabled = !UpdateManager.Busy;
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) => await UpdateManager.CheckAsync(manual: true);
    private async void UpdateAction_Click(object sender, RoutedEventArgs e) => await UpdateManager.UpdateNowAsync();
    private void UpdateCancel_Click(object sender, RoutedEventArgs e) => UpdateManager.CancelDownload();
    private void UpdateNotes_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl(UpdateManager.NotesUrl);

    private void UpdateCheckCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UpdateCheckCombo.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        AppState.Settings.UpdateCheck = mode;
        AppState.SaveSettings();
    }

    private async void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.AutoUpdate = AutoUpdateToggle.IsOn;
        AppState.SaveSettings();
        await UpdateManager.ApplySettingsAsync();
    }

    // ---------------------------------------------------------------- Assets

    private async Task ImportAsync(AssetKind kind, params string[] extensions)
    {
        var file = await Ui.PickFileAsync(extensions);
        if (file is null) return;
        try
        {
            var warning = AssetStore.Validate(kind, file);
            if (AssetStore.IsHardError(kind, file))
            {
                ShowInfo(InfoBarSeverity.Error, Loc.T("Datei nicht übernommen", "File not added"),
                    warning ?? Loc.T("Die Datei ist ungültig.", "The file is invalid."));
                return;
            }
            var name = AssetStore.Import(kind, file);
            var s = AppState.Settings;
            switch (kind)
            {
                case AssetKind.Logo: s.LogoAsset = name; break;
                case AssetKind.Reference: s.ReferenceAsset = name; break;
            }
            AppState.SaveSettings();
            AppState.RefreshAssets();
            if (warning is null)
                ShowInfo(InfoBarSeverity.Success, Loc.T("Übernommen", "Added"), Loc.T($"{name} wurde hinzugefügt.", $"{name} was added."));
            else
                ShowInfo(InfoBarSeverity.Warning, Loc.T("Übernommen, bitte prüfen", "Added, please check"), warning);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, Loc.T("Datei konnte nicht übernommen werden", "File could not be added"), ex.Message);
        }
        await RefreshAsync();
    }

    private async Task RemoveAsync(AssetKind kind)
    {
        try
        {
            AssetStore.Remove(kind);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, Loc.T("Entfernen fehlgeschlagen", "Removing failed"), ex.Message);
            return;
        }
        var s = AppState.Settings;
        switch (kind)
        {
            case AssetKind.Logo: s.LogoAsset = null; break;
            case AssetKind.Reference: s.ReferenceAsset = null; break;
        }
        AppState.SaveSettings();
        AppState.RefreshAssets();
        ShowInfo(InfoBarSeverity.Informational, Loc.T("Entfernt", "Removed"),
            Loc.T("Die Kopie im Datenordner wurde gelöscht. Das Original ist unverändert.", "The copy in the data folder was deleted. The original is unchanged."));
        await RefreshAsync();
    }

    private async void PickLogo_Click(object sender, RoutedEventArgs e) => await ImportAsync(AssetKind.Logo, ".png");
    private async void PickReference_Click(object sender, RoutedEventArgs e) => await ImportAsync(AssetKind.Reference, ".png", ".jpg", ".jpeg");
    private async void RemoveLogo_Click(object sender, RoutedEventArgs e) => await RemoveAsync(AssetKind.Logo);
    private async void RemoveReference_Click(object sender, RoutedEventArgs e) => await RemoveAsync(AssetKind.Reference);

    // ---------------------------------------------------------------- Rendern

    private void GpuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.UseGpu = GpuToggle.IsOn;
        AppState.SaveSettings();
        AppState.NotifyChanged();
    }

    private async void PickBlender_Click(object sender, RoutedEventArgs e)
    {
        var file = await Ui.PickFileAsync(".exe");
        if (file is null) return;
        if (!Path.GetFileName(file).Equals("blender.exe", StringComparison.OrdinalIgnoreCase))
        {
            ShowInfo(InfoBarSeverity.Warning, Loc.T("Falsche Datei", "Wrong file"),
                Loc.T("Bitte die Datei blender.exe im Blender-Ordner auswählen.", "Please choose blender.exe in the Blender folder."));
            return;
        }
        AppState.Settings.BlenderPath = file;
        AppState.SaveSettings();
        UpdateBlenderText();
        ShowInfo(InfoBarSeverity.Informational, Loc.T("Blender-Pfad gespeichert", "Blender path saved"),
            Loc.T("Die Einrichtung wird neu geprüft.", "Setup is being checked again."));
        await AppState.RefreshSetupAsync();
    }

    private async void AutoBlender_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.BlenderPath = null;
        AppState.SaveSettings();
        UpdateBlenderText();
        await AppState.RefreshSetupAsync();
        UpdateBlenderText();
    }

    private async void PickOutput_Click(object sender, RoutedEventArgs e)
    {
        var folder = await Ui.PickFolderAsync();
        if (folder is null) return;
        AppState.Settings.OutputFolder = folder;
        AppState.SaveSettings();
        AppState.NotifyChanged();
        OutputText.Text = folder;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Ui.OpenPath(AppPaths.Ensure(AppState.Settings.EffectiveOutputFolder));
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, Loc.T("Ordner nicht verfügbar", "Folder not available"), ex.Message);
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Ui.OpenPath(AppPaths.Ensure(AppPaths.DataDir));

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        SettingsInfoBar.ActionButton = null;
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.Title = title;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }
}
