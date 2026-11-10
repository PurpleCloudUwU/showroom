using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RetroShowroom.Core;

/// <summary>
/// Tauscht die laufende RetroShowroom.exe gegen eine neue Version aus.
/// <para>
/// Eine laufende .exe lässt sich nicht überschreiben. Deshalb startet die App die neu geladene .exe aus dem
/// Update-Ordner mit <c>--update-anwenden</c>. Diese wartet ohne Fenster, bis die alte App beendet ist, kopiert sich
/// über die alte Datei und startet sie auf Wunsch neu. Es gibt kein zusätzliches Hilfsprogramm und kein Skript.
/// </para>
/// </summary>
public static class UpdateInstaller
{
    public const string ApplyFlag = "--update-anwenden";
    /// <summary>Die App wurde nach einem gescheiterten Austausch (unverändert) neu gestartet.</summary>
    public const string FailedFlag = "--update-fehler";

    public sealed record ApplyJob(string Source, string Target, int? WaitForPid, string? ReadySignal, bool Restart);

    /// <summary>
    /// Nur die veröffentlichte Einzeldatei kann sich selbst ersetzen. Bei einem Entwicklungs-Build liegt die
    /// eigentliche App als RetroShowroom.dll neben der .exe, dann bleibt das Installieren aus.
    /// (Assembly.Location taugt hier nicht als Merkmal: Die Einzeldatei entpackt auch ihre .NET-Teile nach %TEMP%.)
    /// </summary>
    public static bool IsSingleFileApp =>
        Environment.ProcessPath is { } exe && !File.Exists(Path.ChangeExtension(exe, ".dll"));

    /// <summary>
    /// Erkennt den Aufruf „Update anwenden“. Rückgabe true = dieser Prozess ist nur der Installer
    /// (dann darf keine Oberfläche starten, auch wenn die Angaben unvollständig sind und job null ist).
    /// </summary>
    public static bool TryParseArgs(IReadOnlyList<string> args, out ApplyJob? job)
    {
        job = null;
        if (!args.Contains(ApplyFlag)) return false;

        string? target = null, signal = null;
        int? pid = null;
        bool restart = false;
        foreach (var a in args)
        {
            if (a.StartsWith("--ziel=", StringComparison.Ordinal)) target = a["--ziel=".Length..];
            else if (a.StartsWith("--pid=", StringComparison.Ordinal) && int.TryParse(a.AsSpan("--pid=".Length), out int p)) pid = p;
            else if (a.StartsWith("--signal=", StringComparison.Ordinal)) signal = a["--signal=".Length..];
            else if (a == "--starten") restart = true;
        }
        if (!string.IsNullOrWhiteSpace(target) && Environment.ProcessPath is { } self)
            job = new ApplyJob(self, target, pid, signal, restart);
        return true;
    }

    /// <summary>Kann die App an ihrem Ort ersetzt werden? Gibt eine Erklärung zurück oder null, wenn alles passt.</summary>
    public static string? CheckTarget(string? targetExe)
    {
        if (!IsSingleFileApp || string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe))
            return Loc.T("Diese Kopie der App ist ein Entwicklungs-Build und kann sich nicht selbst aktualisieren.",
                         "This copy of the app is a development build and cannot update itself.");

        var updates = Path.GetFullPath(AppPaths.UpdatesDir) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(targetExe).StartsWith(updates, StringComparison.OrdinalIgnoreCase))
            return Loc.T("Die App läuft gerade aus dem Update-Ordner. Bitte die normale RetroShowroom.exe starten.",
                         "The app is running from the update folder. Please start the regular RetroShowroom.exe.");

        var dir = Path.GetDirectoryName(targetExe)!;
        try
        {
            var probe = Path.Combine(dir, $".rs-update-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Loc.T(
                $"Im Ordner der App fehlen Schreibrechte ({dir}). Bitte die neue Version von der Release-Seite laden oder die App in einen eigenen Ordner legen.",
                $"The app folder is not writable ({dir}). Please download the new version from the release page or move the app to a folder of its own.");
        }
        return null;
    }

    // ---------------------------------------------------------------- Seite der laufenden App

    /// <summary>Der gestartete Installer. <see cref="WaitReadyAsync"/> meldet, sobald er bereitsteht.</summary>
    public sealed class Handoff(Process process, EventWaitHandle? ready) : IDisposable
    {
        public Process Process => process;

        /// <summary>
        /// Wartet, bis die neue .exe gestartet ist und auf das Ende dieser App wartet. Beim ersten Start
        /// entpackt .NET erst die nativen Teile, das kann rund 20 Sekunden dauern.
        /// </summary>
        public Task<bool> WaitReadyAsync(TimeSpan timeout) => Task.Run(() =>
        {
            if (ready is null) return true;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (ready.WaitOne(200)) return true;
                if (process.HasExited) return ready.WaitOne(0);
            }
            return false;
        });

        public void Dispose()
        {
            ready?.Dispose();
            process.Dispose();
        }
    }

    /// <summary>
    /// Startet die neue .exe als Installer. Danach muss sich die App beenden, erst dann wird die Datei ersetzt.
    /// </summary>
    /// <param name="restart">Nach dem Austausch die App wieder starten.</param>
    public static Handoff Launch(string newExe, string targetExe, bool restart)
    {
        int pid = Environment.ProcessId;
        string? signalName = restart ? $"RetroShowroomUpdate_{pid}_{Guid.NewGuid():N}" : null;
        var ready = signalName is null ? null : new EventWaitHandle(false, EventResetMode.ManualReset, signalName);
        try
        {
            var psi = new ProcessStartInfo(newExe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(newExe)!,
            };
            psi.ArgumentList.Add(ApplyFlag);
            psi.ArgumentList.Add("--ziel=" + targetExe);
            psi.ArgumentList.Add("--pid=" + pid);
            if (signalName is not null) psi.ArgumentList.Add("--signal=" + signalName);
            if (restart) psi.ArgumentList.Add("--starten");

            Log($"Installer wird gestartet: {newExe} (Ziel {targetExe}, Neustart: {restart})");
            var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start hat keinen Prozess geliefert.");
            // Das neue Fenster darf später in den Vordergrund
            try { AllowSetForegroundWindow(p.Id); } catch { }
            return new Handoff(p, ready);
        }
        catch
        {
            ready?.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- Seite des Installers

    /// <summary>Läuft in der neuen .exe (ohne Oberfläche). Rückgabe 0 = Datei ersetzt.</summary>
    public static int Apply(ApplyJob job)
    {
        Log($"Update anwenden: {job.Source} → {job.Target} (Neustart: {job.Restart})");
        bool ok = false;
        try
        {
            if (job.ReadySignal is not null)
            {
                try
                {
                    using var ready = EventWaitHandle.OpenExisting(job.ReadySignal);
                    ready.Set();
                }
                catch (Exception ex)
                {
                    Log("Bereit-Signal nicht gesetzt: " + ex.Message);
                }
            }

            if (job.WaitForPid is int pid) WaitForExit(pid, job.Target, TimeSpan.FromSeconds(90));

            ok = Replace(job.Source, job.Target, TimeSpan.FromSeconds(30), out var error);
            Log(ok ? "Datei ersetzt." : "Ersetzen fehlgeschlagen, die alte Version bleibt: " + error);
        }
        catch (Exception ex)
        {
            Log("Fehler beim Anwenden: " + ex);
        }

        if (job.Restart)
        {
            try
            {
                Start(job.Target, ok ? null : FailedFlag);
            }
            catch (Exception ex)
            {
                Log("Neustart fehlgeschlagen: " + ex.Message);
            }
        }
        return ok ? 0 : 1;
    }

    private static void WaitForExit(int pid, string target, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // Die Prozessnummer könnte inzwischen einem fremden Programm gehören
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path is not null && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return;
            if (!p.WaitForExit(timeout)) Log($"Die alte App (Prozess {pid}) läuft nach {timeout.TotalSeconds:0} s noch.");
        }
        catch (ArgumentException)
        {
            // schon beendet
        }
        catch (Exception ex)
        {
            Log("Warten auf die alte App: " + ex.Message);
        }
    }

    /// <summary>
    /// Erst als „….neu“ daneben kopieren, dann in einem Schritt umbenennen. So bleibt nie eine halbe .exe liegen.
    /// Solange die alte App (oder ein Virenscanner) die Datei noch hält, wird es wiederholt versucht.
    /// </summary>
    internal static bool Replace(string source, string target, TimeSpan patience, out string? error)
    {
        error = null;
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            error = "Quelle und Ziel sind dieselbe Datei.";
            return false;
        }

        var tmp = target + ".neu";
        var sw = Stopwatch.StartNew();
        bool copied = false;
        while (true)
        {
            try
            {
                if (!copied)
                {
                    File.Copy(source, tmp, overwrite: true);
                    copied = true;
                }
                File.Move(tmp, target, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                if (sw.Elapsed > patience) break;
                Thread.Sleep(400);
            }
        }
        try { File.Delete(tmp); } catch { }
        return false;
    }

    private static void Start(string target, string? argument)
    {
        var psi = new ProcessStartInfo(target)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(target)!,
        };
        if (argument is not null) psi.ArgumentList.Add(argument);
        using var p = Process.Start(psi);
        if (p is not null)
            try { AllowSetForegroundWindow(p.Id); } catch { }
    }

    /// <summary>Kurzes Protokoll im Datenordner (update.log), weil der Installer kein Fenster hat.</summary>
    public static void Log(string message)
    {
        try
        {
            var file = AppPaths.UpdateLogFile;
            AppPaths.Ensure(AppPaths.DataDir);
            if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({Environment.ProcessId}) {message}{Environment.NewLine}");
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
