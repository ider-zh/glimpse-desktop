using System.Text.Json;
using Glimpse.Contracts;
using Glimpse.Core;
using Glimpse.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glimpse.Tests;

public class ReaderTests
{
    private static SessionData Empty() => new(Guid.NewGuid(), "Fixture", "paused", 1, 1, new(-300, 50, 800, 500), 0, [], [], []);
    private static Observation Frame(params string[] lines) => new(Guid.NewGuid().ToString(), "Fake OCR", 10, 2,
        lines.Select((line, i) => new OcrBlock(line, new(0, i * 30, 500, 24))).ToList(), false, "fixture-app");
    private static readonly string[] Article = Enumerable.Range(0, 64)
        .Select(i => $"Paragraph {i:D3}: durable context from a synthetic scrolling article.").ToArray();
    private readonly SessionReconciler aligner = new();

    [Fact]
    public void CommonHeaderDoesNotJustifyMergingRepliesWithDifferentNeighbors()
    {
        var s = aligner.Apply(Empty(), Frame("Shared screen header with enough anchor text", "Author alpha", "支持", "Ending alpha")).Session;
        s = aligner.Apply(s, Frame("Shared screen header with enough anchor text", "Author beta", "支持", "Ending beta")).Session;
        Assert.Equal(2, s.Blocks.Count(b => b.Text == "支持"));
    }

    [Fact]
    public void FirstViewportHasKnownWithinFrameOrder()
    {
        var session = aligner.Apply(Empty(), Frame(Article[..6])).Session;
        Assert.All(session.Blocks, block => Assert.Equal("confirmed", block.Status));
    }

    [Fact]
    public void StabilityResetsAndOnlyCommitsCurrentGeneration()
    {
        var gate = new StabilityGate();
        Assert.False(gate.Observe(true, 0));
        Assert.False(gate.Observe(false, 999));
        Assert.False(gate.Observe(true, 999));
        Assert.False(gate.Observe(false, 1998));
        Assert.True(gate.Observe(false, 1999));
        long obsolete = gate.Generation;
        Assert.Equal("Processing", gate.State);
        gate.Observe(true, 2000);
        gate.Commit(obsolete);
        Assert.Equal("Changing", gate.State);
        Assert.True(gate.Observe(false, 3000));
        gate.Commit(gate.Generation);
        Assert.Equal("Recorded", gate.State);
    }
    [Fact]
    public void PauseRejectsPendingAndStaleResults()
    {
        var gate = new StabilityGate();
        gate.Observe(true, 0); gate.Observe(false, 1000);
        long generation = gate.Generation;
        gate.Pause(); gate.Commit(generation);
        Assert.False(gate.Observe(true, 10000)); Assert.Equal("Paused", gate.State);
        gate.Resume(); Assert.False(gate.Observe(false, 20000));
    }
    [Fact]
    public void IdenticalFramesDoNotConsumeSlots()
    {
        var session = Empty();
        for (int i = 0; i < 10; i++) session = aligner.Apply(session, Frame(Article[..6])).Session;
        Assert.Single(session.Snapshots); Assert.Equal(1, session.Revision); Assert.Equal(6, session.Blocks.Count);
    }
    [Fact]
    public void DownwardTwentyFramesHaveExactOrderAndNineSnapshots()
    {
        var session = Empty();
        for (int i = 0; i < 20; i++) session = aligner.Apply(session, Frame(Article[(i * 3)..(i * 3 + 6)])).Session;
        Assert.Equal(Article[..63], session.Blocks.Select(b => b.Text));
        Assert.Equal(9, session.Snapshots.Count); Assert.Equal(20, session.Revision);
        Assert.Equal(1, session.Blocks[0].Source.FirstSequence);
        Assert.Equal(Article[0], session.Blocks[0].Source.OriginalText);
    }
    [Fact]
    public void UpwardScrollInsertsBeforeExistingAnchors()
    {
        var s = aligner.Apply(Empty(), Frame(Article[6..12])).Session;
        s = aligner.Apply(s, Frame(Article[3..9])).Session;
        s = aligner.Apply(s, Frame(Article[..6])).Session;
        Assert.Equal(Article[..12], s.Blocks.Select(b => b.Text));
    }
    [Fact]
    public void ReturnToEvictedViewportUsesDurableSequence()
    {
        var s = Empty();
        for (int i = 0; i < 20; i++) s = aligner.Apply(s, Frame(Article[(i * 3)..(i * 3 + 6)])).Session;
        s = aligner.Apply(s, Frame(Article[..6])).Session;
        Assert.Equal(63, s.Blocks.Count);
        Assert.Equal(Article[..63], s.Blocks.Select(b => b.Text));
    }
    [Fact]
    public void IdenticalShortRepliesInDifferentContextsAreNotMerged()
    {
        var s = aligner.Apply(Empty(), Frame("Distinct author A speaking about the first topic", "支持", "First ending context")).Session;
        s = aligner.Apply(s, Frame("Distinct author B speaking about the second topic", "支持", "Second ending context")).Session;
        Assert.Equal(2, s.Blocks.Count(b => b.Text == "支持"));
    }
    [Fact]
    public void RepeatedUnanchoredShortRepliesAreConservativelyPreserved()
    {
        var s = aligner.Apply(Empty(), Frame("支持", "支持", "甲")).Session;
        s = aligner.Apply(s, Frame("支持", "支持", "乙")).Session;
        Assert.Equal(4, s.Blocks.Count(b => b.Text == "支持"));
    }
    [Fact]
    public void AnchoredRepeatedRepliesRemainSeparateWithinSnapshot()
    {
        var s = aligner.Apply(Empty(), Frame(Article[0], "支持", "支持", Article[1])).Session;
        s = aligner.Apply(s, Frame(Article[0], "支持", "支持", Article[1], Article[2])).Session;
        Assert.Equal(2, s.Blocks.Count(b => b.Text == "支持"));
    }
    [Fact]
    public void OcrTypoPreservesBothReadingsAndMarksUncertainty()
    {
        var s = aligner.Apply(Empty(), Frame(Article[0], "A sufficiently lengthy sentence about reliable evidence.", Article[1])).Session;
        s = aligner.Apply(s, Frame(Article[0], "A sufficiently lengthy sentence about reliabie evidence.", Article[1], Article[2])).Session;
        var line = s.Blocks[1];
        Assert.Equal(4, s.Blocks.Count); Assert.Equal("uncertain", line.Status);
        Assert.Contains("reliable", line.Source.OriginalText);
        Assert.Contains("reliabie", line.Source.LastObservedText);
        Assert.Single(line.Source.Variants!);
    }
    [Fact]
    public void ChangingNumbersAreNeverMergedAsOcrTypos()
    {
        var s = aligner.Apply(Empty(), Frame(Article[0], "The account total value today is 1000 units.", Article[1])).Session;
        s = aligner.Apply(s, Frame(Article[0], "The account total value today is 2000 units.", Article[1])).Session;
        Assert.Equal(4, s.Blocks.Count);
    }
    [Fact]
    public void BlankFrameDoesNotEraseDocumentOrConsumeSnapshot()
    {
        var s = aligner.Apply(Empty(), Frame(Article[..6])).Session;
        var result = aligner.Apply(s, Frame());
        Assert.False(result.Effective); Assert.Same(s, result.Session);
    }
    [Fact]
    public void PageSwapAloneDoesNotAssertGap()
    {
        var s = aligner.Apply(Empty(), Frame(Article[..6])).Session;
        s = aligner.Apply(s, Frame(Article[20..26]) with { MovementObserved = true, ContextId = "different-app" }).Session;
        Assert.Empty(s.Gaps);
        Assert.Contains(s.Blocks, b => b.Status == "uncertain");
    }
    [Fact]
    public void LongJumpAfterObservedOverlapMarksPossibleGap()
    {
        var s = aligner.Apply(Empty(), Frame(Article[..6])).Session;
        s = aligner.Apply(s, Frame(Article[3..9])).Session;
        s = aligner.Apply(s, Frame(Article[30..36]) with { MovementObserved = true }).Session;
        Assert.Single(s.Gaps);
        Assert.Contains("Possible", s.Gaps[0].Reason);
    }
    [Fact]
    public void ForegroundContextSwapDoesNotAssertGap()
    {
        var s = aligner.Apply(Empty(), Frame(Article[..6])).Session;
        s = aligner.Apply(s, Frame(Article[3..9])).Session;
        s = aligner.Apply(s, Frame(Article[30..36]) with { MovementObserved = true, ContextId = "other-window" }).Session;
        Assert.Empty(s.Gaps);
    }
    [Fact]
    public void NormalizationPreservesNegationDigitsAndSemantics()
    {
        Assert.Equal("不支持 123!", SessionReconciler.Normalize("不支持\r\n １２３！"));
        Assert.NotEqual(SessionReconciler.Normalize("不支持"), SessionReconciler.Normalize("支持"));
    }
    [Fact]
    public void DetectorIgnoresTinyNoiseButDetectsMaterialChange()
    {
        var detector = new FrameChangeDetector();
        var frame = new byte[256 * 160 * 4];
        Assert.True(detector.HasChanged(frame, 256, 160));
        Assert.False(detector.HasChanged(frame, 256, 160));
        frame[0] = 255;
        Assert.False(detector.HasChanged(frame, 256, 160));
        Array.Fill(frame, (byte)200);
        Assert.True(detector.HasChanged(frame, 256, 160));
    }
    [Fact]
    public void DetectorSeesSmallTextChangesInLargeMostlyWhiteFrame()
    {
        var detector = new FrameChangeDetector();
        var frame = Enumerable.Repeat((byte)255, 1280 * 800 * 4).ToArray();
        detector.HasChanged(frame, 1280, 800);
        for (int y = 200; y < 225; y++)
            for (int x = 300; x < 325; x++)
                for (int c = 0; c < 3; c++) frame[(y * 1280 + x) * 4 + c] = 0;
        Assert.True(detector.HasChanged(frame, 1280, 800));
    }
    [Fact]
    public async Task PersistencePruningRestartBackupAndCascadeDelete()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "sessions.db");
            var repository = new SessionRepository(path);
            var session = Empty();
            for (int i = 0; i < 20; i++)
            {
                session = aligner.Apply(session, Frame(Article[(i * 3)..(i * 3 + 6)])).Session;
                await repository.SaveAsync(session);
            }
            var reopened = await new SessionRepository(path).LoadAsync(session.Id);
            Assert.Equal(JsonSerializer.Serialize(session), JsonSerializer.Serialize(reopened));
            string backup = Path.Combine(directory, "backup.db");
            await repository.BackupAsync(backup);
            Assert.Equal(63, (await new SessionRepository(backup).LoadAsync(session.Id))!.Blocks.Count);
            using (var db = new SqliteConnection($"Data Source={path}"))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM ocr_snapshots"; Assert.Equal(9L, cmd.ExecuteScalar());
                cmd.CommandText = "PRAGMA foreign_key_check"; Assert.Null(cmd.ExecuteScalar());
            }
            await repository.DeleteAsync(session.Id);
            Assert.Empty(await repository.ListAsync()); Assert.Null(await repository.LoadAsync(session.Id));
            using (var db = new SqliteConnection($"Data Source={path}"))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM content_blocks"; Assert.Equal(0L, cmd.ExecuteScalar());
                cmd.CommandText = "SELECT COUNT(*) FROM snapshot_blocks"; Assert.Equal(0L, cmd.ExecuteScalar());
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task CanceledTransactionCannotReplaceCommittedDocument()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-tests-" + Guid.NewGuid());
        try
        {
            var repository = new SessionRepository(Path.Combine(directory, "sessions.db"));
            var s = aligner.Apply(Empty(), Frame(Article[..6])).Session;
            await repository.SaveAsync(s);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SaveAsync(s with { Revision = 99 }, canceled.Token));
            Assert.Equal(1, (await repository.LoadAsync(s.Id))!.Revision);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    [Fact]
    public void ProtocolRoundTripKeepsNegativePhysicalCoordinates()
    {
        var e = Envelope.Create("rect", new RectRequest(new(-1920, -200, 800, 600)), Guid.NewGuid(), 25);
        var copy = JsonSerializer.Deserialize<Envelope>(JsonSerializer.Serialize(e, Envelope.Json), Envelope.Json)!;
        Assert.Equal(-1920, copy.Read<RectRequest>().Rect.X);
        Assert.Equal(25, copy.CaptureGeneration); Assert.Equal(e.RequestId, copy.RequestId);
    }
}
