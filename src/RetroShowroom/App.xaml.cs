using Microsoft.UI.Xaml;
using RetroShowroom.Core;

namespace RetroShowroom;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        // Sprache vor dem Laden der Oberfläche festlegen (auch für WinUI-eigene Texte wie „Einstellungen“)
        Loc.Set(AppState.Settings.Language);
        try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Loc.WindowsTag; } catch { }

        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.Ensure(AppPaths.DataDir), "fehler.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
            }
            catch { }
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string? startPage = null;
        foreach (var a in Environment.GetCommandLineArgs().Skip(1))
        {
            if (a.StartsWith("--seite=", StringComparison.OrdinalIgnoreCase))
                startPage = a["--seite=".Length..].ToLowerInvariant();
            else if (File.Exists(a))
                AppState.StartupFiles.Add(Path.GetFullPath(a));
        }

        TexturePreparer.CleanCache(TimeSpan.FromDays(14));
        MainWindow = new MainWindow(startPage);
        MainWindow.Activate();
    }
}
