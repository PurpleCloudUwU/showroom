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

    public MainWindow(string? startPage)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetLogo();
        SizeAndCenter(1400, 900, 1120, 740);
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => AppState.CancelRender?.Invoke();

        AppState.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateBadge);

        _pendingTag = startPage switch
        {
            "einrichtung" => "setup",
            "einstellungen" => "settings",
            _ => "render",
        };
        ContentFrame.Navigate(PageFor(_pendingTag));
        _ = AppState.RefreshSetupAsync();
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
        TitleStatus.Text = status is null ? "" : "·  " + status;
        Title = status is null ? "Retro Showroom" : $"{status} · Retro Showroom";
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
