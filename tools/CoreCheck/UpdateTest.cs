// Prüft den Updater ohne Internet: Ein kleiner Server auf localhost spielt GitHub.
// Aufruf: CoreCheck --update-test
//         CoreCheck --update-live   (fragt nur das echte Release bei GitHub ab, lädt nichts herunter)
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RetroShowroom.Core;

static class UpdateTest
{
    private static int _failed;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok      " : "  FEHLER  ") + what);
        if (!ok) _failed++;
    }

    public static async Task<int> LiveAsync()
    {
        Console.WriteLine("== Abfrage bei GitHub (nur lesen) ==");
        var client = new UpdateClient();
        Console.WriteLine("  " + client.Source.LatestReleaseApi);
        var sw = Stopwatch.StartNew();
        var r = await client.GetLatestAsync();
        Console.WriteLine($"  Antwort nach {sw.ElapsedMilliseconds} ms");
        if (r is null)
        {
            Console.WriteLine("  Kein Release gefunden.");
            return 1;
        }
        Console.WriteLine($"  Version   {r.Version} (Tag {r.Tag}, „{r.Name}“)");
        Console.WriteLine($"  Datei     {r.AssetName}, {r.Size} Bytes");
        Console.WriteLine($"  SHA-256   {r.Sha256 ?? "(nicht geliefert)"}");
        Console.WriteLine($"  Download  {r.DownloadUrl}");
        Console.WriteLine($"  Seite     {r.PageUrl}");
        Console.WriteLine($"  Datum     {r.PublishedAt:u}");
        foreach (var v in new[] { "0.9.0", "1.0.0", "1.1.0" })
            Console.WriteLine($"  Installiert {v}: Update {(r.Version > Version.Parse(v) ? "verfügbar" : "nicht nötig")}");
        return 0;
    }

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("Datenordner: " + AppPaths.DataDir);

        Versions();
        Schedule();
        Settings();
        var port = FreePort();
        var source = new UpdateSource($"http://localhost:{port}/latest", $"http://localhost:{port}/dl/", $"http://localhost:{port}/releases");
        Parse(source);
        await ServerAsync(port, source);
        Apply();
        SetupCacheRoundTrip();

        Console.WriteLine(_failed == 0 ? "\nAlles in Ordnung." : $"\n{_failed} Prüfung(en) fehlgeschlagen.");
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- Versionen

    private static void Versions()
    {
        Console.WriteLine("\n== Versionsnummern ==");
        (string text, string? expected)[] cases =
        [
            ("v1.2.0", "1.2.0"), ("1.2", "1.2.0"), ("V2", "2.0.0"), ("v1.2.3-beta.1", "1.2.3"), ("1.2.3.4", "1.2.3"),
            (" v1.10.0 ", "1.10.0"), ("abc", null), ("", null), ("1..2", null), ("v1.2.3.4.5", null), ("v-1.0", null), ("1.x", null),
        ];
        foreach (var (text, expected) in cases)
        {
            bool ok = UpdateClient.TryParseVersion(text, out var v);
            Check(expected is null ? !ok : ok && v.ToString(3) == expected, $"„{text}“ → {(ok ? v.ToString(3) : "ungültig")}");
        }
        UpdateClient.TryParseVersion("v1.10.0", out var a);
        UpdateClient.TryParseVersion("v1.9.9", out var b);
        Check(a > b, "1.10.0 ist neuer als 1.9.9");
        Check(UpdateClient.Normalize(new Version(1, 0, 0, 0)) == new Version(1, 0, 0), "1.0.0.0 gilt als 1.0.0");
        Check(!(UpdateClient.Normalize(new Version(1, 0, 0, 0)) < new Version(1, 0, 0)), "gleiche Version ist kein Update");
    }

    private static void Schedule()
    {
        Console.WriteLine("\n== Zeitplan ==");
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        Check(UpdateSchedule.IsDue(UpdateSchedule.Daily, null, now, true), "täglich, noch nie geprüft → fällig");
        Check(!UpdateSchedule.IsDue(UpdateSchedule.Daily, now.AddHours(-5), now, true), "täglich, vor 5 Stunden → nicht fällig");
        Check(UpdateSchedule.IsDue(UpdateSchedule.Daily, now.AddHours(-25), now, false), "täglich, vor 25 Stunden → fällig");
        Check(!UpdateSchedule.IsDue(UpdateSchedule.Weekly, now.AddDays(-6), now, true), "wöchentlich, vor 6 Tagen → nicht fällig");
        Check(UpdateSchedule.IsDue(UpdateSchedule.Weekly, now.AddDays(-8), now, true), "wöchentlich, vor 8 Tagen → fällig");
        Check(!UpdateSchedule.IsDue(UpdateSchedule.Monthly, now.AddDays(-29), now, true), "monatlich, vor 29 Tagen → nicht fällig");
        Check(UpdateSchedule.IsDue(UpdateSchedule.Monthly, now.AddDays(-31), now, true), "monatlich, vor 31 Tagen → fällig");
        Check(UpdateSchedule.IsDue(UpdateSchedule.EveryStart, now.AddMinutes(-1), now, true), "bei jedem Start, beim Start → fällig");
        Check(!UpdateSchedule.IsDue(UpdateSchedule.EveryStart, now.AddMinutes(-90), now, false), "bei jedem Start, App läuft seit 90 Minuten → nicht fällig");
        Check(UpdateSchedule.IsDue(UpdateSchedule.EveryStart, now.AddHours(-30), now, false), "bei jedem Start, App läuft seit 30 Stunden → fällig");
        Check(!UpdateSchedule.IsDue(UpdateSchedule.Never, null, now, true), "nie → nicht fällig");
        Check(UpdateSchedule.IsDue(UpdateSchedule.Daily, now.AddDays(3), now, false), "Datum in der Zukunft (Uhr verstellt) → fällig");
        Check(UpdateSchedule.Normalize("quatsch") == UpdateSchedule.Daily, "unbekannter Wert gilt als täglich");
    }

    private static void Settings()
    {
        Console.WriteLine("\n== Einstellungen ==");
        var fresh = AppSettings.Load();
        Check(fresh.UpdateCheck == UpdateSchedule.Daily && fresh.AutoUpdate && fresh.LastUpdateCheck is null, "Standard: täglich prüfen, automatisch installieren");

        // Datei einer älteren Version ohne Update-Felder, mit unbekanntem Feld
        AppPaths.Ensure(AppPaths.DataDir);
        File.WriteAllText(AppPaths.SettingsFile, """{ "Language": "en", "UseGpu": false, "SpotWatt": 300, "EffectiveOutputFolder": "C:\\x", "Unbekannt": 1 }""");
        var old = AppSettings.Load();
        Check(old.Language == "en" && !old.UseGpu && old.SpotWatt == 300, "alte settings.json wird gelesen");
        Check(old.UpdateCheck == UpdateSchedule.Daily && old.AutoUpdate, "fehlende Update-Felder bekommen den Standard");

        var stamp = new DateTime(2026, 11, 10, 13, 14, 15, DateTimeKind.Utc);
        old.UpdateCheck = UpdateSchedule.Weekly;
        old.AutoUpdate = false;
        old.LastUpdateCheck = stamp;
        old.LastRunVersion = "1.1.0";
        old.Save();
        var back = AppSettings.Load();
        Check(back.UpdateCheck == UpdateSchedule.Weekly && !back.AutoUpdate && back.LastUpdateCheck == stamp && back.LastRunVersion == "1.1.0" && back.Language == "en",
            "Speichern und Laden ergibt dieselben Werte");
        Check(back.LastUpdateCheck?.Kind == DateTimeKind.Utc, "Zeitpunkt bleibt UTC");
        Check(!File.ReadAllText(AppPaths.SettingsFile).Contains("EffectiveOutputFolder"), "berechnete Felder landen nicht in der Datei");
        File.Delete(AppPaths.SettingsFile);
    }

    // ---------------------------------------------------------------- Antwort von GitHub lesen

    private static string ReleaseJson(UpdateSource s, string tag, string assets, string? htmlUrl = null) => $$"""
        {
          "tag_name": "{{tag}}", "name": "Showroom", "draft": false, "prerelease": false,
          "html_url": "{{htmlUrl ?? s.ReleasesPage + "/tag/" + tag}}",
          "published_at": "2026-11-10T06:06:33Z",
          "body": "Neu in dieser Version",
          "assets": [ {{assets}} ]
        }
        """;

    private static string AssetJson(string name, string url, long size, string? digest, string state = "uploaded") =>
        $$"""{ "name": "{{name}}", "state": "{{state}}", "size": {{size}}, "digest": {{(digest is null ? "null" : "\"" + digest + "\"")}}, "browser_download_url": "{{url}}" }""";

    private static void Parse(UpdateSource s)
    {
        Console.WriteLine("\n== Release lesen ==");
        var sha = new string('a', 64);
        var good = AssetJson("RetroShowroom.exe", s.DownloadPrefix + "v1.2.0/RetroShowroom.exe", 1234, "sha256:" + sha);

        var r = UpdateClient.ParseRelease(ReleaseJson(s, "v1.2.0", good), s);
        Check(r.Version == new Version(1, 2, 0) && r.AssetName == "RetroShowroom.exe" && r.Size == 1234, "Version, Datei und Größe");
        Check(r.Sha256 == sha.ToUpperInvariant(), "SHA-256 aus „digest“");
        Check(r.PageUrl == s.ReleasesPage + "/tag/v1.2.0", "Release-Seite übernommen");
        Check(r.PublishedAt?.Year == 2026, "Datum gelesen");

        // Reihenfolge egal, fremde und unpassende Dateien werden übergangen
        var mixed = string.Join(",",
            AssetJson("Quelle.zip", s.DownloadPrefix + "v1.2.0/Quelle.zip", 5, null),
            AssetJson("Anderes.exe", s.DownloadPrefix + "v1.2.0/Anderes.exe", 7, null),
            AssetJson("RetroShowroom.exe", "https://boese.example/RetroShowroom.exe", 9, null),
            AssetJson("RetroShowroom.exe", s.DownloadPrefix + "v1.2.0/halb.exe", 9, null, state: "starter"),
            good);
        r = UpdateClient.ParseRelease(ReleaseJson(s, "v1.2.0", mixed), s);
        Check(r.DownloadUrl == s.DownloadPrefix + "v1.2.0/RetroShowroom.exe", "RetroShowroom.exe wird bevorzugt, fremde Adresse übergangen");

        r = UpdateClient.ParseRelease(ReleaseJson(s, "v1.2.0", AssetJson("Showroom-Setup.exe", s.DownloadPrefix + "v1.2.0/Showroom-Setup.exe", 7, "md5:abc")), s);
        Check(r.AssetName == "Showroom-Setup.exe" && r.Sha256 is null, "andere .exe als Ersatz, unbekannte Prüfsumme wird ignoriert");

        r = UpdateClient.ParseRelease(ReleaseJson(s, "v1.2.0", good, htmlUrl: "https://boese.example/seite"), s);
        Check(r.PageUrl == s.LatestPage, "fremde Release-Seite wird nicht übernommen");

        Check(Throws(() => UpdateClient.ParseRelease(ReleaseJson(s, "v1.2.0", ""), s)), "Release ohne .exe → Fehlermeldung");
        Check(Throws(() => UpdateClient.ParseRelease(ReleaseJson(s, "neu", good), s)), "Tag ohne Versionsnummer → Fehlermeldung");
        Check(Throws(() => UpdateClient.ParseRelease("[]", s)), "unerwartete Antwort → Fehlermeldung");
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (UpdateException) { return true; }
    }

    // ---------------------------------------------------------------- Abfrage und Download gegen den Testserver

    private static async Task ServerAsync(int port, UpdateSource source)
    {
        Console.WriteLine("\n== Abfrage und Download (Testserver) ==");
        var exe = new byte[3_000_000];
        Random.Shared.NextBytes(exe);
        exe[0] = (byte)'M';
        exe[1] = (byte)'Z';
        var sha = Convert.ToHexString(SHA256.HashData(exe));

        string mode = "ok";
        int downloads = 0;
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { break; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var path = ctx.Request.Url!.AbsolutePath;
                        if (path == "/latest")
                        {
                            if (mode == "404") { ctx.Response.StatusCode = 404; }
                            else if (mode == "403") { ctx.Response.StatusCode = 403; }
                            else
                            {
                                var digest = mode == "falsche-summe" ? new string('0', 64) : sha;
                                var json = ReleaseJson(source, "v9.9.9", AssetJson("RetroShowroom.exe", source.DownloadPrefix + "v9.9.9/RetroShowroom.exe", exe.Length, "sha256:" + digest));
                                var bytes = Encoding.UTF8.GetBytes(json);
                                ctx.Response.ContentType = "application/json";
                                await ctx.Response.OutputStream.WriteAsync(bytes);
                            }
                        }
                        else if (path.StartsWith("/dl/"))
                        {
                            Interlocked.Increment(ref downloads);
                            int count = mode == "abgeschnitten" ? exe.Length / 2 : exe.Length;
                            if (mode != "abgeschnitten") ctx.Response.ContentLength64 = count;
                            ctx.Response.SendChunked = mode == "abgeschnitten";
                            for (int i = 0; i < count; i += 64 * 1024)
                            {
                                await ctx.Response.OutputStream.WriteAsync(exe.AsMemory(i, Math.Min(64 * 1024, count - i)));
                                if (mode == "langsam") await Task.Delay(40);
                            }
                        }
                        else ctx.Response.StatusCode = 404;
                    }
                    catch { }
                    finally { try { ctx.Response.Close(); } catch { } }
                });
            }
        });

        var client = new UpdateClient(source);
        var current = new Version(1, 1, 0);

        var r = await client.GetLatestAsync();
        Check(r is { Size: 3_000_000 } && r.Version == new Version(9, 9, 9), "Abfrage liefert Version 9.9.9");

        double last = -1;
        int reports = 0;
        var progress = new SyncProgress(p => { last = p.Fraction; reports++; });
        var path = await client.DownloadAsync(r!, progress);
        Check(File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(exe)), "Download vollständig und unverändert");
        Check(Path.GetFileName(path) == "RetroShowroom.exe" && path.StartsWith(Path.Combine(AppPaths.UpdatesDir, "9.9.9")), "liegt unter Updates\\9.9.9\\RetroShowroom.exe");
        Check(last == 1 && reports >= 2, $"Fortschritt gemeldet ({reports} Meldungen, zuletzt {last:P0})");
        Check(!File.Exists(path + ".part"), "kein Teil-Download übrig");
        Check(UpdateStore.LoadReady(current)?.Version == r!.Version, "als „bereit“ gemerkt");
        Check(UpdateStore.LoadReady(new Version(9, 9, 9)) is null, "für eine gleich neue App nicht mehr „bereit“");

        int before = downloads;
        var again = await client.DownloadAsync(r);
        Check(again == path && downloads == before, "vorhandene, geprüfte Datei wird nicht erneut geladen");

        File.WriteAllBytes(path, exe.AsSpan(0, exe.Length - 1).ToArray().Append((byte)(exe[^1] ^ 1)).ToArray());
        Check(!await UpdateClient.VerifyFileAsync(path, r), "veränderte Datei fällt bei der Prüfung auf");
        again = await client.DownloadAsync(r);
        Check(downloads == before + 1 && await UpdateClient.VerifyFileAsync(again, r), "veränderte Datei wird neu geladen");

        mode = "falsche-summe";
        var bad = await client.GetLatestAsync();
        Check(await FailsAsync(() => client.DownloadAsync(bad!)) && !File.Exists(path + ".part"), "falsche Prüfsumme → abgelehnt, nichts bleibt liegen");
        Check(await UpdateClient.VerifyFileAsync(path, r), "die zuvor geprüfte Datei bleibt dabei erhalten");

        File.Delete(path);
        mode = "abgeschnitten";
        Check(await FailsAsync(() => client.DownloadAsync(r)) && !File.Exists(path) && !File.Exists(path + ".part"), "abgebrochene Übertragung → abgelehnt");

        mode = "langsam";
        using (var cancel = new CancellationTokenSource(400))
        {
            bool cancelled = false;
            try { await client.DownloadAsync(r, null, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !File.Exists(path) && !File.Exists(path + ".part"), "Abbrechen räumt den Teil-Download weg");
        }

        mode = "404";
        Check(await client.GetLatestAsync() is null, "404 (noch kein Release) → kein Update, kein Fehler");
        mode = "403";
        Check(await FailsAsync(() => client.GetLatestAsync()), "403 (Limit) → verständliche Fehlermeldung");

        stop.Cancel();
        listener.Stop();
        await server;
        Check(await FailsAsync(() => client.GetLatestAsync()), "Server nicht erreichbar → verständliche Fehlermeldung");

        // Aufräumen nach der Installation
        mode = "ok";
        Directory.CreateDirectory(Path.Combine(AppPaths.UpdatesDir, "0.5.0"));
        File.WriteAllText(Path.Combine(AppPaths.UpdatesDir, "0.5.0", "RetroShowroom.exe"), "alt");
        Directory.CreateDirectory(Path.Combine(AppPaths.UpdatesDir, "9.9.9"));
        File.WriteAllBytes(path, exe);
        UpdateStore.MarkReady(r);
        UpdateStore.Cleanup(current);
        Check(!Directory.Exists(Path.Combine(AppPaths.UpdatesDir, "0.5.0")) && File.Exists(path) && UpdateStore.LoadReady(current) is not null,
            "Aufräumen: ältere Version weg, neuere bleibt");
        UpdateStore.Cleanup(new Version(9, 9, 9));
        Check(!Directory.Exists(AppPaths.UpdatesDir), "Aufräumen nach dem Update: Ordner ist leer und entfernt");
    }

    private sealed class SyncProgress(Action<DownloadProgress> a) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => a(value);
    }

    private static async Task<bool> FailsAsync<T>(Func<Task<T>> f)
    {
        try { await f(); return false; }
        catch (UpdateException ex) { Console.WriteLine("            Meldung: " + ex.Message); return true; }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---------------------------------------------------------------- Datei austauschen

    private static void Apply()
    {
        Console.WriteLine("\n== Datei austauschen ==");
        var dir = Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "austausch test")).FullName;
        var source = Path.Combine(dir, "neu.exe");
        var target = Path.Combine(dir, "RetroShowroom.exe");
        File.WriteAllText(source, "MZ neue Version");
        File.WriteAllText(target, "MZ alte Version");

        int code = UpdateInstaller.Apply(new UpdateInstaller.ApplyJob(source, target, null, null, false));
        Check(code == 0 && File.ReadAllText(target) == "MZ neue Version" && File.Exists(source), "Ziel ersetzt, Quelle bleibt");
        Check(!File.Exists(target + ".neu"), "keine Zwischendatei übrig");

        // Ziel ist noch 1,5 Sekunden gesperrt (alte App läuft noch): wird wiederholt versucht
        File.WriteAllText(target, "MZ alte Version");
        var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Run(async () => { await Task.Delay(1500); locked.Dispose(); });
        var sw = Stopwatch.StartNew();
        code = UpdateInstaller.Apply(new UpdateInstaller.ApplyJob(source, target, null, null, false));
        Check(code == 0 && File.ReadAllText(target) == "MZ neue Version" && sw.ElapsedMilliseconds >= 1200, $"gesperrtes Ziel: gewartet und ersetzt ({sw.ElapsedMilliseconds} ms)");

        // Gehört die Prozessnummer inzwischen einem fremden Programm, wird nicht darauf gewartet
        File.WriteAllText(target, "MZ alte Version");
        using var foreign = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        sw.Restart();
        code = UpdateInstaller.Apply(new UpdateInstaller.ApplyJob(source, target, foreign.Id, null, false));
        Check(code == 0 && sw.ElapsedMilliseconds < 3000 && !foreign.HasExited, $"fremder Prozess mit derselben Nummer hält nicht auf ({sw.ElapsedMilliseconds} ms)");
        try { foreign.Kill(true); } catch { }
        Check(UpdateInstaller.Apply(new UpdateInstaller.ApplyJob(source, target, 999_999_999, null, false)) == 0, "schon beendeter Prozess hält nicht auf");

        code = UpdateInstaller.Apply(new UpdateInstaller.ApplyJob(source, source, null, null, false));
        Check(code == 1, "Quelle = Ziel wird abgelehnt");

        bool isInstaller = UpdateInstaller.TryParseArgs(["--update-anwenden", "--ziel=C:\\Ein Ordner\\RetroShowroom.exe", "--pid=4711", "--signal=abc", "--starten"], out var job);
        Check(isInstaller && job is { Target: "C:\\Ein Ordner\\RetroShowroom.exe", WaitForPid: 4711, ReadySignal: "abc", Restart: true }, "Aufrufparameter gelesen");
        Check(UpdateInstaller.TryParseArgs(["--update-anwenden"], out job) && job is null, "unvollständiger Aufruf: Installer-Modus ohne Auftrag (keine Oberfläche)");
        Check(!UpdateInstaller.TryParseArgs(["--seite=einstellungen"], out _), "normaler Start wird nicht als Installer erkannt");
        Check(UpdateInstaller.CheckTarget(target) is not null, "Entwicklungs-Build installiert kein Update");
        Directory.Delete(dir, true);
    }

    // ---------------------------------------------------------------- Einrichtungs-Stand

    private static void SetupCacheRoundTrip()
    {
        Console.WriteLine("\n== Gemerkte Einrichtung ==");
        var dir = Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "blender")).FullName;
        var blender = Path.Combine(dir, "blender.exe");
        File.WriteAllText(blender, "kein echtes Blender");
        var settings = new AppSettings { BlenderPath = blender };

        var report = new SetupReport
        {
            BlenderPath = blender, BlenderProbed = true, BlenderVersion = "5.2.0", BlenderVersionParts = [5, 2, 0],
            SollumzKey = SetupReport.ExpectedSollumzKey, SollumzVersion = "2.8.1", PyMateriaFound = true,
        };
        report.SkeletonPaths.Add(blender); // existierende Datei, deren Name nicht passt
        report.Gpus.Add(new GpuDevice("OptiX", "Testkarte"));
        SetupCache.Save(report);

        var back = SetupCache.TryLoad(settings);
        Check(back is { FromCache: true, BlenderOk: true, SollumzOk: true } && back.BlenderVersion == "5.2.0" && back.SupportsDetailedLog, "Stand wird wiederhergestellt");
        Check(back?.Gpus.Count == 1 && back.Gpus[0] == new GpuDevice("OptiX", "Testkarte") && back.SkeletonPaths.Count == 1, "Grafikkarte und Skelettpfade dabei");
        Check(back?.SkeletonMale is null, "Skelette werden neu gesucht (hier: keins)");

        File.AppendAllText(blender, "!");
        Check(SetupCache.TryLoad(settings) is null, "andere blender.exe → Stand gilt nicht mehr");

        SetupCache.Save(report);
        Check(SetupCache.TryLoad(new AppSettings { BlenderPath = blender }) is not null, "neu gemerkt");
        SetupCache.Save(new SetupReport { BlenderPath = blender, BlenderProbed = true, BlenderError = "startet nicht" });
        Check(!File.Exists(AppPaths.SetupCacheFile), "Blender nicht in Ordnung → Stand wird verworfen");
        Directory.Delete(dir, true);
    }
}
