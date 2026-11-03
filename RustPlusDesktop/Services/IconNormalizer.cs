using System;
using System.IO;
using SkiaSharp;

namespace RustPlusDesk.Services;

/// <summary>
/// Keeps the on-disk icon cache small and uniform: every cached icon is re-encoded to an exact
/// 40x40 PNG (the only size the app renders). This both saves space — the source art is often
/// 256/512px — and lets the sidebar hover decoration (which requires 40x40 files) find icons to use.
///
/// Uses the same MIT-licensed decoder as animated emoji playback.
/// </summary>
public static class IconNormalizer
{
    public const int Size = 40;

    /// <summary>
    /// Re-encodes icon bytes to an exact 40x40, aspect-preserving, transparently padded PNG.
    /// Returns the original bytes unchanged if decoding/encoding fails.
    /// </summary>
    public static byte[] To40(byte[] input)
    {
        try
        {
            using var data = SKData.CreateCopy(input);
            using var codec = SKCodec.Create(data);
            if (codec is null || !ImageDecodeLimits.IsSafeCanvas(codec.Info.Width, codec.Info.Height)) return input;
            using var image = SKBitmap.Decode(codec);
            if (image is null) return input;
            using var output = new SKBitmap(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(output))
            {
                canvas.Clear(SKColors.Transparent);
                float scale = Math.Min((float)Size / image.Width, (float)Size / image.Height);
                float width = image.Width * scale, height = image.Height * scale;
                var destination = SKRect.Create((Size - width) / 2, (Size - height) / 2, width, height);
                canvas.DrawBitmap(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }
            using var encoded = output.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray() ?? input;
        }
        catch
        {
            return input;
        }
    }

    /// <summary>
    /// Deletes cached PNGs that are not 40x40 (old 256/512px downloads). Uses a header-only size
    /// read so it does not decode the images. Returns the number of files removed. Idempotent.
    /// </summary>
    public static int CleanupNon40(string directory)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*.png"))
            {
                try
                {
                    if (!IsExactly40(path))
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
                catch { /* skip locked/partial files */ }
            }
        }
        catch { /* best effort */ }
        return removed;
    }

    private static bool IsExactly40(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < header.Length) return false;
        if (header[1] != 'P' || header[2] != 'N' || header[3] != 'G') return false;

        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        return width == Size && height == Size;
    }
}
