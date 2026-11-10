using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using RetroShowroom.Core;

namespace RetroShowroom.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        AppState.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateBlenderText);
    }

    private async Task RefreshAsync()
    {
        var s = AppState.Settings;

        _loading = true;
        LanguageCombo.SelectedIndex = Loc.English ? 1 : 0;
        GpuToggle.IsOn = s.UseGpu;
        _loading = false;

        var logo = AssetStore.Resolve(AssetKind.Logo, s.LogoAsset);
        LogoText.Text = Describe(logo, Loc.T("Noch nicht hinzugefügt. Kommt an die Rückwand und auf die Sockel.",
                                             "Not added yet. Appears on the back wall and on the bases."));
        RemoveLogoButton.IsEnabled = logo is not null;
        LogoThumb.Source = logo is null ? null : await SafeBitmap(logo, 88);
        LogoPlaceholder.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;

        var reference = AssetStore.Resolve(AssetKind.Reference, s.ReferenceAsset);
        RefText.Text = Describe(reference, Loc.T("Optional. Wird in der Vorschau zum Vergleichen angezeigt.",
                                                 "Optional. Shown in the preview for comparison."));
        RemoveRefButton.IsEnabled = reference is not null;
        RefThumb.Source = reference is null ? null : await SafeBitmap(reference, 88);
        RefPlaceholder.Visibility = reference is null ? Visibility.Visible : Visibility.Collapsed;

        UpdateBlenderText();
        OutputText.Text = s.EffectiveOutputFolder;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Retro Showroom {v?.ToString(3)}";
        DataText.Text = Loc.T("Datenordner: ", "Data folder: ") + AppPaths.DataDir;
        AboutLogo.Source ??= await Ui.LoadAppLogoAsync(64);
    }

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
        Process.Start(new ProcessStartInfo(exe, "--seite=einstellungen") { UseShellExecute = true });
        Application.Current.Exit();
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
