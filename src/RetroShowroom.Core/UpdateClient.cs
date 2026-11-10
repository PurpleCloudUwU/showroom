using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace RetroShowroom.Core;

/// <summary>Woher die App ihre Updates bezieht (GitHub-Releases).</summary>
public sealed record UpdateSource(string LatestReleaseApi, string DownloadPrefix, string ReleasesPage)
{
    public const string Owner = "PurpleCloudUwU";
    public const string Repo = "showroom";

    public static UpdateSource GitHub { get; } = new(
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest",
        $"https://github.com/{Owner}/{Repo}/releases/download/",
        $"https://github.com/{Owner}/{Repo}/releases");

    /// <summary>Seite des neuesten Releases (für „Versionshinweise“).</summary>
    public string LatestPage => ReleasesPage + "/latest";
}

/// <summary>Ein veröffentlichtes Release mit der passenden .exe.</summary>
public sealed record ReleaseInfo
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }
    public string? Name { get; init; }
    public string? Notes { get; init; }
    public required string PageUrl { get; init; }
    public required string AssetName { get; init; }
    public required string DownloadUrl { get; init; }
    public long Size { get; init; }
    /// <summary>SHA-256 der Datei als Hex, wenn GitHub sie mitliefert.</summary>
    public string? Sha256 { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

public readonly record struct DownloadProgress(long Received, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
}

/// <summary>Fehler mit einem Text, der dem Nutzer so angezeigt werden kann.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Wie oft nach Updates gesucht wird (Einstellung <see cref="AppSettings.UpdateCheck"/>).</summary>
public static class UpdateSchedule
{
    public const string EveryStart = "start", Daily = "daily", Weekly = "weekly", Monthly = "monthly", Never = "never";

    public static readonly string[] All = [EveryStart, Daily, Weekly, Monthly, Never];

    /// <summary>Unbekannte Werte gelten als „täglich“.</summary>
    public static string Normalize(string? mode) => All.Contains(mode) ? mode! : Daily;

    private static TimeSpan Interval(string mode) => mode switch
    {
        Weekly => TimeSpan.FromDays(7),
        Monthly => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(1),
    };

    /// <summary>Ist jetzt eine Abfrage fällig? <paramref name="atStart"/> = erste Gelegenheit nach dem Start der App.</summary>
    public static bool IsDue(string? mode, DateTime? lastCheckUtc, DateTime nowUtc, bool atStart)
    {
        mode = Normalize(mode);
        if (mode == Never) return false;
        if (mode == EveryStart && atStart) return true;
        if (lastCheckUtc is not { } last) return true;
        var since = nowUtc - last.ToUniversalTime();
        // Negativ = Uhr wurde zurückgestellt, dann lieber einmal zu oft fragen
        return since < TimeSpan.Zero || since >= Interval(mode);
    }
}

/// <summary>Fragt GitHub nach dem neuesten Release und lädt die .exe herunter (mit Prüfung von Größe und SHA-256).</summary>
public sealed class UpdateClient(UpdateSource? source = null)
{
    public const string AssetName = "RetroShowroom.exe";

    private const long MaxDownloadBytes = 1L << 30;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);
    private static readonly HttpClient Http = CreateHttp();

    public UpdateSource Source { get; } = source ?? UpdateSource.GitHub;

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxAutomaticRedirections = 5,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub verlangt einen User-Agent. Bewusst ohne Angaben zum Rechner oder Nutzer.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RetroShowroom-Updater");
        return http;
    }

    // ---------------------------------------------------------------- Abfrage

    /// <summary>Neuestes Release (ohne Entwürfe und Vorabversionen). Null, wenn es noch keins gibt.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CheckTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Source.LatestReleaseApi);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateException(Loc.T(
                    "GitHub nimmt gerade keine weiteren Abfragen an (Limit erreicht). Bitte später erneut versuchen.",
                    "GitHub is not accepting more requests right now (rate limit). Please try again later."));
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException(Loc.T($"GitHub antwortet mit Fehler {(int)resp.StatusCode}.", $"GitHub responded with error {(int)resp.StatusCode}."));

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParseRelease(json, Source);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(Loc.T("GitHub hat nicht rechtzeitig geantwortet.", "GitHub did not respond in time."));
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(Loc.T("Keine Verbindung zu GitHub: ", "Could not reach GitHub: ") + ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new UpdateException(Loc.T("Die Antwort von GitHub ist nicht lesbar.", "The response from GitHub could not be read."), ex);
        }
    }

    /// <summary>Liest die Antwort von „releases/latest“. Wirft <see cref="UpdateException"/>, wenn Version oder .exe fehlen.</summary>
    public static ReleaseInfo ParseRelease(string json, UpdateSource source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new UpdateException(Loc.T("Die Antwort von GitHub ist nicht lesbar.", "The response from GitHub could not be read."));

        var tag = Str(root, "tag_name");
        if (!TryParseVersion(tag, out var version))
            throw new UpdateException(Loc.T(
                $"Die Versionsnummer des Releases ist nicht lesbar („{tag}“). Erwartet wird ein Tag wie v1.2.0.",
                $"The version number of the release could not be read (\"{tag}\"). Expected a tag like v1.2.0."));

        string? name = null, url = null, digest = null;
        long size = 0;
        bool exact = false;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var n = Str(a, "name");
                var u = Str(a, "browser_download_url");
                if (n is null || u is null || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (Str(a, "state") is { } state && state != "uploaded") continue;
                // Nur Dateien aus genau diesem Repository annehmen
                if (!u.StartsWith(source.DownloadPrefix, StringComparison.Ordinal)) continue;

                bool isExact = n.Equals(AssetName, StringComparison.OrdinalIgnoreCase);
                if (name is not null && (exact || !isExact)) continue;
                (name, url, exact) = (n, u, isExact);
                size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var l) ? l : 0;
                digest = Str(a, "digest");
            }
        }
        if (name is null || url is null)
            throw new UpdateException(Loc.T(
                $"Version {version.ToString(3)} ist veröffentlicht, aber im Release fehlt die Datei {AssetName} (noch).",
                $"Version {version.ToString(3)} has been published, but the file {AssetName} is missing from the release (so far)."));

        var page = Str(root, "html_url");
        return new ReleaseInfo
        {
            Version = version,
            Tag = tag!,
            Name = Str(root, "name"),
            Notes = Str(root, "body") is { } body ? (body.Length > 4000 ? body[..4000] : body) : null,
            // Die Seite wird später im Browser geöffnet: nur Adressen dieses Repositorys zulassen
            PageUrl = page is not null && page.StartsWith(source.ReleasesPage + "/", StringComparison.Ordinal) ? page : source.LatestPage,
            AssetName = name,
            DownloadUrl = url,
            Size = size,
            Sha256 = ParseSha256(digest),
            PublishedAt = root.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String && pub.TryGetDateTimeOffset(out var d) ? d : null,
        };
    }

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ParseSha256(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : null;
    }

    /// <summary>„v1.2.0“, „1.2“ oder „v1.2.0-beta“ → 1.2.0. Immer drei Stellen.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.AsSpan().Trim();
        if (s.Length > 0 && s[0] is 'v' or 'V') s = s[1..];
        int cut = s.IndexOfAny('-', '+', ' ');
        if (cut >= 0) s = s[..cut];

        Span<int> parts = stackalloc int[3];
        int count = 0;
        foreach (var range in s.Split('.'))
        {
            if (count == 4) return false;
            var p = s[range];
            if (p.Length == 0 || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) return false;
            if (count < 3) parts[count] = value;
            count++;
        }
        if (count == 0) return false;
        version = new Version(parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>Auf drei Stellen bringen (1.2.0.0 und 1.2 werden zu 1.2.0), damit Vergleiche stimmen.</summary>
    public static Version Normalize(Version? v) =>
        v is null ? new Version(0, 0, 0) : new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    // ---------------------------------------------------------------- Download

    /// <summary>
    /// Lädt die .exe des Releases in den Update-Ordner und prüft sie. Gibt den Pfad zurück.
    /// Liegt die geprüfte Datei schon dort, wird nichts geladen.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var target = UpdateStore.PathFor(release);
        var dir = AppPaths.Ensure(Path.GetDirectoryName(target)!);
        if (await VerifyFileAsync(target, release, ct).ConfigureAwait(false))
        {
            UpdateStore.MarkReady(release);
            return target;
        }

        EnsureFreeSpace(dir, release.Size);
        var part = target + ".part";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException(Loc.T($"Der Server antwortet mit Fehler {(int)resp.StatusCode}.", $"The server responded with error {(int)resp.StatusCode}."));

            long total = release.Size > 0 ? release.Size : resp.Content.Headers.ContentLength ?? 0;
            if (total > MaxDownloadBytes) throw TooLarge();

            long received = 0;
            string hashHex;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(1 << 17);
                try
                {
                    await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    long lastReport = Environment.TickCount64;
                    progress?.Report(new(0, total));
                    while (true)
                    {
                        // Kommt 45 Sekunden lang nichts mehr, gilt die Verbindung als hängen geblieben
                        stall.CancelAfter(StallTimeout);
                        int n;
                        try
                        {
                            n = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new UpdateException(Loc.T("Die Verbindung ist hängen geblieben.", "The connection stalled."));
                        }
                        if (n == 0) break;
                        received += n;
                        if (received > MaxDownloadBytes || (release.Size > 0 && received > release.Size)) throw TooLarge();
                        hash.AppendData(buffer, 0, n);
                        await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);

                        long now = Environment.TickCount64;
                        if (now - lastReport >= 150)
                        {
                            lastReport = now;
                            progress?.Report(new(received, total));
                        }
                    }
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                hashHex = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (release.Size > 0 && received != release.Size)
                throw new UpdateException(Loc.T(
                    $"Der Download ist unvollständig ({received} von {release.Size} Bytes).",
                    $"The download is incomplete ({received} of {release.Size} bytes)."));
            if (release.Sha256 is not null && !hashHex.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(Loc.T(
                    "Die Prüfsumme der geladenen Datei stimmt nicht mit der von GitHub überein. Die Datei wurde verworfen.",
                    "The checksum of the downloaded file does not match the one from GitHub. The file was discarded."));
            if (!LooksLikeExe(part))
                throw new UpdateException(Loc.T("Die geladene Datei ist kein Windows-Programm.", "The downloaded file is not a Windows program."));

            File.Move(part, target, overwrite: true);
            UpdateStore.MarkReady(release);
            progress?.Report(new(received, received));
            return target;
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            if (ex is HttpRequestException or IOException)
                throw new UpdateException(ex.Message, ex);
            throw;
        }

        static UpdateException TooLarge() =>
            new(Loc.T("Die Datei ist größer als angekündigt. Der Download wurde abgebrochen.", "The file is larger than announced. The download was cancelled."));
    }

    /// <summary>Stimmen Größe, Dateikopf und (falls bekannt) SHA-256 mit dem Release überein?</summary>
    public static async Task<bool> VerifyFileAsync(string path, ReleaseInfo release, CancellationToken ct = default)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 1024 || (release.Size > 0 && fi.Length != release.Size)) return false;
            if (!LooksLikeExe(path)) return false;
            if (release.Sha256 is null) return true;

            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 17,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool LooksLikeExe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureFreeSpace(string dir, long size)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;
        }
        catch
        {
            return; // z. B. Netzlaufwerk: lässt sich nicht prüfen
        }
        // Platz für den Download und für die Kopie beim Installieren
        long need = Math.Max(size, 0) * 2 + (64L << 20);
        if (free < need)
            throw new UpdateException(Loc.T(
                $"Zu wenig freier Speicherplatz für das Update (benötigt rund {need >> 20} MB).",
                $"Not enough free disk space for the update (about {need >> 20} MB needed)."));
    }
}

/// <summary>Ablage der heruntergeladenen Updates im Datenordner (Updates\&lt;Version&gt;\RetroShowroom.exe).</summary>
public static class UpdateStore
{
    private static string ReadyFile => Path.Combine(AppPaths.UpdatesDir, "bereit.json");

    /// <summary>
    /// Wohin die .exe eines Releases gespeichert wird. Der Dateiname bleibt „RetroShowroom.exe“, damit .NET
    /// die nativen Teile nur einmal entpackt (der Ordner unter %TEMP%\.net hängt am Dateinamen).
    /// </summary>
    public static string PathFor(ReleaseInfo release) =>
        Path.Combine(AppPaths.UpdatesDir, release.Version.ToString(3), SafeExeName(release.AssetName));

    private static string SafeExeName(string name)
    {
        var file = Path.GetFileName(name.Replace('/', '\\'));
        var invalid = Path.GetInvalidFileNameChars();
        if (file.Length is 0 or > 100 || file.IndexOfAny(invalid) >= 0 || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return UpdateClient.AssetName;
        return file;
    }

    /// <summary>Merkt sich, dass dieses Release fertig geladen und geprüft im Update-Ordner liegt.</summary>
    public static void MarkReady(ReleaseInfo release)
    {
        try
        {
            AppPaths.Ensure(AppPaths.UpdatesDir);
            var tmp = ReadyFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(release, CoreJsonContext.Default.ReleaseInfo));
            File.Move(tmp, ReadyFile, true);
        }
        catch { }
    }

    /// <summary>Ein früher geladenes, noch nicht installiertes Update, das neuer ist als <paramref name="current"/>.</summary>
    public static ReleaseInfo? LoadReady(Version current)
    {
        try
        {
            if (!File.Exists(ReadyFile)) return null;
            var release = JsonSerializer.Deserialize(File.ReadAllText(ReadyFile), CoreJsonContext.Default.ReleaseInfo);
            if (release is null || UpdateClient.Normalize(release.Version) <= current) return null;
            return File.Exists(PathFor(release)) ? release : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Räumt nach einem Update die entpackten Teile älterer Versionen weg. .NET entpackt jede Version der .exe
    /// einmal nach %TEMP%\.net\RetroShowroom\&lt;Kennung&gt; (rund 160 MB) und löscht das nie von selbst.
    /// Ordner, aus denen gerade noch eine andere Instanz läuft, bleiben stehen.
    /// </summary>
    public static void RemoveOldUnpackedVersions()
    {
        try
        {
#pragma warning disable IL3000 // Die Einzeldatei entpackt auch ihre .NET-Teile, der Pfad zeigt in den Entpack-Ordner
            var current = Path.GetDirectoryName(typeof(UpdateStore).Assembly.Location);
#pragma warning restore IL3000
            if (!UpdateInstaller.IsSingleFileApp || string.IsNullOrEmpty(current)) return;
            var parent = Path.GetDirectoryName(current);
            // Nur im erwarteten Aufbau …\.net\<Name der .exe>\<Kennung> etwas löschen
            if (parent is null || !string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), ".net", StringComparison.OrdinalIgnoreCase)) return;

            foreach (var dir in Directory.GetDirectories(parent))
            {
                if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
                if (InUse(dir)) continue;
                try
                {
                    Directory.Delete(dir, true);
                    UpdateInstaller.Log("Entpackte Teile einer älteren Version entfernt: " + dir);
                }
                catch { }
            }
        }
        catch { }

        static bool InUse(string dir)
        {
            foreach (var name in new[] { "coreclr.dll", "Microsoft.ui.xaml.dll" })
            {
                var file = Path.Combine(dir, name);
                if (!File.Exists(file)) continue;
                try
                {
                    using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Entfernt Downloads von Versionen, die schon installiert (oder älter) sind, und liegen gebliebene Teil-Downloads.</summary>
    public static void Cleanup(Version current)
    {
        try
        {
            var root = AppPaths.UpdatesDir;
            if (!Directory.Exists(root)) return;
            // Läuft die App selbst aus dem Update-Ordner, nichts anfassen
            if (Environment.ProcessPath is { } exe && Path.GetFullPath(exe).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var dir in Directory.GetDirectories(root))
            {
                bool known = UpdateClient.TryParseVersion(Path.GetFileName(dir), out var v);
                if (known && v > current)
                {
                    foreach (var part in Directory.GetFiles(dir, "*.part"))
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > TimeSpan.FromDays(1))
                            try { File.Delete(part); } catch { }
                    continue;
                }
                try { Directory.Delete(dir, true); } catch { }
            }

            if (File.Exists(ReadyFile) && LoadReady(current) is null)
                try { File.Delete(ReadyFile); } catch { }
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch { }
    }
}
