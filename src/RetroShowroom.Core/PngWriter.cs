using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RetroShowroom.Core;

/// <summary>Minimaler PNG-Schreiber (RGBA, 8 Bit) und -Leser für Kopfdaten.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void WriteRgba(string path, int width, int height, byte[] rgba)
    {
        if (rgba.Length < width * height * 4) throw new ArgumentException("Zu wenige Pixeldaten.", nameof(rgba));

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        fs.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // Bit-Tiefe
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(fs, "IHDR", ihdr);

        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                int row = width * 4;
                var line = new byte[row + 1];
                line[0] = 1; // Filter „Sub“
                for (int y = 0; y < height; y++)
                {
                    int o = y * row;
                    for (int i = 0; i < 4; i++) line[1 + i] = rgba[o + i];
                    for (int i = 4; i < row; i++) line[1 + i] = (byte)(rgba[o + i] - rgba[o + i - 4]);
                    z.Write(line);
                }
            }
            WriteChunk(fs, "IDAT", ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        WriteChunk(fs, "IEND", []);
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

    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
