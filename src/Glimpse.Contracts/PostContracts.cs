namespace Glimpse.Contracts;

// Stored inside provenance_json so evidence survives detailed snapshot eviction.
// Bounds are relative to the captured viewport, in physical pixels.
public record BlockGeometry(int Sequence, TextBox Bounds, string? ContextId,
    Guid? PostId = null, string? Role = null);
public record PostEvidence(Guid BlockId, TextBox Bounds, int Sequence, string Role);
public record ObservedPost(Guid Id, string Label, List<Guid> BlockIds,
    List<PostEvidence> Evidence, bool IsVisible, bool Uncertain);
public record PostCatalog(Guid SessionId, int DocumentRevision, List<ObservedPost> Posts,
    List<Guid> UnassignedBlockIds, List<Guid> ExcludedBlockIds, List<string> Warnings);
