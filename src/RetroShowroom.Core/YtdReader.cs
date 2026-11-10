using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RetroShowroom.Core;

public enum TextureFormat { Unknown, BC1, BC2, BC3, BC4, BC5, BC7, A8R8G8B8, A8B8G8R8, A1R5G5B5, A8, L8 }

public sealed class YtdTexture
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required int MipLevels { get; init; }
    public required TextureFormat Format { get; init; }
    public required uint RawFormat { get; init; }
    /// <summary>Rohdaten der größten Mip-Stufe.</summary>
    public required byte[] Mip0 { get; init; }

    public string FormatName => Format switch
    {
        TextureFormat.BC1 => "BC1 (DXT1)",
        TextureFormat.BC2 => "BC2 (DXT3)",
        TextureFormat.BC3 => "BC3 (DXT5)",
        TextureFormat.BC4 => "BC4 (ATI1)",
        TextureFormat.BC5 => "BC5 (ATI2)",
        TextureFormat.Unknown => Loc.T("unbekannt", "unknown") + $" (0x{RawFormat:X8})",
        _ => Format.ToString(),
    };
}

/// <summary>
/// Liest Texturen aus einer GTA-V-Texturdatei (.ytd, RSC7, Legacy/PC).
/// Aufbau wie in CodeWalker: Resource-Header, deflate-komprimierter System- und Grafikteil,
/// darin ein pgDictionary&lt;grcTexturePC&gt;.
/// </summary>
public static class YtdReader
{
    private const uint Rsc7Magic = 0x37435352; // "RSC7"

    public static IReadOnlyList<YtdTexture> Read(string path) => Read(File.ReadAllBytes(path));

    public static IReadOnlyList<YtdTexture> Read(byte[] file)
    {
        var (view, _) = Open(file);

        ulong listPtr = view.U64(0x30);
        int count = view.U16(0x38);
        long listOff = view.Resolve(listPtr);
        if (count == 0 || listOff < 0)
            throw new InvalidDataException(Loc.T("Die YTD enthält keine Texturen.", "The YTD contains no textures."));

        var result = new List<YtdTexture>(count);
        for (int i = 0; i < count; i++)
        {
            long t = view.Resolve(view.U64(listOff + i * 8L));
            if (t >= 0) result.Add(ReadTexture(view, t));
        }
        if (result.Count == 0)
            throw new InvalidDataException(Loc.T("Die YTD enthält keine lesbaren Texturen.", "The YTD contains no readable textures."));
        return result;
    }

    /// <summary>Kurzinfo zum Resource-Header (für Fehlersuche).</summary>
    public static string Describe(string path)
    {
        var file = File.ReadAllBytes(path);
        var (view, version) = Open(file);
        return $"RSC7 v{version}, System {view.SystemSize} B, entpackt {view.Length} B";
    }

    private static (View view, uint version) Open(byte[] file)
    {
        if (file.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Rsc7Magic)
            throw new InvalidDataException(Loc.T(
                "Die Datei ist keine GTA-V-Ressource (RSC7). Bitte eine .ytd aus OpenIV oder CodeWalker verwenden.",
                "The file is not a GTA V resource (RSC7). Please use a .ytd exported from OpenIV or CodeWalker."));

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        uint sysFlags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        int sysSize = PageFlagsSize(sysFlags);

        byte[] data;
        try
        {
            data = Inflate(file, 16);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException(Loc.T(
                "Die YTD lässt sich nicht entpacken (verschlüsselt oder beschädigt). Bitte mit OpenIV oder CodeWalker exportieren.",
                "The YTD cannot be decompressed (encrypted or damaged). Please export it with OpenIV or CodeWalker."));
        }

        if (sysSize < 0x40 || data.Length < sysSize)
            throw new InvalidDataException(Loc.T("Unerwarteter Aufbau der YTD (Seitengrößen passen nicht).", "Unexpected YTD layout (page sizes do not match)."));
        return (new View(data, sysSize), version);
    }

    private static YtdTexture ReadTexture(View v, long t)
    {
        string name = v.CString(v.Resolve(v.U64(t + 0x28))) ?? $"textur_{t:X}";
        int width = v.U16(t + 0x50);
        int height = v.U16(t + 0x52);
        int stride = v.U16(t + 0x56);
        uint raw = v.U32(t + 0x58);
        int levels = v.U8(t + 0x5D);
        long dataOff = v.Resolve(v.U64(t + 0x70));
        var format = MapFormat(raw);

        byte[] mip0 = [];
        int size = Mip0Size(format, width, height, stride);
        if (format != TextureFormat.Unknown && width > 0 && height > 0 && dataOff >= 0)
        {
            if (dataOff + size > v.Length)
                throw new InvalidDataException(Loc.T($"Textur „{name}“ ist abgeschnitten.", $"Texture \"{name}\" is truncated."));
            mip0 = v.Slice(dataOff, size);
        }

        return new YtdTexture
        {
            Name = name, Width = width, Height = height, Stride = stride, MipLevels = levels,
            Format = format, RawFormat = raw, Mip0 = mip0,
        };
    }

    private static TextureFormat MapFormat(uint raw) => raw switch
    {
        0x31545844 => TextureFormat.BC1, // "DXT1"
        0x33545844 => TextureFormat.BC2, // "DXT3"
        0x35545844 => TextureFormat.BC3, // "DXT5"
        0x31495441 => TextureFormat.BC4, // "ATI1"
        0x32495441 => TextureFormat.BC5, // "ATI2"
        0x20374342 => TextureFormat.BC7, // "BC7 "
        21 => TextureFormat.A8R8G8B8,
        32 => TextureFormat.A8B8G8R8,
        25 => TextureFormat.A1R5G5B5,
        28 => TextureFormat.A8,
        50 => TextureFormat.L8,
        _ => TextureFormat.Unknown,
    };

    internal static int Mip0Size(TextureFormat f, int w, int h, int stride)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        return f switch
        {
            TextureFormat.BC1 or TextureFormat.BC4 => bw * bh * 8,
            TextureFormat.BC2 or TextureFormat.BC3 or TextureFormat.BC5 or TextureFormat.BC7 => bw * bh * 16,
            TextureFormat.A8R8G8B8 or TextureFormat.A8B8G8R8 => Math.Max(stride, w * 4) * h,
            TextureFormat.A1R5G5B5 => Math.Max(stride, w * 2) * h,
            TextureFormat.A8 or TextureFormat.L8 => Math.Max(stride, w) * h,
            _ => 0,
        };
    }

    /// <summary>Größe eines Resource-Segments aus den Page-Flags (RSC7).</summary>
    internal static int PageFlagsSize(uint f)
    {
        uint s0 = ((f >> 27) & 0x1) << 0;
        uint s1 = ((f >> 26) & 0x1) << 1;
        uint s2 = ((f >> 25) & 0x1) << 2;
        uint s3 = ((f >> 24) & 0x1) << 3;
        uint s4 = ((f >> 17) & 0x7F) << 4;
        uint s5 = ((f >> 11) & 0x3F) << 5;
        uint s6 = ((f >> 7) & 0xF) << 6;
        uint s7 = ((f >> 5) & 0x3) << 7;
        uint s8 = ((f >> 4) & 0x1) << 8;
        long baseSize = 0x200L << (int)(f & 0xF);
        return (int)(baseSize * (s0 + s1 + s2 + s3 + s4 + s5 + s6 + s7 + s8));
    }

    private static byte[] Inflate(byte[] file, int offset)
    {
        using var input = new MemoryStream(file, offset, file.Length - offset, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private sealed class View(byte[] data, int systemSize)
    {
        public int Length => data.Length;
        public int SystemSize => systemSize;

        /// <summary>Virtuelle Adresse (0x5… System, 0x6… Grafik) in einen Offset umrechnen, -1 wenn ungültig.</summary>
        public long Resolve(ulong ptr)
        {
            if ((ptr >> 32) != 0) return -1;
            uint a = (uint)ptr;
            long off = a & 0x0FFFFFFF;
            long r = (a & 0xF0000000) switch
            {
                0x50000000 => off,
                0x60000000 => systemSize + off,
                _ => -1,
            };
            return r >= 0 && r < data.Length ? r : -1;
        }

        public byte U8(long o) => data[o];
        public ushort U16(long o) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)o, 2));
        public uint U32(long o) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)o, 4));
        public ulong U64(long o) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)o, 8));
        public byte[] Slice(long o, int len) => data.AsSpan((int)o, len).ToArray();

        public string? CString(long o)
        {
            if (o < 0) return null;
            int end = Array.IndexOf(data, (byte)0, (int)o, Math.Min(256, data.Length - (int)o));
            if (end <= o) return null;
            var s = Encoding.ASCII.GetString(data, (int)o, end - (int)o);
            return s.All(c => c >= 0x20 && c < 0x7F) ? s : null;
        }
    }
}
