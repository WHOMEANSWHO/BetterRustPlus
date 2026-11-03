namespace RustPlusDesk.Services;

/// <summary>Reject unreasonable image allocations before decoding downloaded icons or bundled animations.</summary>
internal static class ImageDecodeLimits
{
    internal static bool IsSafeCanvas(int width, int height) =>
        width is > 0 and <= 8192 && height is > 0 and <= 8192 && (long)width * height <= 16_777_216;

    internal static bool IsSafeAnimation(int width, int height, int frames) =>
        IsSafeCanvas(width, height) && frames is > 0 and <= 2048 && (long)width * height * frames <= 67_108_864;
}
