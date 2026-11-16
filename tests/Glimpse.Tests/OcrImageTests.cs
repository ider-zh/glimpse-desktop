using Glimpse.Core;
using Xunit;

namespace Glimpse.Tests;

public class OcrImageTests
{
    [Fact]
    public void AutomaticEnhancementTargetsSmallGlyphsAndContrastedEmptyResults()
    {
        byte[] blank = Enumerable.Repeat((byte)255, 400).ToArray();
        Assert.True(OcrImage.ShouldEnhance([8, 10, 12], blank));
        Assert.False(OcrImage.ShouldEnhance([18, 20, 24], blank));
        Assert.False(OcrImage.ShouldEnhance([], blank));
        blank[0] = blank[1] = blank[2] = 0;
        Assert.True(OcrImage.ShouldEnhance([], blank));
    }
    [Fact]
    public void NativeScaleReusesImageWithoutAllocation()
    {
        byte[] bytes = new byte[16];
        var image = OcrImage.Prepare(bytes, 2, 2, 1, 100);
        Assert.Same(bytes, image.Pixels); Assert.Equal(1, image.ScaleX); Assert.Equal(1, image.ScaleY);
    }
    [Fact]
    public void BilinearResizePreservesCornersAndAlpha()
    {
        byte[] pixels = [0, 0, 0, 255, 100, 100, 100, 255, 200, 200, 200, 255, 255, 255, 255, 255];
        var image = OcrImage.Prepare(pixels, 2, 2, 2, 100);
        Assert.Equal(4, image.Width); Assert.Equal(4, image.Height);
        Assert.Equal(0, image.Pixels[0]); Assert.Equal(100, image.Pixels[12]);
        Assert.Equal(200, image.Pixels[48]); Assert.Equal(255, image.Pixels[60]);
        Assert.Equal(72, image.Pixels[20]); // .75²*0 + .25*.75*100 + .75*.25*200 + .25²*255
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(255, image.Pixels[i * 4 + 3]));
    }
    [Fact]
    public void ResizeIsCappedAndProvidesActualBoxScale()
    {
        var image = OcrImage.Prepare(new byte[20 * 10 * 4], 20, 10, 3, 35);
        Assert.Equal(35, image.Width); Assert.Equal(18, image.Height);
        Assert.Equal(1.75, image.ScaleX); Assert.Equal(1.8, image.ScaleY);
    }
    [Fact]
    public void InvalidImageAndCancellationAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrImage.Prepare([], 2, 2, 2, 100));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => OcrImage.Prepare(new byte[16], 2, 2, 2, 100, canceled.Token));
    }
    [Fact]
    public void EnhancementRespectsOutputMemoryBudget()
    {
        var image = OcrImage.Prepare(new byte[2500 * 2000 * 4], 2500, 2000, 2, 8192);
        Assert.True(image.ScaleX > 1);
        Assert.True(image.Pixels.Length <= 32 * 1024 * 1024);
    }
}
