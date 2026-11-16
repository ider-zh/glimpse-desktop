using System.Text.Json;

namespace Glimpse.Contracts;

public record PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width >= 32 && Height >= 32 && Width <= 7680 && Height <= 4320;
}
public record TextBox(double X, double Y, double Width, double Height);
public record OcrBlock(string Text, TextBox Bounds, double? Confidence = null);
public record Observation(string FrameHash, string Engine, long CapturedAtMs, int ProcessingMs,
    List<OcrBlock> Blocks, bool MovementObserved = false, string? ContextId = null);
public record Provenance(long FirstSeenAtMs, int FirstSequence, string OriginalText, int Observations,
    string LastObservedText, List<string>? Variants = null);
public record ContentBlock(Guid Id, string Text, string Normalized, string Status, Provenance Source);
public record Snapshot(Guid Id, int Sequence, Observation Observation, List<Guid> BlockIds,
    int NewBlocks, int ReusedBlocks, int UncertainBlocks);
public record CaptureGap(Guid Id, int FromSequence, int ToSequence, string Reason, bool Resolved = false);
public record SessionData(Guid Id, string Title, string Status, long CreatedAtMs, long UpdatedAtMs,
    PixelRect Rect, int Revision, List<ContentBlock> Blocks, List<Snapshot> Snapshots, List<CaptureGap> Gaps);
public record SessionSummary(Guid Id, string Title, string Status, long UpdatedAtMs, int Revision);
public record ReaderUpdate(string State, long Generation, SessionData? Session = null, string? Error = null,
    int OcrMs = 0, int CommitMs = 0, string? OcrLanguage = null);
public record Envelope(int ProtocolVersion, Guid RequestId, Guid? SessionId, long CaptureGeneration,
    long Timestamp, string MessageType, JsonElement Payload)
{
    public const int Version = 1;
    public static Envelope Create<T>(string type, T payload, Guid? session = null, long generation = 0,
        Guid? request = null) => new(Version, request ?? Guid.NewGuid(), session, generation,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), type, JsonSerializer.SerializeToElement(payload, Json));
    public T Read<T>() => Payload.Deserialize<T>(Json) ?? throw new InvalidDataException("Missing payload.");
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
public record StartRequest(string Title, PixelRect Rect);
public record SessionRequest(Guid Id);
public record RectRequest(PixelRect Rect);
