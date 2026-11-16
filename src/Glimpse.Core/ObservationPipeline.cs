using Glimpse.Contracts;

namespace Glimpse.Core;

public interface IOcrEngine
{
    string Name { get; }
    Task<List<OcrBlock>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellation);
}
public interface IScreenCapture
{
    byte[] Capture(PixelRect rect);
}
public interface IFrameChangeDetector
{
    bool HasChanged(byte[] bgra, int width, int height);
    void Reset();
}
public interface ISessionRepository
{
    Task<List<SessionSummary>> ListAsync(CancellationToken cancellation = default);
    Task<SessionData?> LoadAsync(Guid id, CancellationToken cancellation = default);
    Task SaveAsync(SessionData session, CancellationToken cancellation = default);
    Task DeleteAsync(Guid id, CancellationToken cancellation = default);
}

// Compare a fixed 256x160 luminance grid against the last significant frame. Small repeated
// changes accumulate against that reference, rather than continually moving the baseline.
public sealed class FrameChangeDetector : IFrameChangeDetector
{
    private byte[]? baseline;
    public void Reset() => baseline = null;
    public bool HasChanged(byte[] bgra, int width, int height)
    {
        const int columns = 256, rows = 160;
        var sample = new byte[columns * rows];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int i = ((y * height / rows) * width + x * width / columns) * 4;
                sample[y * columns + x] = (byte)((bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8);
            }
        bool changed = baseline is null;
        if (baseline is not null)
        {
            int different = 0, total = 0;
            for (int i = 0; i < sample.Length; i++)
            {
                int delta = Math.Abs(sample[i] - baseline[i]);
                if (delta > 18) different++;
                total += delta;
            }
            changed = different > 10 || total > sample.Length * .5;
        }
        if (changed) baseline = sample;
        return changed;
    }
}

// Time is supplied by the caller (monotonic milliseconds), making debounce deterministic.
public sealed class StabilityGate
{
    public const int DebounceMs = 1000;
    private long changedAt;
    private bool pending;
    public string State { get; private set; } = "Idle";
    public long Generation { get; private set; }
    public bool Observe(bool changed, long now)
    {
        if (State == "Paused") return false;
        if (changed)
        {
            Generation++;
            changedAt = now;
            pending = true;
            State = "Changing";
        }
        if (pending && now - changedAt >= DebounceMs)
        {
            pending = false;
            State = "Processing";
            return true;
        }
        if (pending && !changed) State = "Stabilizing";
        return false;
    }
    public void Commit(long generation)
    {
        if (Generation == generation && State == "Processing") State = "Recorded";
    }
    public void Pause() { Generation++; pending = false; State = "Paused"; }
    public void Resume() { Generation++; pending = false; State = "Idle"; }
}
