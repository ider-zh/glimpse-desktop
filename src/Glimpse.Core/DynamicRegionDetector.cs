using Glimpse.Contracts;

namespace Glimpse.Core;

public record DynamicRegionAnalysis(List<PixelRect> Regions, bool BroadMotion, string Notice);

/// <summary>Bounded, local motion candidates; never claims to understand video content.</summary>
public sealed class DynamicRegionDetector
{
    private const int Columns = 128, Rows = 80, TileColumns = 32, TileRows = 20;
    private byte[]? previous;
    private readonly int[] changes = new int[TileColumns * TileRows];
    private readonly int[] firstChange = Enumerable.Repeat(int.MaxValue, TileColumns * TileRows).ToArray();
    private readonly int[] lastChange = new int[TileColumns * TileRows];
    private int intervals, broadIntervals;
    public void Observe(byte[] bgra, int width, int height)
    {
        if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("Invalid BGRA frame.");
        var sample = new byte[Columns * Rows];
        var changedPixels = new int[TileColumns * TileRows];
        int different = 0;
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Columns; x++)
            {
                int source = ((y * height / Rows) * width + x * width / Columns) * 4;
                int target = y * Columns + x;
                sample[target] = (byte)((bgra[source] * 29 + bgra[source + 1] * 150 + bgra[source + 2] * 77) >> 8);
                if (previous is not null && Math.Abs(sample[target] - previous[target]) > 18)
                {
                    changedPixels[y / 4 * TileColumns + x / 4]++; different++;
                }
            }
        if (previous is not null)
        {
            intervals++;
            int changedTiles = 0;
            for (int i = 0; i < changes.Length; i++)
                if (changedPixels[i] >= 2)
                {
                    changes[i]++; changedTiles++; firstChange[i] = Math.Min(firstChange[i], intervals); lastChange[i] = intervals;
                }
            if (different > sample.Length * .70 || changedTiles > changes.Length * .70) broadIntervals++;
        }
        previous = sample;
    }

    public DynamicRegionAnalysis Analyze(int width, int height)
    {
        DynamicRegionAnalysis Refuse() => new([], true, "画面存在大范围运动，可能正在滚动。请停下页面后重试，或手动框选视频。");
        if (intervals < 6) return new([], false, "采样不足，请重试或手动框选视频。");
        if (broadIntervals >= 2) return Refuse();
        var active = Enumerable.Range(0, changes.Length).Where(i => changes[i] >= Math.Max(4, (int)Math.Ceiling(intervals / 3.0)) &&
            firstChange[i] <= Math.Ceiling(intervals / 3.0) && lastChange[i] >= intervals * .66).ToHashSet();
        if (active.Count == 0) return new([], false, "未发现持续变化的区域；如视频仍阻塞识别，请手动框选。");
        int allMinX = active.Min(i => i % TileColumns), allMaxX = active.Max(i => i % TileColumns);
        int allMinY = active.Min(i => i / TileColumns), allMaxY = active.Max(i => i / TileColumns);
        if (allMaxX - allMinX + 1 > TileColumns * .85 && allMaxY - allMinY + 1 > TileRows * .80) return Refuse();
        var regions = new List<PixelRect>();
        while (active.Count > 0)
        {
            int seed = active.First(); active.Remove(seed);
            var component = new List<int>(); var pending = new Queue<int>(); pending.Enqueue(seed);
            while (pending.TryDequeue(out int tile))
            {
                component.Add(tile);
                int x = tile % TileColumns, y = tile / TileColumns;
                foreach (var neighbor in new[] { (X: x - 1, Y: y), (X: x + 1, Y: y), (X: x, Y: y - 1), (X: x, Y: y + 1) })
                    if (neighbor.X >= 0 && neighbor.X < TileColumns && neighbor.Y >= 0 && neighbor.Y < TileRows &&
                        active.Remove(neighbor.Y * TileColumns + neighbor.X)) pending.Enqueue(neighbor.Y * TileColumns + neighbor.X);
            }
            if (component.Count < 3) continue;
            int left = Math.Max(0, component.Min(i => i % TileColumns) * width / TileColumns - 4);
            int top = Math.Max(0, component.Min(i => i / TileColumns) * height / TileRows - 4);
            int right = Math.Min(width, (int)Math.Ceiling((component.Max(i => i % TileColumns) + 1) * width / (double)TileColumns) + 4);
            int bottom = Math.Min(height, (int)Math.Ceiling((component.Max(i => i / TileColumns) + 1) * height / (double)TileRows) + 4);
            if ((long)(right - left) * (bottom - top) < (long)width * height / 100) continue;
            regions.Add(new(left, top, right - left, bottom - top));
        }
        if (regions.Count > 8 || regions.Sum(r => (long)r.Width * r.Height) > (long)width * height * .65) return Refuse();
        return regions.Count == 0 ? new([], false, "仅发现零散变化，未自动忽略；请手动框选视频。") :
            new(regions, false, $"已自动忽略 {regions.Count} 个持续变化区域。请检查观察框中的虚线范围，可清除后手动重选。");
    }
}
