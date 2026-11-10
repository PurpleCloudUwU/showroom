using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RetroShowroom.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace RetroShowroom;

internal enum CheckState { Ok, Warning, Missing, Pending, Info }

/// <summary>Kleine Helfer für Dateiauswahl, Bilder, Explorer und Statussymbole.</summary>
internal static class Ui
{
    public static nint Hwnd => WindowNative.GetWindowHandle(App.MainWindow);

    public static async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.ComputerFolder };
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>Bild laden, ohne die Datei zu sperren. Große Bilder werden auf maxWidth verkleinert.</summary>
    public static async Task<BitmapImage> LoadBitmapAsync(string path, int maxWidth = 0)
    {
        var bmp = new BitmapImage();
        if (maxWidth > 0)
        {
            var info = PngWriter.ReadInfo(path);
            if (info is null || info.Value.Width > maxWidth) bmp.DecodePixelWidth = maxWidth;
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await bmp.SetSourceAsync(fs.AsRandomAccessStream());
        return bmp;
    }

    /// <summary>Das eingebettete App-Logo (Retro-Sechseck) als Bild.</summary>
    public static async Task<BitmapImage?> LoadAppLogoAsync(int width)
    {
        try
        {
            using var res = typeof(Ui).Assembly.GetManifestResourceStream("AppLogo.png");
            if (res is null) return null;
            var ms = new MemoryStream();
            await res.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage { DecodePixelWidth = width };
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Icon-Handle des in die .exe eingebetteten App-Symbols (für Taskleiste und Alt+Tab).</summary>
    public static nint LoadExeIcon()
    {
        var module = GetModuleHandle(null);
        // Das .NET SDK legt das ApplicationIcon unter der Ressourcen-ID 32512 ab
        var icon = LoadImage(module, 32512, 1 /* IMAGE_ICON */, 0, 0, 0x40 /* LR_DEFAULTSIZE */);
        if (icon == 0 && Environment.ProcessPath is { } exe) icon = ExtractIcon(module, exe, 0);
        return icon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hinst, nint name, uint type, int cx, int cy, uint load);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIcon(nint hinst, string file, int index);

    public static void OpenPath(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static void ShowInExplorer(string path) =>
        Process.Start("explorer.exe", $"/select,\"{path}\"");

    public static void OpenUrl(string url) => OpenPath(url);

    public static async Task CopyImageAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        dp.SetStorageItems([file]);
        dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(dp);
        Clipboard.Flush();
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static void SetState(FontIcon icon, CheckState state)
    {
        (icon.Glyph, icon.Foreground) = state switch
        {
            CheckState.Ok => ("", Brush("SystemFillColorSuccessBrush")),
            CheckState.Warning => ("", Brush("SystemFillColorCautionBrush")),
            CheckState.Missing => ("", Brush("SystemFillColorCriticalBrush")),
            CheckState.Info => ("", Brush("TextFillColorSecondaryBrush")),
            _ => ("", Brush("TextFillColorTertiaryBrush")),
        };
    }

    /// <summary>Taskleistensymbol blinken lassen, bis das Fenster wieder vorne ist.</summary>
    public static void FlashTaskbar()
    {
        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = Hwnd,
                dwFlags = 0x3 | 0xC, // FLASHW_ALL | FLASHW_TIMERNOFG
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);
}

/// <summary>Threadsicherer Ringpuffer für die Blender-Ausgabe.</summary>
internal sealed class LogBuffer(int capacity)
{
    private readonly Queue<string> _lines = new();
    private bool _changed;

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity) _lines.Dequeue();
            _changed = true;
        }
    }

    public void Clear()
    {
        lock (_lines)
        {
            _lines.Clear();
            _changed = true;
        }
    }

    public bool TakeChanged(out string text)
    {
        lock (_lines)
        {
            text = _changed ? string.Join("\n", _lines) : "";
            var was = _changed;
            _changed = false;
            return was;
        }
    }
}
