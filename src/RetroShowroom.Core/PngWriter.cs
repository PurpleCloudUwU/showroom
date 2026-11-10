using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace RetroShowroom.Core;

/// <summary>Minimaler PNG-Schreiber (RGBA, 8 Bit) und -Leser für Kopfdaten.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Rohdaten je Band (rund 1 MB). Fest statt nach Kernzahl, damit auf jedem Rechner dieselbe Datei entsteht.</summary>
    private const int BandBytes = 1 << 20;

    /// <summary>
    /// Schreibt ein RGBA-Bild als PNG. Das Komprimieren ist der teure Teil, deshalb wird das Bild in Bänder
    /// von rund 1 MB geteilt, die parallel komprimiert werden. Jedes Band endet an einer Bytegrenze
    /// (Sync-Flush), so lassen sich die Stücke zu einem gültigen Deflate-Strom aneinanderhängen.
    /// </summary>
    public static void WriteRgba(string path, int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Breite und Höhe müssen größer als 0 sein.");
        if (rgba.Length < (long)width * height * 4) throw new ArgumentException("Zu wenige Pixeldaten.", nameof(rgba));

        int row = width * 4;
        int rowsPerBand = Math.Max(1, BandBytes / (row + 1));
        int bands = (height + rowsPerBand - 1) / rowsPerBand;
        var compressed = new MemoryStream[bands];
        var lengths = new int[bands];
        var adlers = new uint[bands];

        void Compress(int band)
        {
            int first = band * rowsPerBand, end = Math.Min(height, first + rowsPerBand);
            var output = new MemoryStream((end - first) * (row + 1) / 2 + 64);
            var line = ArrayPool<byte>.Shared.Rent(row + 1);
            try
            {
                uint adler = 1;
                var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true);
                for (int y = first; y < end; y++)
                {
                    FilterSub(rgba.AsSpan(y * row, row), line.AsSpan(0, row + 1));
                    adler = Adler32(adler, line.AsSpan(0, row + 1));
                    deflate.Write(line, 0, row + 1);
                }
                if (band == bands - 1)
                {
                    // Letztes Band: Dispose schreibt den Abschlussblock
                    deflate.Dispose();
                    lengths[band] = (int)output.Length;
                }
                else
                {
                    // Sync-Flush: alles ausgeben und auf eine Bytegrenze auffüllen. Den Abschlussblock, den
                    // Dispose danach noch anhängt, lassen wir weg (er steht hinter der gemerkten Länge).
                    deflate.Flush();
                    lengths[band] = (int)output.Length;
                    deflate.Dispose();
                }
                adlers[band] = adler;
                compressed[band] = output;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(line);
            }
        }

        if (bands == 1) Compress(0);
        else Parallel.For(0, bands, Compress);

        // Prüfsumme über die ungepackten Daten aus den Teilsummen der Bänder zusammensetzen
        uint total = adlers[0];
        long dataLength = 2 + 4 + lengths[0];
        for (int b = 1; b < bands; b++)
        {
            int rows = Math.Min(height, (b + 1) * rowsPerBand) - b * rowsPerBand;
            total = Adler32Combine(total, adlers[b], (long)rows * (row + 1));
            dataLength += lengths[b];
        }
        if (dataLength > int.MaxValue) throw new InvalidOperationException("Das Bild ist zu groß für einen PNG-Block.");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        fs.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // Bit-Tiefe
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(fs, "IHDR", ihdr);

        // IDAT: zlib-Kopf, die Bänder, Adler-32
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, (int)dataLength);
        fs.Write(buf);
        uint crc = 0xFFFFFFFFu;
        Emit(fs, ref crc, "IDAT"u8);
        Emit(fs, ref crc, [0x78, 0x01]);
        for (int b = 0; b < bands; b++)
            Emit(fs, ref crc, compressed[b].GetBuffer().AsSpan(0, lengths[b]));
        BinaryPrimitives.WriteUInt32BigEndian(buf, total);
        Emit(fs, ref crc, buf);
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc ^ 0xFFFFFFFFu);
        fs.Write(buf);

        WriteChunk(fs, "IEND", []);
    }

    private static void Emit(Stream s, ref uint crc, ReadOnlySpan<byte> data)
    {
        s.Write(data);
        crc = Crc32.Update(crc, data);
    }

    /// <summary>PNG-Filter „Sub“: jedes Byte minus das Byte vier Stellen davor (gleicher Kanal im Pixel links).</summary>
    private static void FilterSub(ReadOnlySpan<byte> source, Span<byte> line)
    {
        line[0] = 1;
        var target = line[1..];
        source[..4].CopyTo(target);
        int i = 4;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= source.Length - Vector<byte>.Count; i += Vector<byte>.Count)
                (new Vector<byte>(source[i..]) - new Vector<byte>(source[(i - 4)..])).CopyTo(target[i..]);
        }
        for (; i < source.Length; i++) target[i] = (byte)(source[i] - source[i - 4]);
    }

    private const uint AdlerBase = 65521;

    private static uint Adler32(uint adler, ReadOnlySpan<byte> data)
    {
        uint a = adler & 0xFFFF, b = adler >> 16;
        while (data.Length > 0)
        {
            // 5552 Bytes passen in 32 Bit, bevor der Rest gebildet werden muss
            int n = Math.Min(data.Length, 5552);
            foreach (byte x in data[..n])
            {
                a += x;
                b += a;
            }
            a %= AdlerBase;
            b %= AdlerBase;
            data = data[n..];
        }
        return (b << 16) | a;
    }

    /// <summary>Adler-32 zweier aneinandergehängter Abschnitte aus den Einzelsummen (wie adler32_combine in zlib).</summary>
    private static uint Adler32Combine(uint first, uint second, long secondLength)
    {
        uint rem = (uint)(secondLength % AdlerBase);
        uint sum1 = first & 0xFFFF;
        uint sum2 = rem * sum1 % AdlerBase;
        sum1 += (second & 0xFFFF) + AdlerBase - 1;
        sum2 += (first >> 16) + (second >> 16) + AdlerBase - rem;
        if (sum1 >= AdlerBase) sum1 -= AdlerBase;
        if (sum1 >= AdlerBase) sum1 -= AdlerBase;
        if (sum2 >= AdlerBase << 1) sum2 -= AdlerBase << 1;
        if (sum2 >= AdlerBase) sum2 -= AdlerBase;
        return sum1 | (sum2 << 16);
    }

    public readonly record struct PngInfo(int Width, int Height, byte ColorType, bool HasTransparencyChunk)
    {
        public bool HasAlpha => ColorType is 4 or 6 || HasTransparencyChunk;
    }

    /// <summary>Liest Breite, Höhe und Farbtyp. Null, wenn keine gültige PNG.</summary>
    public static PngInfo? ReadInfo(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[8];
            if (fs.Read(sig) != 8 || !sig.SequenceEqual(Signature)) return null;
            Span<byte> head = stackalloc byte[8];
            int w = 0, h = 0; byte ct = 0; bool trns = false; bool gotHeader = false;
            while (fs.Read(head) == 8)
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(head);
                string type = Encoding.ASCII.GetString(head[4..]);
                if (type == "IHDR")
                {
                    var buf = new byte[13];
                    fs.ReadExactly(buf);
                    w = BinaryPrimitives.ReadInt32BigEndian(buf);
                    h = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(4));
                    ct = buf[9];
                    gotHeader = true;
                    fs.Seek(len - 13 + 4, SeekOrigin.Current);
                    continue;
                }
                if (type == "tRNS") trns = true;
                if (type is "IDAT" or "IEND") break;
                fs.Seek(len + 4L, SeekOrigin.Current);
            }
            return gotHeader ? new PngInfo(w, h, ct, trns) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        Span<byte> t = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, t);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    /// <summary>CRC-32 der PNG-Blöcke, acht Bytes je Schritt („Slicing by 8“) statt eines.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var table = new uint[8 * 256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            for (int k = 1; k < 8; k++)
            for (int n = 0; n < 256; n++)
            {
                uint prev = table[(k - 1) * 256 + n];
                table[k * 256 + n] = (prev >> 8) ^ table[prev & 0xFF];
            }
            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            var t = Table;
            while (data.Length >= 8)
            {
                uint lo = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
                uint hi = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                crc = t[7 * 256 + (lo & 0xFF)] ^ t[6 * 256 + ((lo >> 8) & 0xFF)] ^ t[5 * 256 + ((lo >> 16) & 0xFF)] ^ t[4 * 256 + (lo >> 24)]
                      ^ t[3 * 256 + (hi & 0xFF)] ^ t[2 * 256 + ((hi >> 8) & 0xFF)] ^ t[256 + ((hi >> 16) & 0xFF)] ^ t[hi >> 24];
                data = data[8..];
            }
            foreach (var b in data) crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
