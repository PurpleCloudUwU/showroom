using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace RetroShowroom.Core;

/// <summary>Wandelt die Rohdaten einer YTD-Textur in RGBA-Pixel (8 Bit je Kanal) um.</summary>
public static class TextureDecoder
{
    public static byte[] ToRgba(YtdTexture t)
    {
        if (t.Mip0.Length == 0)
            throw Unsupported(t);

        int w = t.Width, h = t.Height;
        byte[] src = t.Mip0;
        switch (t.Format)
        {
            case TextureFormat.BC1: return Bc(t, CompressionFormat.Bc1WithAlpha);
            case TextureFormat.BC2: return Bc(t, CompressionFormat.Bc2);
            case TextureFormat.BC3: return Bc(t, CompressionFormat.Bc3);
            case TextureFormat.BC4: return Bc(t, CompressionFormat.Bc4);
            case TextureFormat.BC5: return Bc(t, CompressionFormat.Bc5);
            case TextureFormat.BC7: return Bc(t, CompressionFormat.Bc7);

            case TextureFormat.A8R8G8B8:
            {
                // Im Speicher B, G, R, A
                var o = new byte[w * h * 4];
                int row = Math.Max(t.Stride, w * 4);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = y * row + x * 4, d = (y * w + x) * 4;
                    o[d] = src[s + 2]; o[d + 1] = src[s + 1]; o[d + 2] = src[s]; o[d + 3] = src[s + 3];
                }
                return o;
            }
            case TextureFormat.A8B8G8R8:
            {
                var o = new byte[w * h * 4];
                int row = Math.Max(t.Stride, w * 4);
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(src, y * row, o, y * w * 4, w * 4);
                return o;
            }
            case TextureFormat.A1R5G5B5:
            {
                var o = new byte[w * h * 4];
                int row = Math.Max(t.Stride, w * 2);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = y * row + x * 2, d = (y * w + x) * 4;
                    int v = src[s] | (src[s + 1] << 8);
                    o[d] = (byte)(((v >> 10) & 31) * 255 / 31);
                    o[d + 1] = (byte)(((v >> 5) & 31) * 255 / 31);
                    o[d + 2] = (byte)((v & 31) * 255 / 31);
                    o[d + 3] = (byte)((v & 0x8000) != 0 ? 255 : 0);
                }
                return o;
            }
            case TextureFormat.L8:
            case TextureFormat.A8:
            {
                var o = new byte[w * h * 4];
                int row = Math.Max(t.Stride, w);
                bool alpha = t.Format == TextureFormat.A8;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte v = src[y * row + x];
                    int d = (y * w + x) * 4;
                    o[d] = o[d + 1] = o[d + 2] = alpha ? (byte)255 : v;
                    o[d + 3] = alpha ? v : (byte)255;
                }
                return o;
            }
        }
        throw Unsupported(t);
    }

    private static NotSupportedException Unsupported(YtdTexture t) =>
        new(Loc.T($"Das Texturformat {t.FormatName} wird nicht unterstützt.", $"The texture format {t.FormatName} is not supported."));

    private static byte[] Bc(YtdTexture t, CompressionFormat format)
    {
        var decoder = new BcDecoder();
        ColorRgba32[] pixels = decoder.DecodeRaw(t.Mip0, t.Width, t.Height, format);
        return MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
    }
}
