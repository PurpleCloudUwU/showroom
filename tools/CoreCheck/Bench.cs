// Misst die Textur-Umwandlung (BC dekodieren, PNG schreiben) mit erzeugten Testdaten.
// Aufruf: CoreCheck --bench [kantenlänge]
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using RetroShowroom.Core;

static class Bench
{
    public static void Run(int size)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "RetroShowroomBench")).FullName;
        SelfTest(dir);

        Console.WriteLine($"Testtextur {size} × {size}, {Environment.ProcessorCount} Kerne");
        var rgba = Pattern(size);

        foreach (var (format, bc) in new[]
                 {
                     (TextureFormat.BC1, CompressionFormat.Bc1),
                     (TextureFormat.BC3, CompressionFormat.Bc3),
                     (TextureFormat.BC7, CompressionFormat.Bc7),
                 })
        {
            var encoder = new BcEncoder();
            encoder.OutputOptions.Format = bc;
            encoder.OutputOptions.Quality = CompressionQuality.Fast;
            encoder.OutputOptions.GenerateMipMaps = false;
            byte[] raw = encoder.EncodeToRawBytes(rgba, size, size, PixelFormat.Rgba32, 0, out _, out _);

            var tex = new YtdTexture
            {
                Name = "bench", Width = size, Height = size, Stride = 0, MipLevels = 1, Format = format, RawFormat = 0,
                DataLength = raw.Length, Source = raw, SourceOffset = 0,
            };

            byte[] pixels = TextureDecoder.ToRgba(tex); // aufwärmen
            double decode = Best(5, () => pixels = TextureDecoder.ToRgba(tex));

            var png = Path.Combine(dir, $"bench_{format}.png");
            PngWriter.WriteRgba(png, size, size, pixels);
            double write = Best(4, () => PngWriter.WriteRgba(png, size, size, pixels));

            Console.WriteLine($"  {format}: dekodieren {decode,6:0.0} ms   PNG schreiben {write,6:0.0} ms   " +
                              $"({new FileInfo(png).Length / 1024} KB, Pixel {Hash(pixels)}, PNG {Hash(File.ReadAllBytes(png))})");
        }
        Directory.Delete(dir, true);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..12];

    /// <summary>
    /// Liest geschriebene PNGs mit eigenem, unabhängigem Code zurück (Blockprüfsummen, zlib samt Adler-32, Filter)
    /// und vergleicht jedes Pixel. Deckt ein Band, viele Bänder und krumme Größen ab.
    /// </summary>
    private static void SelfTest(string dir)
    {
        Console.WriteLine("PNG-Selbsttest");
        var rnd = new Random(7);
        int failed = 0;
        foreach (var (w, h, noise) in new[]
                 {
                     (1, 1, true), (3, 5, true), (7, 1, true), (1, 9, true), (64, 64, false), (1000, 7, true), (513, 511, false),
                     (2048, 2048, false), (1024, 1024, true), (4096, 300, false), (300000, 3, true), (5, 70000, false),
                 })
        {
            var pixels = new byte[w * h * 4];
            if (noise) rnd.NextBytes(pixels);
            else for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)((i * 7 / 4 + i / (w * 4)) ^ (i >> 9));
            var path = Path.Combine(dir, "selbsttest.png");
            PngWriter.WriteRgba(path, w, h, pixels);
            string? problem = Verify(path, w, h, pixels);
            if (problem is not null) failed++;
            Console.WriteLine($"  {(problem is null ? "ok     " : "FEHLER ")} {w} × {h}{(noise ? " (Rauschen)" : "")}{(problem is null ? "" : ": " + problem)}");
        }
        if (failed > 0) Environment.ExitCode = 1;
    }

    private static string? Verify(string path, int width, int height, byte[] pixels)
    {
        var file = File.ReadAllBytes(path);
        if (!file.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "Signatur";
        var info = PngWriter.ReadInfo(path);
        if (info is not { } pi || pi.Width != width || pi.Height != height || pi.ColorType != 6) return "Kopfdaten";

        using var idat = new MemoryStream();
        var seen = new List<string>();
        for (int pos = 8; pos < file.Length;)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
            string type = Encoding.ASCII.GetString(file, pos + 4, 4);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos + 8 + len));
            if (crc != SimpleCrc(file.AsSpan(pos + 4, 4 + len))) return "Prüfsumme des Blocks " + type;
            if (type == "IDAT") idat.Write(file, pos + 8, len);
            seen.Add(type);
            pos += 12 + len;
        }
        if (string.Join(",", seen.Distinct()) != "IHDR,IDAT,IEND") return "Blöcke " + string.Join(",", seen);

        // zlib entpacken (prüft dabei die Adler-32 am Ende) ...
        int row = width * 4;
        var raw = new byte[(long)height * (row + 1)];
        var packed = idat.ToArray();
        try
        {
            using var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
            z.ReadExactly(raw);
            if (z.ReadByte() >= 0) return "zu viele Daten";
        }
        catch (Exception ex)
        {
            return "zlib: " + ex.Message;
        }
        // ... die Adler-32 zusätzlich selbst nachrechnen ...
        uint a = 1, b = 0;
        foreach (byte x in raw) { a = (a + x) % 65521; b = (b + a) % 65521; }
        if (BinaryPrimitives.ReadUInt32BigEndian(packed.AsSpan(packed.Length - 4)) != ((b << 16) | a)) return "Adler-32";

        // ... und den Filter zurückrechnen
        for (int y = 0; y < height; y++)
        {
            int o = y * (row + 1);
            if (raw[o] != 1) return "Filtertyp";
            for (int i = 0; i < row; i++)
            {
                byte v = (byte)(raw[o + 1 + i] + (i >= 4 ? raw[o + 1 + i - 4] : 0));
                raw[o + 1 + i] = v;
                if (v != pixels[y * row + i]) return $"Pixel in Zeile {y}";
            }
        }
        return null;
    }

    private static uint SimpleCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte x in data)
        {
            crc ^= x;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static double Best(int runs, Action a)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            a();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    /// <summary>Stoffähnliches Muster: Verlauf, Streifen und etwas Rauschen (fester Startwert, also immer gleich).</summary>
    private static byte[] Pattern(int size)
    {
        var rnd = new Random(1234);
        var p = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = (y * size + x) * 4;
            int n = rnd.Next(24);
            p[i] = (byte)Math.Min(255, x * 255 / size + n);
            p[i + 1] = (byte)Math.Min(255, y * 255 / size + n);
            p[i + 2] = (byte)(((x / 16 + y / 16) & 1) * 120 + n);
            p[i + 3] = (byte)(x < size / 2 ? 255 : 128 + n);
        }
        return p;
    }
}
