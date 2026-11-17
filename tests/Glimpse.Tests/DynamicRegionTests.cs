using Glimpse.Core;
using Xunit;

namespace Glimpse.Tests;

public class DynamicRegionTests
{
    private const int Width = 320, Height = 200;
    private static byte[] Frame(int i, bool global = false, bool small = false, bool transient = false)
    {
        var pixels = Enumerable.Repeat((byte)220, Width * Height * 4).ToArray();
        int right = global ? Width : small ? 8 : 128, bottom = global ? Height : small ? 8 : 140;
        byte shade = (byte)((transient && i > 2 || i % 2 == 0) ? 20 : 180);
        for (int y = 0; y < bottom; y++)
            pixels.AsSpan(y * Width * 4, right * 4).Fill(shade);
        return pixels;
    }
    [Fact]
    public void PersistentLocalMotionProducesBoundedMaskOutsideTextRemainsReadable()
    {
        var detector = new DynamicRegionDetector();
        for (int i = 0; i < 13; i++) detector.Observe(Frame(i), Width, Height);
        var result = detector.Analyze(Width, Height);
        Assert.False(result.BroadMotion);
        var region = Assert.Single(result.Regions);
        Assert.Equal(0, region.X); Assert.Equal(0, region.Y);
        Assert.InRange(region.Width, 128, 144); Assert.InRange(region.Height, 140, 154);
        Assert.False(FrameMasker.Intersects(new(200, 30, 100, 20), region));
    }
    [Fact]
    public void BroadMotionRefusesToMaskScrollingScreen()
    {
        var detector = new DynamicRegionDetector();
        for (int i = 0; i < 13; i++) detector.Observe(Frame(i, global: true), Width, Height);
        var result = detector.Analyze(Width, Height);
        Assert.True(result.BroadMotion); Assert.Empty(result.Regions);
    }
    [Fact]
    public void SmallBlinkingControlAndTransientChangesAreNotVideoCandidates()
    {
        foreach (bool small in new[] { false, true })
        {
            var detector = new DynamicRegionDetector();
            for (int i = 0; i < 13; i++) detector.Observe(Frame(i, small: small, transient: !small), Width, Height);
            Assert.Empty(detector.Analyze(Width, Height).Regions);
        }
    }
    [Fact]
    public void StaticPageAndInsufficientSamplesProduceNoCandidate()
    {
        var detector = new DynamicRegionDetector();
        detector.Observe(Frame(0), Width, Height);
        Assert.Empty(detector.Analyze(Width, Height).Regions);
        for (int i = 0; i < 12; i++) detector.Observe(Frame(0), Width, Height);
        Assert.Empty(detector.Analyze(Width, Height).Regions);
    }
    [Fact]
    public void LowDutyLoopWithFourChangesSpanningEarlyAndLateSamplesIsDetected()
    {
        var detector = new DynamicRegionDetector();
        int state = 0;
        for (int i = 0; i < 13; i++)
        {
            if (i is 1 or 4 or 8 or 12) state ^= 1;
            detector.Observe(Frame(state), Width, Height);
        }
        Assert.Single(detector.Analyze(Width, Height).Regions);
        // Same number of changes only at the start is a transient load, not sustained motion.
        detector = new DynamicRegionDetector(); state = 0;
        for (int i = 0; i < 13; i++)
        {
            if (i is >= 1 and <= 4) state ^= 1;
            detector.Observe(Frame(state), Width, Height);
        }
        Assert.Empty(detector.Analyze(Width, Height).Regions);
    }
}
