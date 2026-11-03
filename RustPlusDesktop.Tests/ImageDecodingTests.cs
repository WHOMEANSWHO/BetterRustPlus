using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustPlusDesk.Services;
using SkiaSharp;

namespace RustPlusDesktop.Tests;

[TestClass]
public sealed class ImageDecodingTests
{
    [TestMethod]
    public void IconResizePreservesAspectRatioAndTransparentPadding()
    {
        using var original = new SKBitmap(80, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
        original.Erase(SKColors.Red);
        using var encoded = original.Encode(SKEncodedImageFormat.Png, 100);
        using var resized = SKBitmap.Decode(IconNormalizer.To40(encoded.ToArray()));
        Assert.IsNotNull(resized);
        Assert.AreEqual(40, resized.Width);
        Assert.AreEqual(40, resized.Height);
        Assert.AreEqual((byte)0, resized.GetPixel(20, 0).Alpha);
        Assert.AreEqual(SKColors.Red, resized.GetPixel(20, 20));
        Assert.AreEqual((byte)0, resized.GetPixel(20, 39).Alpha);
    }

    [TestMethod]
    public void CorrectlySizedNonPngStillBecomesPng()
    {
        using var original = new SKBitmap(40, 40);
        original.Erase(SKColors.Blue);
        using var encoded = original.Encode(SKEncodedImageFormat.Jpeg, 90);
        byte[] result = IconNormalizer.To40(encoded.ToArray());
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, result[..8]);
    }

    [TestMethod]
    public void CorruptImageRetainsExistingFallbackWithoutThrowing()
    {
        byte[] corrupt = [1, 2, 3, 4];
        Assert.AreSame(corrupt, IconNormalizer.To40(corrupt));
    }

    [TestMethod]
    public void AllocationLimitsRejectOversizedImagesAndAnimationTotals()
    {
        Assert.IsTrue(ImageDecodeLimits.IsSafeCanvas(512, 512));
        Assert.IsFalse(ImageDecodeLimits.IsSafeCanvas(8192, 8192));
        Assert.IsFalse(ImageDecodeLimits.IsSafeCanvas(0, 20));
        Assert.IsFalse(ImageDecodeLimits.IsSafeAnimation(512, 512, 2048));
        Assert.IsFalse(ImageDecodeLimits.IsSafeAnimation(32, 32, 2049));
    }
}
