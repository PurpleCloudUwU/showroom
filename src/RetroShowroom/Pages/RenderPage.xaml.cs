using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RetroShowroom.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace RetroShowroom.Pages;

public sealed partial class RenderPage : Page
{
    private readonly bool _ready;
    private bool _loadingUi;

    private PartInfo? _part;
    private readonly List<string> _texturePaths = [];
    private PreparedTexture? _texture;
    private int _textureVersion;

    private bool _rendering;
    private CancellationTokenSource? _cts;
    private readonly LogBuffer _log = new(600);
    private readonly DispatcherQueueTimer _timer;
    private DateTime _renderStart;
    private volatile RenderProgress? _progress;
    private volatile string? _notice;
    private string? _lastOutput;

    public RenderPage()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(400);
        _timer.Tick += (_, _) => RefreshRenderUi();

        LoadOptionsFromSettings();
        AppState.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateSetupInfo();
            UpdateStartState();
        });
        Loaded += RenderPage_Loaded;

        _ready = true;
        UpdateSetupInfo();
        UpdateStartState();
    }

    // ---------------------------------------------------------------- Optionen

    private string SelectedQuality => QualityRadios.SelectedIndex switch { 1 => Quality.Gross, 2 => Quality.Test, _ => Quality.Final };
    private char SelectedGender => GenderRadios.SelectedIndex == 1 ? 'f' : 'm';
    private string SelectedKind => KindRadios.SelectedIndex == 1 ? "hose" : "oben";
    private string SelectedStyle => (StyleCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? RenderDefaults.Style;
    private double SpotWatt => double.IsNaN(SpotWattBox.Value) ? RenderDefaults.SpotWatt : SpotWattBox.Value;

    private void LoadOptionsFromSettings()
    {
        _loadingUi = true;
        var s = AppState.Settings;
        QualityRadios.SelectedIndex = s.Quality switch { Quality.Gross => 1, Quality.Test => 2, _ => 0 };
        SelectStyle(s.Style);
        SpotsToggle.IsOn = s.Spots;
        SpotWattBox.Value = s.SpotWatt;
        KonturToggle.IsOn = s.Kontur;
        _loadingUi = false;
        UpdateAdvancedSummary();
    }

    private void SelectStyle(string style)
    {
        StyleCombo.SelectedItem = StyleCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == style)
                                  ?? StyleCombo.Items[0];
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loadingUi) return;
        SaveOptions();
        UpdateAdvancedSummary();
        UpdateSetupInfo();
        UpdateStartState();
    }

    private void SpotWattBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Options_Changed(sender, new RoutedEventArgs());

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) UpdateStartState();
    }

    private void SaveOptions()
    {
        var s = AppState.Settings;
        s.Quality = SelectedQuality;
        s.Style = SelectedStyle;
        s.Spots = SpotsToggle.IsOn;
        s.SpotWatt = SpotWatt;
        s.Kontur = KonturToggle.IsOn;
        AppState.SaveSettings();
    }

    private void ResetAdvanced_Click(object sender, RoutedEventArgs e)
    {
        _loadingUi = true;
        SelectStyle(RenderDefaults.Style);
        SpotsToggle.IsOn = RenderDefaults.Spots;
        SpotWattBox.Value = RenderDefaults.SpotWatt;
        KonturToggle.IsOn = RenderDefaults.Kontur;
        _loadingUi = false;
        Options_Changed(sender, e);
    }

    private void UpdateAdvancedSummary()
    {
        var diffs = new List<string>();
        if (SelectedStyle != RenderDefaults.Style) diffs.Add(Loc.T("Stil ", "style ") + (StyleCombo.SelectedItem as ComboBoxItem)?.Content);
        if (SpotsToggle.IsOn != RenderDefaults.Spots) diffs.Add(SpotsToggle.IsOn ? Loc.T("Spots an", "spots on") : Loc.T("Spots aus", "spots off"));
        else if (SpotsToggle.IsOn && Math.Abs(SpotWatt - RenderDefaults.SpotWatt) > 0.01) diffs.Add($"Spots {SpotWatt:0} W");
        if (KonturToggle.IsOn != RenderDefaults.Kontur) diffs.Add(KonturToggle.IsOn ? Loc.T("Kontur an", "contour on") : Loc.T("Kontur aus", "contour off"));
        SpotWattBox.IsEnabled = SpotsToggle.IsOn;

        AdvancedSummary.Text = diffs.Count == 0
            ? Loc.T("Abgesegneter Standard", "Approved default")
            : Loc.T("Abweichend: ", "Changed: ") + string.Join(", ", diffs);
        AdvancedSummary.Foreground = Ui.Brush(diffs.Count == 0 ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");
    }

    // ---------------------------------------------------------------- Einrichtung / Startknopf

    private void UpdateSetupInfo()
    {
        if (!_ready) return;
        var setup = AppState.Setup;
        ReferenceToggle.IsEnabled = setup?.ReferencePath is not null;
        if (ReferenceToggle.IsChecked == true && setup?.ReferencePath is null) ReferenceToggle.IsChecked = false;

        if (setup is null)
        {
            SetupInfoBar.IsOpen = true;
            SetupInfoBar.Severity = InfoBarSeverity.Informational;
            SetupInfoBar.Title = Loc.T("Einrichtung wird geprüft …", "Checking setup …");
            SetupInfoBar.Message = Loc.T("Blender wird dafür kurz im Hintergrund gestartet.", "Blender is started briefly in the background for this.");
            SetupInfoButton.Visibility = Visibility.Collapsed;
            DeviceHint.Text = Loc.T("Rechengerät wird ermittelt …", "Detecting render device …");
            return;
        }

        var issues = setup.BlockingIssues(SelectedGender);
        SetupInfoBar.IsOpen = issues.Count > 0;
        SetupInfoBar.Severity = InfoBarSeverity.Warning;
        SetupInfoBar.Title = Loc.T("Einrichtung unvollständig", "Setup incomplete");
        SetupInfoBar.Message = Loc.T("Es fehlt: ", "Missing: ") + string.Join(", ", issues);
        SetupInfoButton.Visibility = Visibility.Visible;

        var cpuTimes = Loc.T("Auf der CPU dauert Final etwa 6–9 Min., Groß etwa 15–20 Min.",
                             "On the CPU, Final takes about 6–9 min, Large about 15–20 min.");
        if (!AppState.Settings.UseGpu)
            DeviceHint.Text = Loc.T("Rendert nur auf der CPU (in den Einstellungen gewählt). ", "Renders on the CPU only (chosen in the settings). ") + cpuTimes;
        else if (setup.Gpus.Count > 0)
            DeviceHint.Text = Loc.T($"Rendert auf der Grafikkarte: {setup.GpuSummary}. Bei Fehlern automatisch auf der CPU.",
                                    $"Renders on the graphics card: {setup.GpuSummary}. Falls back to the CPU automatically on errors.");
        else if (setup.BlenderOk)
            DeviceHint.Text = Loc.T("Keine kompatible Grafikkarte gefunden, es wird auf der CPU gerendert. ",
                                    "No compatible graphics card found, rendering on the CPU. ") + cpuTimes;
        else
            DeviceHint.Text = Loc.T("Rechengerät wird nach der Einrichtung ermittelt.", "The render device is detected once setup is complete.");
    }

    private void UpdateStartState()
    {
        if (!_ready) return;
        var missing = new List<string>();
        if (_part is null) missing.Add(Loc.T("Kleidungsstück", "clothing item"));
        if (_texture is null) missing.Add(Loc.T("Textur", "texture"));
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) missing.Add(Loc.T("Titel", "title"));
        var setup = AppState.Setup;
        if (setup is null) missing.Add(Loc.T("Einrichtungsprüfung läuft", "setup check running"));
        else missing.AddRange(setup.BlockingIssues(SelectedGender));

        StartButton.IsEnabled = !_rendering && missing.Count == 0;
        if (_rendering)
            StartHint.Text = "";
        else if (missing.Count > 0)
            StartHint.Text = Loc.T("Es fehlt noch: ", "Still missing: ") + string.Join(", ", missing);
        else
        {
            var folder = AppState.Settings.EffectiveOutputFolder;
            var file = Path.GetFileName(OutputNaming.Next(folder, TitleBox.Text, SelectedQuality));
            StartHint.Text = Loc.T($"Wird gespeichert als {file} in „{folder}“.", $"Will be saved as {file} in \"{folder}\".");
        }
    }

    private void GoToSetup_Click(object sender, RoutedEventArgs e) => App.MainWindow.Navigate("setup");

    // ---------------------------------------------------------------- Dateien

    private async void RenderPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (AppState.StartupFiles.Count == 0) return;
        var files = AppState.StartupFiles.ToList();
        AppState.StartupFiles.Clear();
        await HandleFilesAsync(files);
    }

    private async Task HandleFilesAsync(IReadOnlyList<string> paths)
    {
        InputInfoBar.IsOpen = false;
        var part = paths.FirstOrDefault(PartInfo.IsPartFile);
        var tex = paths.FirstOrDefault(PartInfo.IsTextureFile);
        if (part is null && tex is null)
        {
            ShowInputInfo(InfoBarSeverity.Warning, Loc.T("Datei nicht unterstützt", "File not supported"),
                Loc.T("Erwartet wird eine .ydd oder .blend (Kleidungsstück) und eine .ytd oder .png (Textur).",
                      "Expected a .ydd or .blend (clothing item) and a .ytd or .png (texture)."));
            return;
        }
        if (part is not null) await SetPartAsync(part, autoTexture: tex is null);
        if (tex is not null) await SelectTextureFileAsync(tex);
    }

    private async Task SetPartAsync(string path, bool autoTexture)
    {
        _part = PartInfo.FromPath(path);
        AppState.Settings.LastPartFolder = Path.GetDirectoryName(_part.FullPath);
        AppState.SaveSettings();

        PartNameText.Text = ShortName(_part.FileName);
        ToolTipService.SetToolTip(PartNameText, _part.FullPath);
        PartIcon.Glyph = _part.IsBlend ? "" : "";
        PartIcon.Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush");

        _loadingUi = true;
        if (_part.Gender is char g) GenderRadios.SelectedIndex = g == 'f' ? 1 : 0;
        KindRadios.SelectedIndex = _part.Kind == "hose" ? 1 : 0;
        LongCheck.IsChecked = false;
        _loadingUi = false;

        var bits = new List<string>
        {
            _part.Gender switch
            {
                'm' => Loc.T("Männlich", "Male"),
                'f' => Loc.T("Weiblich", "Female"),
                _ => Loc.T("Geschlecht unbekannt", "Gender unknown"),
            },
            _part.Kind == "hose" ? Loc.T("Hose", "Pants") : Loc.T("Oberteil", "Top"),
        };
        if (_part.Component is not null) bits.Add($"{_part.Component} {_part.Index}");
        if (_part.IsBlend) bits.Add(Loc.T("bearbeitetes Teil (.blend)", "edited item (.blend)"));
        if (_part.FileName.Contains('^')) bits.Add(_part.FileName[.._part.FileName.LastIndexOf('^')]);
        PartInfoText.Text = string.Join(" · ", bits);
        DetectHint.Text = _part.Gender is null
            ? Loc.T("Das Geschlecht steht nicht im Dateinamen. Bitte oben prüfen.", "The gender is not in the file name. Please check above.")
            : Loc.T("Geschlecht und Typ wurden aus dem Dateinamen erkannt.", "Gender and type were detected from the file name.");

        if (autoTexture)
        {
            _texturePaths.Clear();
            _texturePaths.AddRange(_part.FindTextureCandidates());
            RefreshTextureCombo(_texturePaths.Count > 0 ? 0 : -1);
            if (_texturePaths.Count > 0)
                await LoadTextureAsync(_texturePaths[0], null);
            else
                ClearTexture(Loc.T("Keine passende Textur im Ordner gefunden. Über den Knopf rechts eine .ytd oder .png wählen.",
                                   "No matching texture found in the folder. Use the button on the right to choose a .ytd or .png."));
        }

        UpdateSetupInfo();
        UpdateStartState();
    }

    private async Task SelectTextureFileAsync(string path)
    {
        var full = Path.GetFullPath(path);
        int idx = _texturePaths.FindIndex(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            _texturePaths.Add(full);
            idx = _texturePaths.Count - 1;
        }
        RefreshTextureCombo(idx);
        await LoadTextureAsync(full, null);
    }

    /// <summary>„mp_m_freemode_01_…^jbib_016_u.ydd“ → „jbib_016_u.ydd“ (das Pack-Präfix ist bei allen Dateien gleich).</summary>
    private static string ShortName(string fileName) =>
        fileName.Contains('^') ? fileName[(fileName.LastIndexOf('^') + 1)..] : fileName;

    private void RefreshTextureCombo(int selectIndex)
    {
        _loadingUi = true;
        TextureCombo.ItemsSource = _texturePaths.Select(p => ShortName(Path.GetFileName(p))).ToList();
        TextureCombo.SelectedIndex = selectIndex;
        _loadingUi = false;
    }

    private void ClearTexture(string message)
    {
        _textureVersion++;
        _texture = null;
        TexThumb.Source = null;
        TexBusy.IsActive = false;
        TexPlaceholder.Visibility = Visibility.Visible;
        InnerTextureCombo.Visibility = Visibility.Collapsed;
        TexInfoText.Text = message;
        UpdateStartState();
    }

    private async void TextureCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loadingUi) return;
        int i = TextureCombo.SelectedIndex;
        if (i >= 0 && i < _texturePaths.Count) await LoadTextureAsync(_texturePaths[i], null);
    }

    private async void InnerTextureCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loadingUi || _texture is null) return;
        if (InnerTextureCombo.SelectedItem is string name && name != _texture.TextureName)
            await LoadTextureAsync(_texture.SourcePath, name);
    }

    private async Task LoadTextureAsync(string path, string? innerName)
    {
        int version = ++_textureVersion;
        _texture = null;
        UpdateStartState();
        TexBusy.IsActive = true;
        TexThumb.Source = null;
        TexPlaceholder.Visibility = Visibility.Collapsed;
        TexInfoText.Text = path.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("YTD wird gelesen und umgewandelt …", "Reading and converting YTD …")
            : Loc.T("Textur wird gelesen …", "Reading texture …");
        try
        {
            var t = await Task.Run(() => TexturePreparer.Prepare(path, innerName));
            if (version != _textureVersion) return;
            _texture = t;
            TexThumb.Source = await Ui.LoadBitmapAsync(t.PngPath, 160);
            TexInfoText.Text = $"{t.TextureName} · {t.Width} × {t.Height} · {t.FormatName}";

            _loadingUi = true;
            if (t.AllTextureNames.Count > 1)
            {
                InnerTextureCombo.ItemsSource = t.AllTextureNames.ToList();
                InnerTextureCombo.SelectedItem = t.TextureName;
                InnerTextureCombo.Visibility = Visibility.Visible;
            }
            else
            {
                InnerTextureCombo.Visibility = Visibility.Collapsed;
            }
            _loadingUi = false;
        }
        catch (Exception ex)
        {
            if (version != _textureVersion) return;
            _texture = null;
            TexPlaceholder.Visibility = Visibility.Visible;
            TexInfoText.Text = Loc.T("Textur nicht lesbar: ", "Texture not readable: ") + ex.Message;
        }
        finally
        {
            if (version == _textureVersion)
            {
                TexBusy.IsActive = false;
                UpdateStartState();
            }
        }
    }

    private async void PickPart_Click(object sender, RoutedEventArgs e)
    {
        var file = await Ui.PickFileAsync(".ydd", ".blend");
        if (file is not null) await HandleFilesAsync([file]);
    }

    private async void PickTexture_Click(object sender, RoutedEventArgs e)
    {
        var file = await Ui.PickFileAsync(".ytd", ".png");
        if (file is not null) await SelectTextureFileAsync(file);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (_rendering || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } ui) ui.Caption = Loc.T("Übernehmen", "Use");
        DropRect.Stroke = Ui.Brush("AccentFillColorDefaultBrush");
        DropRect.Fill = Ui.Brush("SubtleFillColorSecondaryBrush");
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => ResetDropVisual();

    private void ResetDropVisual()
    {
        DropRect.Stroke = Ui.Brush("ControlStrongStrokeColorDefaultBrush");
        DropRect.Fill = Ui.Brush("SubtleFillColorTransparentBrush");
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        ResetDropVisual();
        if (_rendering || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        List<string> paths;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }
        finally
        {
            deferral.Complete();
        }
        if (paths.Count > 0) await HandleFilesAsync(paths);
    }

    private void ShowInputInfo(InfoBarSeverity severity, string title, string message)
    {
        InputInfoBar.Severity = severity;
        InputInfoBar.Title = title;
        InputInfoBar.Message = message;
        InputInfoBar.IsOpen = true;
    }

    // ---------------------------------------------------------------- Rendern

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var setup = AppState.Setup;
        if (_rendering || _part is null || _texture is null || setup is null) return;
        if (setup.BlockingIssues(SelectedGender).Count > 0) return;

        var s = AppState.Settings;
        var title = TitleBox.Text.Trim().ToUpperInvariant();
        var quality = SelectedQuality;
        string outDir, script;
        try
        {
            outDir = AppPaths.Ensure(s.EffectiveOutputFolder);
            script = RenderScript.Extract();
        }
        catch (Exception ex)
        {
            ShowResultInfo(InfoBarSeverity.Error, Loc.T("Rendern kann nicht starten", "Rendering cannot start"), ex.Message);
            return;
        }

        var req = new RenderRequest
        {
            BlenderPath = setup.BlenderPath!,
            ScriptPath = script,
            PartPath = _part.FullPath,
            TexturePng = _texture.PngPath,
            LogoPath = setup.LogoPath!,
            RenderTarget = Path.Combine(AppPaths.Ensure(AppPaths.CacheDir), $"render_{Guid.NewGuid():N}.png"),
            OutputPath = OutputNaming.Next(outDir, title, quality),
            Title = title,
            Gender = SelectedGender,
            Kind = SelectedKind,
            LongTop = LongCheck.IsChecked == true,
            Quality = quality,
            Style = SelectedStyle,
            Spots = SpotsToggle.IsOn,
            SpotWatt = SpotWatt,
            Kontur = KonturToggle.IsOn,
            UseGpu = s.UseGpu,
            DetailedLog = setup.SupportsDetailedLog,
        };

        SetRendering(true);
        _log.Clear();
        _log.Add("> " + req.CommandLinePreview());
        _progress = null;
        _notice = null;
        ResultInfoBar.IsOpen = false;
        _renderStart = DateTime.Now;
        _timer.Start();

        var cts = _cts = new CancellationTokenSource();
        AppState.CancelRender = () => { try { cts.Cancel(); } catch { } };
        var runner = new RenderRunner();
        runner.Output += _log.Add;
        runner.Progress += p => _progress = p;
        runner.Notice += n => _notice = n;

        RenderResult result;
        try
        {
            result = await Task.Run(() => runner.RunAsync(req, cts.Token));
        }
        catch (Exception ex)
        {
            result = new RenderResult { Error = ex.Message };
        }
        finally
        {
            _timer.Stop();
            AppState.CancelRender = null;
            _cts = null;
            cts.Dispose();
            RefreshRenderUi();
            SetRendering(false);
        }
        await ShowResultAsync(result);
    }

    private void SetRendering(bool on)
    {
        _rendering = on;
        AppState.IsRendering = on;
        InputHost.IsEnabled = !on;
        CancelButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        if (on)
        {
            RenderBar.IsIndeterminate = true;
            RenderBar.ShowError = false;
            RenderBar.Value = 0;
            PhaseText.Text = Loc.T("Blender startet …", "Starting Blender …");
            DeviceText.Text = "";
            TimeText.Text = "";
            OpenImageButton.IsEnabled = ShowInFolderButton.IsEnabled = CopyButton.IsEnabled = false;
            App.MainWindow.SetRenderStatus(Loc.T("Rendern …", "Rendering …"));
        }
        else
        {
            RenderBar.IsIndeterminate = false;
            App.MainWindow.SetRenderStatus(null);
        }
        UpdateStartState();
    }

    private void RefreshRenderUi()
    {
        if (_log.TakeChanged(out var text))
        {
            LogText.Text = text;
            if (LogExpander.IsExpanded)
            {
                LogScroll.UpdateLayout();
                LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
            }
        }
        if (!_rendering) return;

        if (_notice is { } n)
        {
            _notice = null;
            ShowResultInfo(InfoBarSeverity.Warning, Loc.T("Grafikkarte gescheitert", "Graphics card failed"), n);
        }

        var time = Loc.T($"{Ui.Duration(DateTime.Now - _renderStart)} vergangen", $"{Ui.Duration(DateTime.Now - _renderStart)} elapsed");
        if (_progress is { } p)
        {
            DeviceText.Text = p.Device ?? "";
            if (p.TotalSamples > 0)
            {
                double pct = 100.0 * p.Sample / p.TotalSamples;
                RenderBar.IsIndeterminate = false;
                RenderBar.Value = pct;
                PhaseText.Text = Loc.T($"Rendern · Sample {p.Sample} von {p.TotalSamples}", $"Rendering · sample {p.Sample} of {p.TotalSamples}");
                App.MainWindow.SetRenderStatus(Loc.T($"Rendern {pct:0} %", $"Rendering {pct:0}%"));
                if (p.Remaining is TimeSpan r) time += Loc.T($" · noch ca. {Ui.Duration(r)}", $" · about {Ui.Duration(r)} left");
            }
            else
            {
                PhaseText.Text = p.Phase;
                RenderBar.IsIndeterminate = true;
            }
        }
        TimeText.Text = time;
    }

    private async Task ShowResultAsync(RenderResult r)
    {
        TimeText.Text = r.Duration > TimeSpan.Zero ? Loc.T("Dauer ", "Duration ") + Ui.Duration(r.Duration) : "";
        if (r.Device is not null) DeviceText.Text = r.Device;

        if (r.Success && r.OutputPath is not null)
        {
            _lastOutput = r.OutputPath;
            RenderBar.Value = 100;
            PhaseText.Text = Loc.T("Fertig", "Done");
            ResultTitle.Text = Path.GetFileName(r.OutputPath);
            ReferenceToggle.IsChecked = false;
            await ShowImageAsync(r.OutputPath, null);
            OpenImageButton.IsEnabled = ShowInFolderButton.IsEnabled = CopyButton.IsEnabled = true;
            if (r.FellBackToCpu)
                ShowResultInfo(InfoBarSeverity.Warning, Loc.T("Auf der CPU gerendert", "Rendered on the CPU"),
                    Loc.T("Die Grafikkarte ist gescheitert, das Bild kommt von der CPU. Details in der Blender-Ausgabe.",
                          "The graphics card failed, so the image was rendered on the CPU. Details are in the Blender output."));
            else
                ShowResultInfo(InfoBarSeverity.Success, Loc.T("Fertig", "Done"),
                    Loc.T($"Gespeichert in „{Path.GetDirectoryName(r.OutputPath)}“.", $"Saved in \"{Path.GetDirectoryName(r.OutputPath)}\"."));
            Ui.FlashTaskbar();
        }
        else if (r.Cancelled)
        {
            RenderBar.Value = 0;
            PhaseText.Text = Loc.T("Abgebrochen", "Cancelled");
        }
        else
        {
            RenderBar.Value = 100;
            RenderBar.ShowError = true;
            PhaseText.Text = Loc.T("Fehlgeschlagen", "Failed");
            ShowResultInfo(InfoBarSeverity.Error, r.Error ?? Loc.T("Rendern fehlgeschlagen", "Rendering failed"),
                r.Hint ?? Loc.T("Details stehen in der Blender-Ausgabe.", "Details are in the Blender output."));
            LogExpander.IsExpanded = true;
            RefreshRenderUi();
            Ui.FlashTaskbar();
        }
        UpdateStartState();
    }

    private async Task ShowImageAsync(string path, string? badge)
    {
        try
        {
            ResultImage.Source = await Ui.LoadBitmapAsync(path, 2560);
            EmptyState.Visibility = Visibility.Collapsed;
            ImageBadge.Visibility = badge is null ? Visibility.Collapsed : Visibility.Visible;
            ImageBadgeText.Text = badge ?? "";
        }
        catch (Exception ex)
        {
            ShowResultInfo(InfoBarSeverity.Error, Loc.T("Bild kann nicht angezeigt werden", "Image cannot be displayed"), ex.Message);
        }
    }

    private void ShowResultInfo(InfoBarSeverity severity, string title, string message)
    {
        ResultInfoBar.Severity = severity;
        ResultInfoBar.Title = title;
        ResultInfoBar.Message = message;
        ResultInfoBar.IsOpen = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        PhaseText.Text = Loc.T("Wird abgebrochen …", "Cancelling …");
        try { _cts?.Cancel(); } catch { }
    }

    private async void ReferenceToggle_Click(object sender, RoutedEventArgs e)
    {
        var reference = AppState.Setup?.ReferencePath;
        if (ReferenceToggle.IsChecked == true && reference is not null)
        {
            await ShowImageAsync(reference, Loc.T("Referenz", "Reference"));
        }
        else if (_lastOutput is not null && File.Exists(_lastOutput))
        {
            await ShowImageAsync(_lastOutput, null);
        }
        else
        {
            ResultImage.Source = null;
            ImageBadge.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is not null && File.Exists(_lastOutput)) Ui.OpenPath(_lastOutput);
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is not null && File.Exists(_lastOutput)) Ui.ShowInExplorer(_lastOutput);
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is null || !File.Exists(_lastOutput)) return;
        try
        {
            await Ui.CopyImageAsync(_lastOutput);
            ShowResultInfo(InfoBarSeverity.Success, Loc.T("Kopiert", "Copied"),
                Loc.T("Das Bild ist in der Zwischenablage, z. B. zum Einfügen in Discord.", "The image is on the clipboard, e.g. for pasting into Discord."));
        }
        catch (Exception ex)
        {
            ShowResultInfo(InfoBarSeverity.Error, Loc.T("Kopieren fehlgeschlagen", "Copy failed"), ex.Message);
        }
    }
}
