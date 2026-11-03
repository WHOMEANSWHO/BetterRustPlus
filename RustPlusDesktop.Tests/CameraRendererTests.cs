using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RustPlusApi.Camera;
using RustPlusApi.Data.Cameras;
using SkiaSharp;

namespace RustPlusDesktop.Tests;

[TestClass]
public sealed class CameraRendererTests
{
    [TestMethod]
    [DataRow("cam01")]
    [DataRow("cam02")]
    [DataRow("cctv01")]
    [DataRow("turret01")]
    [DataRow("drone01")]
    public void RealCameraFramesMatchUpstreamApprovedPixels(string fixtureId)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Camera");
        var fixture = JsonSerializer.Deserialize<CaptureFixture>(File.ReadAllText(Path.Combine(directory, fixtureId + "-frames.json")))!;
        var renderer = new CameraRenderer(fixture.Width, fixture.Height);
        foreach (var frame in fixture.Frames)
            renderer.AddRays(new CameraFrame { SampleOffset = frame.SampleOffset, RayData = Convert.FromBase64String(frame.RayDataBase64) });
        using var actual = SKBitmap.Decode(renderer.Render());
        using var expected = SKBitmap.Decode(Path.Combine(directory, fixtureId + "-golden.png"));
        Assert.AreEqual(expected.Width, actual.Width);
        Assert.AreEqual(expected.Height, actual.Height);
        CollectionAssert.AreEqual(expected.Pixels, actual.Pixels, $"Camera fixture {fixtureId} must preserve every approved pixel");
    }

    [TestMethod]
    public void UnobservedCameraPixelsRemainTransparent()
    {
        using var image = SKBitmap.Decode(new CameraRenderer(4, 4).Render());
        Assert.IsTrue(image.Pixels.All(pixel => pixel.Alpha == 0));
    }

    [TestMethod]
    public void MalformedNetworkFramesDoNotOverrunDecoder()
    {
        var renderer = new CameraRenderer(4, 4);
        renderer.AddRays(new CameraFrame { RayData = [255, 1], SampleOffset = 0 });
        renderer.AddRays(new CameraFrame { RayData = [255, 128, 63, 2], SampleOffset = -1 });
        renderer.AddRays(new CameraFrame { RayData = [255, 128, 63, 2], SampleOffset = int.MaxValue });
        using var image = SKBitmap.Decode(renderer.Render());
        Assert.AreEqual(4, image.Width);
    }

    [TestMethod]
    public void UnreasonableServerDimensionsAreRejectedBeforeAllocation()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CameraRenderer(0, 10));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new CameraRenderer(4096, 4096));
    }

    private sealed record CapturedFrame(int SampleOffset, string RayDataBase64);
    private sealed record CaptureFixture(string CameraId, int Width, int Height, List<CapturedFrame> Frames);
}
