using Glimpse.Contracts;
using Glimpse.Core;
using Glimpse.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glimpse.Tests;

public class FrameMaskTests
{
    [Fact]
    public void AnimatedIgnoredPixelsDoNotResetStabilityButOutsideChangesDo()
    {
        const int width = 320, height = 200;
        PixelRect mask = new(0, 0, 160, height);
        var detector = new FrameChangeDetector(); var gate = new StabilityGate();
        for (int time = 0; time <= 1200; time += 200)
        {
            var pixels = Enumerable.Repeat((byte)220, width * height * 4).ToArray();
            for (int y = 0; y < height; y++)
                pixels.AsSpan(y * width * 4, 160 * 4).Fill((byte)(time % 400 == 0 ? 10 : 150));
            FrameMasker.Apply(pixels, width, height, [mask]);
            bool ready = gate.Observe(detector.HasChanged(pixels, width, height), time);
            if (time == 1000) { Assert.True(ready); gate.Commit(gate.Generation); }
        }
        Assert.Equal("Recorded", gate.State);
        var outsideChanged = new byte[width * height * 4];
        FrameMasker.Apply(outsideChanged, width, height, [mask]);
        Assert.True(detector.HasChanged(outsideChanged, width, height));
    }

    [Fact]
    public void MaskPreservesDimensionsAndPixelsOutsideExactRectangle()
    {
        var pixels = Enumerable.Repeat((byte)42, 8 * 6 * 4).ToArray();
        FrameMasker.Apply(pixels, 8, 6, [new(2, 1, 3, 2)]);
        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 8; x++)
                Assert.All(pixels.Skip((y * 8 + x) * 4).Take(4), b => Assert.Equal((byte)(x is >= 2 and < 5 && y is >= 1 and < 3 ? 255 : 42), b));
        Assert.True(FrameMasker.Intersects(new(1, 0, 2, 2), new(2, 1, 3, 2)));
        Assert.False(FrameMasker.Intersects(new(5, 0, 2, 2), new(2, 1, 3, 2)));
    }

    [Fact]
    public void InvalidAndOverflowingMasksAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => FrameMasker.Validate([new(-1, 0, 10, 10)], 100, 100));
        Assert.Throws<InvalidOperationException>(() => FrameMasker.Validate([new(int.MaxValue, 0, 10, 10)], 100, 100));
        Assert.Throws<InvalidOperationException>(() => FrameMasker.Validate(Enumerable.Repeat(new PixelRect(0, 0, 1, 1), 9), 100, 100));
    }

    [Fact]
    public void BroadOutsideMovementInvalidatesMaskButAnimationAndSmallControlsDoNot()
    {
        const int width = 320, height = 200;
        PixelRect mask = new(0, 0, 160, height);
        var guard = new MaskMotionGuard();
        var pixels = new byte[width * height * 4];
        Assert.False(guard.HasGlobalMovement(pixels, width, height, [mask]));
        for (int y = 0; y < height; y++) pixels.AsSpan(y * width * 4, 160 * 4).Fill(255);
        Assert.False(guard.HasGlobalMovement(pixels, width, height, [mask]));
        pixels.AsSpan((10 * width + 170) * 4, 20 * 4).Fill(255);
        Assert.False(guard.HasGlobalMovement(pixels, width, height, [mask]));
        pixels.AsSpan().Fill(255);
        Assert.True(guard.HasGlobalMovement(pixels, width, height, [mask]));
    }

    [Fact]
    public async Task MaskSettingsSurviveStorageAndOldSchemaUpgrades()
    {
        string folder = Path.Combine(Path.GetTempPath(), "glimpse-mask-" + Guid.NewGuid());
        string path = Path.Combine(folder, "sessions.db");
        try
        {
            var repo = new SessionRepository(path);
            var session = new SessionData(Guid.NewGuid(), "mask", "paused", 1, 1, new(0, 0, 320, 200), 0, [], [], [], [new(1, 2, 80, 60)]);
            await repo.SaveAsync(session);
            Assert.Equal(session.IgnoreRegions, (await repo.LoadAsync(session.Id))!.IgnoreRegions);
            using (var db = new SqliteConnection($"Data Source={path}"))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "ALTER TABLE sessions DROP COLUMN ignore_regions_json; PRAGMA user_version=2";
                command.ExecuteNonQuery();
            }
            var upgraded = new SessionRepository(path);
            Assert.Empty((await upgraded.LoadAsync(session.Id))!.IgnoreRegions!);
            Assert.Equal("mask", (await upgraded.LoadAsync(session.Id))!.Title);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void SparseInkScrollingOnWhiteInvalidatesFixedScreenMask()
    {
        const int width = 640, height = 400;
        PixelRect mask = new(0, 0, 240, height);
        byte[] Page(int offset)
        {
            var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
            for (int y = 0; y < height; y++)
                for (int x = 260; x < width - 20; x++)
                    if ((y + offset) % 20 < 10 && x % 20 < 2)
                        pixels.AsSpan((y * width + x) * 4, 3).Fill(0);
            return pixels;
        }
        var first = Page(0); var scrolled = Page(10);
        int changed = Enumerable.Range(0, width * height).Count(i => first[i * 4] != scrolled[i * 4]);
        Assert.True(changed < width * height * .15);
        var guard = new MaskMotionGuard();
        Assert.False(guard.HasGlobalMovement(first, width, height, [mask]));
        Assert.True(guard.HasGlobalMovement(scrolled, width, height, [mask]));
    }
}
