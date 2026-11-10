using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RetroShowroom.Core;

namespace RetroShowroom.Pages;

public sealed partial class SetupPage : Page
{
    private const string SollumzReleases = "https://github.com/Sollumz/Sollumz/releases";
    private const string BlenderDownload = "https://www.blender.org/download/";

    public SetupPage()
    {
        InitializeComponent();
        AppState.Changed += (_, _) => DispatcherQueue.TryEnqueue(Render);
        Render();
    }

    private void Render()
    {
        var r = AppState.Setup;
        bool checking = AppState.IsCheckingSetup;
        CheckRing.IsActive = checking;
        RecheckButton.IsEnabled = !checking;

        if (r is null)
        {
            foreach (var icon in new[] { BlenderIcon, SollumzIcon, SkeletonIcon, GpuIcon, LogoIcon, RefIcon })
                Ui.SetState(icon, CheckState.Pending);
            var checkingText = Loc.T("Wird geprüft …", "Checking …");
            BlenderDetail.Text = SollumzDetail.Text = SkeletonDetail.Text = GpuDetail.Text = checkingText;
            LogoDetail.Text = RefDetail.Text = checkingText;
            SummaryBar.Severity = InfoBarSeverity.Informational;
            SummaryBar.Title = Loc.T("Einrichtung wird geprüft …", "Checking setup …");
            SummaryBar.Message = Loc.T("Das dauert ein paar Sekunden.", "This takes a few seconds.");
            return;
        }

        // Blender
        string? version = r.BlenderVersion;
        if (!r.BlenderFound)
        {
            Ui.SetState(BlenderIcon, CheckState.Missing);
            BlenderDetail.Text = Loc.T("Nicht gefunden. Über „Installieren“ einrichten oder den Pfad zu blender.exe wählen.",
                                       "Not found. Use \"Install\" or choose the path to blender.exe.");
        }
        else if (r.BlenderError is not null)
        {
            Ui.SetState(BlenderIcon, CheckState.Missing);
            BlenderDetail.Text = $"{r.BlenderPath}\n{r.BlenderError}";
        }
        else if (!r.BlenderVersionOk)
        {
            Ui.SetState(BlenderIcon, CheckState.Missing);
            BlenderDetail.Text = Loc.T($"Blender {version} ist zu alt, benötigt wird 4.2 oder neuer.", $"Blender {version} is too old, 4.2 or newer is required.")
                                 + $"\n{r.BlenderPath}";
        }
        else
        {
            Ui.SetState(BlenderIcon, CheckState.Ok);
            BlenderDetail.Text = $"Blender {version}\n{r.BlenderPath}";
        }
        InstallBlenderButton.Visibility = r.BlenderOk ? Visibility.Collapsed : Visibility.Visible;

        // Sollumz
        if (!r.BlenderOk)
        {
            Ui.SetState(SollumzIcon, CheckState.Pending);
            SollumzDetail.Text = Loc.T("Wird geprüft, sobald Blender läuft.", "Checked once Blender is working.");
        }
        else if (r.SollumzKey is null)
        {
            Ui.SetState(SollumzIcon, CheckState.Missing);
            SollumzDetail.Text = Loc.T("Nicht installiert oder nicht aktiviert.", "Not installed or not enabled.");
        }
        else if (!r.SollumzOk)
        {
            Ui.SetState(SollumzIcon, CheckState.Missing);
            SollumzDetail.Text = Loc.T(
                $"Installiert als „{r.SollumzKey}“. Die App erwartet „{SetupReport.ExpectedSollumzKey}“. Bitte als Erweiterung über „Von Datei installieren …“ einrichten (Repository „user_default“).",
                $"Installed as \"{r.SollumzKey}\". The app expects \"{SetupReport.ExpectedSollumzKey}\". Please install it as an extension via Install from Disk … (repository \"user_default\").");
        }
        else
        {
            Ui.SetState(SollumzIcon, CheckState.Ok);
            var ver = string.IsNullOrEmpty(r.SollumzVersion) ? "" : " " + r.SollumzVersion;
            SollumzDetail.Text = Loc.T($"Sollumz{ver} ist installiert und aktiv.", $"Sollumz{ver} is installed and enabled.")
                                 + (r.PyMateriaFound == false
                                     ? Loc.T(" PyMateria wurde nicht gefunden. Falls der YDD-Import scheitert, in den Sollumz-Einstellungen die Lizenz annehmen.",
                                             " PyMateria was not found. If the YDD import fails, accept the license in the Sollumz preferences.")
                                     : "");
        }
        SollumzSteps.Visibility = r.SollumzOk ? Visibility.Collapsed : Visibility.Visible;

        // Skelette
        bool skelOk = r.SkeletonOk('m') && r.SkeletonOk('f');
        if (!r.SollumzOk)
        {
            Ui.SetState(SkeletonIcon, CheckState.Pending);
            SkeletonDetail.Text = Loc.T("Wird geprüft, sobald Sollumz installiert ist.", "Checked once Sollumz is installed.");
        }
        else
        {
            Ui.SetState(SkeletonIcon, skelOk ? CheckState.Ok : CheckState.Missing);
            var missing = Loc.T("fehlt", "missing");
            var listed = r.SkeletonPaths.Count > 0 && !skelOk
                ? Loc.T("\nIn Sollumz eingetragen, aber nicht gefunden:\n", "\nListed in Sollumz but not found:\n") + string.Join("\n", r.SkeletonPaths)
                : "";
            SkeletonDetail.Text = Loc.T("Männlich: ", "Male: ") + (r.SkeletonMale ?? missing) + "\n"
                                  + Loc.T("Weiblich: ", "Female: ") + (r.SkeletonFemale ?? missing) + listed;
        }
        SkeletonSteps.Visibility = skelOk ? Visibility.Collapsed : Visibility.Visible;

        // Grafikkarte
        if (!AppState.Settings.UseGpu)
        {
            Ui.SetState(GpuIcon, CheckState.Info);
            GpuDetail.Text = Loc.T("In den Einstellungen ausgeschaltet. Es wird nur auf der CPU gerendert.",
                                   "Turned off in the settings. Rendering uses the CPU only.");
        }
        else if (!r.BlenderOk)
        {
            Ui.SetState(GpuIcon, CheckState.Pending);
            GpuDetail.Text = Loc.T("Wird geprüft, sobald Blender läuft.", "Checked once Blender is working.");
        }
        else if (r.Gpus.Count > 0)
        {
            Ui.SetState(GpuIcon, CheckState.Ok);
            GpuDetail.Text = Loc.T(
                $"{r.GpuSummary}. Wird zum Rendern verwendet. Scheitert sie (z. B. Grafikspeicher voll), rendert die App automatisch auf der CPU weiter.",
                $"{r.GpuSummary}. Used for rendering. If it fails (e.g. graphics memory full), the app continues on the CPU automatically.");
        }
        else
        {
            Ui.SetState(GpuIcon, CheckState.Warning);
            GpuDetail.Text = Loc.T("Keine kompatible Grafikkarte gefunden (OptiX, CUDA, HIP oder oneAPI). Es wird auf der CPU gerendert.",
                                   "No compatible graphics card found (OptiX, CUDA, HIP or oneAPI). Rendering uses the CPU.");
        }

        // Assets
        Asset(LogoIcon, LogoDetail, r.LogoPath, true,
            Loc.T("Noch nicht hinzugefügt. Unter Einstellungen → Assets auswählen.", "Not added yet. Choose it under Settings → Assets."));
        Asset(RefIcon, RefDetail, r.ReferencePath, false,
            Loc.T("Optional. Zum Vergleichen mit dem Ergebnis.", "Optional. For comparing with the result."));

        // Zusammenfassung
        int open = r.OpenPoints;
        SummaryBar.Severity = open == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        SummaryBar.Title = open == 0
            ? Loc.T("Alles bereit", "All set")
            : open == 1 ? Loc.T("1 Punkt offen", "1 item open") : Loc.T($"{open} Punkte offen", $"{open} items open");
        SummaryBar.Message = (open == 0
                                 ? Loc.T("Du kannst rendern.", "You can render now.")
                                 : Loc.T("Die rot markierten Punkte erledigen und danach „Erneut prüfen“.", "Fix the items marked in red, then click \"Check again\"."))
                             + Loc.T($" Geprüft um {r.CheckedAt:HH:mm} Uhr.", $" Checked at {r.CheckedAt:HH:mm}.");
    }

    private static void Asset(FontIcon icon, TextBlock detail, string? path, bool required, string missingText)
    {
        if (path is not null)
        {
            Ui.SetState(icon, CheckState.Ok);
            detail.Text = Path.GetFileName(path);
        }
        else
        {
            Ui.SetState(icon, required ? CheckState.Missing : CheckState.Info);
            detail.Text = missingText;
        }
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        ActionInfoBar.IsOpen = false;
        await AppState.RefreshSetupAsync();
    }

    private void InstallBlender_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/k winget install --id BlenderFoundation.Blender -e --source winget")
            {
                UseShellExecute = true,
            });
            ShowAction(InfoBarSeverity.Informational, Loc.T("Installation gestartet", "Installation started"),
                Loc.T("Im Konsolenfenster läuft winget. Wenn die Installation fertig ist, hier auf „Erneut prüfen“ klicken.",
                      "winget is running in the console window. When the installation is done, click \"Check again\" here."));
        }
        catch
        {
            Ui.OpenUrl(BlenderDownload);
            ShowAction(InfoBarSeverity.Informational, Loc.T("Download-Seite geöffnet", "Download page opened"),
                Loc.T("winget ist nicht verfügbar. Blender von der Webseite installieren und danach „Erneut prüfen“.",
                      "winget is not available. Install Blender from the website, then click \"Check again\"."));
        }
    }

    private async void PickBlender_Click(object sender, RoutedEventArgs e)
    {
        var file = await Ui.PickFileAsync(".exe");
        if (file is null) return;
        if (!Path.GetFileName(file).Equals("blender.exe", StringComparison.OrdinalIgnoreCase))
        {
            ShowAction(InfoBarSeverity.Warning, Loc.T("Falsche Datei", "Wrong file"),
                Loc.T("Bitte die Datei blender.exe im Blender-Ordner auswählen.", "Please choose blender.exe in the Blender folder."));
            return;
        }
        AppState.Settings.BlenderPath = file;
        AppState.SaveSettings();
        await AppState.RefreshSetupAsync();
    }

    private void SollumzDownload_Click(object sender, RoutedEventArgs e) => Ui.OpenUrl(SollumzReleases);

    private void OpenBlender_Click(object sender, RoutedEventArgs e)
    {
        var path = AppState.Setup?.BlenderPath;
        if (path is null || !File.Exists(path))
        {
            ShowAction(InfoBarSeverity.Warning, Loc.T("Blender nicht gefunden", "Blender not found"),
                Loc.T("Bitte zuerst Blender installieren oder den Pfad wählen.", "Please install Blender or choose its path first."));
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
        ShowAction(InfoBarSeverity.Informational, Loc.T("Blender wurde geöffnet", "Blender was opened"),
            Loc.T("Nach den Änderungen in Blender die Einstellungen speichern, Blender schließen und hier „Erneut prüfen“.",
                  "After making the changes in Blender, save the preferences, close Blender and click \"Check again\" here."));
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.MainWindow.Navigate("settings");

    private void ShowAction(InfoBarSeverity severity, string title, string message)
    {
        ActionInfoBar.Severity = severity;
        ActionInfoBar.Title = title;
        ActionInfoBar.Message = message;
        ActionInfoBar.IsOpen = true;
    }
}
