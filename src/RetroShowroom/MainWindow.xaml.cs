using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using RetroShowroom.Core;
using RetroShowroom.Pages;
using Windows.Graphics;

namespace RetroShowroom;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private string _pendingTag;
    private string? _renderStatus;

    // Hinweisleiste für Updates
    private string? _updateBarKey;
    private string? _updateBarDismissed;
    private Action? _updateBarAction;

    public MainWindow(string? startPage)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetLogo();
        SizeAndCenter(1400, 900, 1120, 740);
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) =>
        {
            AppState.CancelRender?.Invoke();
            AppState.FlushSettings();
            // Liegt ein Update bereit, ersetzt der Installer die .exe, sobald diese App beendet ist
            UpdateManager.InstallOnExit();
        };

        AppState.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateBadge);
        UpdateManager.Changed += RefreshUpdateBar;

        _pendingTag = startPage switch
        {
            "einrichtung" => "setup",
            "einstellungen" => "settings",
            _ => "render",
        };
        ContentFrame.Navigate(PageFor(_pendingTag));
        RefreshUpdateBar();
        _ = AppState.RefreshSetupAsync();
        UpdateManager.Start(DispatcherQueue);
    }

    private void Nav_Loaded(object sender, RoutedEventArgs e) => Navigate(_pendingTag);

    /// <summary>Retro-Sechseck als Fenstersymbol (Taskleiste, Alt+Tab) und in der Titelleiste.</summary>
    private async void SetLogo()
    {
        try
        {
            var icon = Ui.LoadExeIcon();
            if (icon != 0) AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(icon));
        }
        catch { }
        TitleLogo.Source = await Ui.LoadAppLogoAsync(40);
    }

    public void Navigate(string tag)
    {
        _pendingTag = tag;
        if (tag == "settings")
        {
            if (Nav.SettingsItem is not null) Nav.SelectedItem = Nav.SettingsItem;
            else ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == tag);
    }

    private static Type PageFor(string tag) => tag switch
    {
        "setup" => typeof(SetupPage),
        "settings" => typeof(SettingsPage),
        _ => typeof(RenderPage),
    };

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "render";
        var page = PageFor(tag);
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }

    private void UpdateBadge()
    {
        var open = AppState.Setup?.OpenPoints ?? 0;
        SetupBadge.Visibility = open > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetupBadge.Value = open;
    }

    /// <summary>Fortschritt in Titelleiste und Taskleiste anzeigen (null = nichts).</summary>
    public void SetRenderStatus(string? status)
    {
        // Wird beim Rendern alle 400 ms aufgerufen: Fenstertitel nur anfassen, wenn sich der Text ändert
        if (status == _renderStatus) return;
        _renderStatus = status;
        TitleStatus.Text = status is null ? "" : "·  " + status;
        Title = status is null ? "Retro Showroom" : $"{status} · Retro Showroom";
    }

    // ---------------------------------------------------------------- Updates

    /// <summary>Zeigt über jeder Seite an, was es zu Updates zu sagen gibt (oder nichts).</summary>
    private void RefreshUpdateBar()
    {
        var stage = UpdateManager.Stage;
        var v = UpdateManager.Latest?.Version.ToString(3);
        var current = UpdateManager.CurrentText;
        bool auto = AppState.Settings.AutoUpdate;
        var problem = UpdateManager.InstallProblem;

        string? key = null, title = null, message = null, button = null;
        var severity = InfoBarSeverity.Informational;
        bool closable = true, progress = false;
        Action? action = null;

        if (UpdateManager.Error is { } error && !UpdateManager.ErrorIsQuiet && stage is UpdateStage.Available or UpdateStage.Ready)
        {
            key = "fehler|" + error;
            severity = InfoBarSeverity.Error;
            title = Loc.T("Update fehlgeschlagen", "Update failed");
            message = error;
            button = Loc.T("Erneut versuchen", "Try again");
            action = UpdateNow;
        }
        else if (stage == UpdateStage.Installing)
        {
            key = "installieren";
            title = Loc.T($"Version {v} wird installiert", $"Installing version {v}");
            message = Loc.T("Die App startet gleich neu. Beim ersten Start einer neuen Version dauert das einmalig etwa 20 Sekunden.",
                            "The app will restart shortly. The first start of a new version takes about 20 seconds, once.");
            closable = false;
        }
        else if (stage == UpdateStage.Downloading && UpdateManager.UserInitiated)
        {
            key = "laden";
            title = Loc.T($"Version {v} wird heruntergeladen", $"Downloading version {v}");
            message = $"{UpdateManager.Progress * 100:0} %";
            closable = false;
            progress = true;
        }
        else if (stage is UpdateStage.Available or UpdateStage.Ready && problem is not null)
        {
            key = "manuell|" + v;
            severity = InfoBarSeverity.Warning;
            title = Loc.T($"Version {v} ist verfügbar", $"Version {v} is available");
            message = problem;
            button = Loc.T("Release-Seite öffnen", "Open release page");
            action = UpdateNow;
        }
        else if (stage == UpdateStage.Ready)
        {
            key = $"bereit|{v}|{auto}";
            severity = InfoBarSeverity.Success;
            title = Loc.T($"Version {v} ist bereit", $"Version {v} is ready");
            message = auto
                ? Loc.T("Sie wird beim Schließen der App installiert.", "It will be installed when you close the app.")
                : Loc.T("Zum Installieren startet die App neu.", "The app restarts to install it.");
            button = auto ? Loc.T("Jetzt neu starten", "Restart now") : Loc.T("Jetzt installieren", "Install now");
            action = UpdateNow;
        }
        else if (stage == UpdateStage.Available && !auto)
        {
            key = "neu|" + v;
            title = Loc.T($"Version {v} ist verfügbar", $"Version {v} is available");
            message = Loc.T($"Installiert ist Version {current}.", $"Version {current} is installed.");
            button = Loc.T("Aktualisieren", "Update");
            action = UpdateNow;
        }
        else if (UpdateManager.LastInstallFailed)
        {
            key = "start-fehler";
            severity = InfoBarSeverity.Error;
            title = Loc.T("Das Update konnte nicht installiert werden", "The update could not be installed");
            message = Loc.T("Die App ist unverändert. Details stehen in update.log im Datenordner.",
                            "The app is unchanged. Details are in update.log in the data folder.");
            button = Loc.T("Release-Seite öffnen", "Open release page");
            action = OpenReleasePage;
        }
        else if (UpdateManager.UpdatedFrom is { } from)
        {
            key = "aktualisiert";
            severity = InfoBarSeverity.Success;
            title = Loc.T("Aktualisiert", "Updated");
            message = Loc.T($"Retro Showroom wurde von Version {from} auf {current} aktualisiert.",
                            $"Retro Showroom was updated from version {from} to {current}.");
            button = Loc.T("Versionshinweise", "Release notes");
            action = OpenReleasePage;
        }

        _updateBarKey = key;
        _updateBarAction = action;
        if (key is null || key == _updateBarDismissed)
        {
            UpdateBar.IsOpen = false;
            UpdateBar.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBar.Severity = severity;
        UpdateBar.Title = title;
        UpdateBar.Message = message;
        UpdateBar.IsClosable = closable;
        UpdateBarButton.Content = button;
        UpdateBarButton.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateBarProgress.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
        if (progress) UpdateBarProgress.Value = UpdateManager.Progress * 100;
        UpdateBar.Visibility = Visibility.Visible;
        UpdateBar.IsOpen = true;
    }

    private static async void UpdateNow() => await UpdateManager.UpdateNowAsync();

    private void OpenReleasePage()
    {
        Ui.OpenUrl(UpdateManager.NotesUrl);
        UpdateManager.DismissStartupNotice();
        RefreshUpdateBar();
    }

    private void UpdateBarButton_Click(object sender, RoutedEventArgs e) => _updateBarAction?.Invoke();

    private void UpdateBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason != InfoBarCloseReason.CloseButton) return;
        // Diesen Hinweis bis zur nächsten Änderung nicht wieder aufklappen
        _updateBarDismissed = _updateBarKey;
        UpdateBar.Visibility = Visibility.Collapsed;
        if (_updateBarKey is "aktualisiert" or "start-fehler") UpdateManager.DismissStartupNotice();
    }

    private void SizeAndCenter(int width, int height, int minWidth, int minHeight)
    {
        double scale = Ui.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(width * scale), area.Width - 32);
        int h = Math.Min((int)(height * scale), area.Height - 32);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = Math.Min((int)(minWidth * scale), area.Width);
            p.PreferredMinimumHeight = Math.Min((int)(minHeight * scale), area.Height);
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || !AppState.IsRendering) return;
        args.Cancel = true;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = Loc.T("Es wird noch gerendert", "Rendering is still running"),
            Content = Loc.T("Wenn du die App jetzt schließt, wird das Rendern abgebrochen.", "If you close the app now, rendering will be cancelled."),
            PrimaryButtonText = Loc.T("Abbrechen und schließen", "Cancel and close"),
            CloseButtonText = Loc.T("Weiter rendern", "Keep rendering"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            AppState.CancelRender?.Invoke();
            _allowClose = true;
            Close();
        }
    }
}
