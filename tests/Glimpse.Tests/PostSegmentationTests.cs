using Glimpse.Contracts;
using Glimpse.Core;
using Glimpse.Storage;
using Xunit;

namespace Glimpse.Tests;

public class PostSegmentationTests
{
    private readonly SessionReconciler reconciler = new();
    private readonly PostSegmenter observer = new();
    private static SessionData Empty() => new(Guid.NewGuid(), "Synthetic posts", "paused", 1, 1,
        new(0, 0, 800, 700), 0, [], [], []);
    private static Observation Frame(params (string Text, int Y)[] lines) => new(Guid.NewGuid().ToString(),
        "synthetic", 10, 1, lines.Select(l => new OcrBlock(l.Text, new(100, l.Y, 580, 22))).ToList(), false, "fixture");

    [Fact]
    public void TwoPostsHaveDisjointEvidenceAndControlStripsNeverReachScopedContext()
    {
        var session = reconciler.Apply(Empty(), Frame(("@alice", 10), ("The first post is about gardening and soil.", 40),
            ("赞 12 评论 3 分享", 75), ("@bob", 125), ("The second post is about space and rockets.", 155),
            ("赞 7 评论 1 分享", 190))).Session;
        var catalog = observer.Build(session);
        Assert.Equal(2, catalog.Posts.Count);
        Assert.Equal(2, catalog.ExcludedBlockIds.Count);
        Assert.Empty(catalog.Posts[0].BlockIds.Intersect(catalog.Posts[1].BlockIds));
        var scoped = observer.Scope(session, catalog.Posts[0].Id);
        Assert.Contains(scoped.Blocks, b => b.Text.Contains("gardening"));
        Assert.DoesNotContain(scoped.Blocks, b => b.Text.Contains("rockets") || b.Text.Contains("分享"));
        Assert.All(scoped.Snapshots.SelectMany(s => s.Observation.Blocks), b => Assert.DoesNotContain("rockets", b.Text));
        Assert.Equal(session.Revision, scoped.Revision);
    }

    [Fact]
    public void ReplyWordInsideOrdinaryContentIsNotGloballyDeleted()
    {
        var session = reconciler.Apply(Empty(), Frame(("My post is discussing how to write replies.", 10), ("回复", 42),
            ("The word above is part of this content.", 74))).Session;
        var catalog = observer.Build(session);
        Assert.Single(catalog.Posts);
        Assert.Empty(catalog.ExcludedBlockIds);
        Assert.Contains(observer.Scope(session, catalog.Posts[0].Id).Blocks, b => b.Text == "回复");
    }

    [Fact]
    public void ScrollOverlapRetainsPostIdentityAndDoesNotMergeNextPost()
    {
        var session = reconciler.Apply(Empty(), Frame(("@alice", 10), ("A sufficiently long first paragraph about gardening.", 40),
            ("A sufficiently long second paragraph about gardening.", 70))).Session;
        Guid original = Assert.Single(observer.Build(session).Posts).Id;
        session = reconciler.Apply(session, Frame(("A sufficiently long second paragraph about gardening.", 10),
            ("A newly observed ending paragraph about gardening.", 40), ("赞 评论 分享", 75),
            ("@bob", 120), ("A new post about a wholly separate rocket topic.", 150))).Session;
        var catalog = observer.Build(session);
        Assert.Equal(2, catalog.Posts.Count);
        Assert.Contains(catalog.Posts, p => p.Id == original && p.BlockIds.Count == 4);
        Assert.DoesNotContain(observer.Scope(session, original).Blocks, b => b.Text.Contains("rocket"));
    }

    [Fact]
    public async Task DurablePostEvidenceSurvivesSnapshotEvictionAndDatabaseReload()
    {
        var session = Empty();
        for (int i = 0; i < 12; i++)
            session = reconciler.Apply(session, Frame(($"@author{i}", 10), ($"Unique synthetic post number {i:D2} has a substantial body.", 40))).Session;
        Assert.Equal(9, session.Snapshots.Count);
        var catalog = observer.Build(session);
        Assert.Equal(12, catalog.Posts.Count);
        Guid first = catalog.Posts[0].Id;
        Assert.False(catalog.Posts[0].IsVisible);
        Assert.Equal(10, catalog.Posts[0].Evidence[0].Bounds.Y);
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-posts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new SessionRepository(Path.Combine(directory, "fixture.db"));
            await repository.SaveAsync(session);
            var loaded = await repository.LoadAsync(session.Id);
            Assert.NotNull(loaded);
            var persisted = observer.Build(loaded);
            Assert.Equal(12, persisted.Posts.Count);
            Assert.Equal(first, persisted.Posts[0].Id);
            Assert.Equal(catalog.Posts[0].Evidence, persisted.Posts[0].Evidence);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LegacyTextRemainsUsableButExplicitlyUncertain()
    {
        var empty = Empty();
        var id = Guid.NewGuid();
        var legacy = empty with { Blocks = [new(id, "Older readable article", "Older readable article", "confirmed", new(1, 1, "Older readable article", 1, "Older readable article"))] };
        var catalog = observer.Build(legacy);
        var post = Assert.Single(catalog.Posts);
        Assert.True(post.Uncertain);
        Assert.NotEmpty(catalog.Warnings);
        Assert.Single(observer.Scope(legacy, post.Id).Blocks);
        Assert.Null(legacy.Blocks[0].Source.Geometry);
    }

    [Fact]
    public void IsolatedShortFragmentAfterBoundaryIsUnassignedInsteadOfContaminatingPreviousPost()
    {
        var session = reconciler.Apply(Empty(), Frame(("A substantial and complete gardening post.", 10),
            ("赞 评论 分享", 45), ("嗯", 100))).Session;
        var catalog = observer.Build(session);
        Assert.Single(catalog.Posts);
        Assert.Single(catalog.UnassignedBlockIds);
        Assert.DoesNotContain(observer.Scope(session, catalog.Posts[0].Id).Blocks, b => b.Text == "嗯");
        Assert.Throws<InvalidOperationException>(() => observer.Scope(session, Guid.NewGuid()));
    }

    [Fact]
    public void MultipleEdgeNavigationLabelsAreExcludedWithoutDiscardingBodyUseOfSameWords()
    {
        var frame = Frame(("This post discusses the 首页 feature in an application.", 40), ("The author has another sentence about navigation.", 70));
        frame.Blocks.AddRange([new("首页", new(2, 10, 40, 22)), new("发现", new(2, 45, 40, 22))]);
        var session = reconciler.Apply(Empty(), frame).Session;
        var catalog = observer.Build(session);
        Assert.Equal(2, catalog.ExcludedBlockIds.Count);
        Assert.Contains(observer.Scope(session, Assert.Single(catalog.Posts).Id).Blocks, b => b.Text.Contains("首页 feature"));
    }

    [Fact]
    public void TwoColumnFeedNeverMergesCoRowPostsOrSpanningHeader()
    {
        var frame = Frame();
        frame.Blocks.AddRange([
            new("@alice", new(20, 40, 110, 22)), new("@bob", new(430, 40, 110, 22)),
            new("The left gardening post is entirely independent.", new(20, 75, 320, 22)),
            new("The right space post is entirely independent.", new(430, 75, 320, 22)),
            new("Another gardening sentence on the left.", new(20, 105, 320, 22)),
            new("Another space sentence on the right.", new(430, 105, 320, 22)),
            new("A screen header spanning both content columns", new(20, 0, 730, 22))
        ]);
        var session = reconciler.Apply(Empty(), frame).Session;
        var catalog = observer.Build(session);
        Assert.Equal(2, catalog.Posts.Count);
        Assert.Single(catalog.UnassignedBlockIds);
        var left = catalog.Posts.Single(p => p.Label == "@alice");
        Assert.DoesNotContain(observer.Scope(session, left.Id).Blocks, b => b.Text.Contains("space") || b.Text.Contains("screen header"));
    }

    [Fact]
    public void IdenticalTextFromDifferentSourceWindowsGetsIndependentPostIdentity()
    {
        var frame = Frame(("@sameauthor", 10), ("An identical long paragraph copied between two unrelated windows.", 40));
        var session = reconciler.Apply(Empty(), frame with { ContextId = "window-one" }).Session;
        Guid original = Assert.Single(observer.Build(session).Posts).Id;
        session = reconciler.Apply(session, frame with { ContextId = "window-two" }).Session;
        var catalog = observer.Build(session);
        Assert.Equal(2, catalog.Posts.Count);
        Assert.NotEqual(original, catalog.Posts.Single(p => p.IsVisible).Id);
        Assert.Equal(2, session.Revision);
    }

    [Fact]
    public void IndentedCommentAuthorRemainsWithParentPost()
    {
        var frame = Frame(("@alice", 10), ("The parent post is discussing gardening and watering.", 40));
        frame.Blocks.AddRange([
            new("@commenter", new(150, 80, 140, 22)),
            new("An indented comment about watering follows.", new(150, 110, 490, 22))
        ]);
        var session = reconciler.Apply(Empty(), frame).Session;
        var post = Assert.Single(observer.Build(session).Posts);
        Assert.Equal(4, post.BlockIds.Count);
        Assert.Contains(post.Evidence, e => e.Role == "comment");
    }

    [Fact]
    public void MaskExcludesPreviouslyReadVideoTextFromGenerationWithoutErasingRawEvidence()
    {
        var frame = Frame(("Previously observed VIDEO transcript should not be sent.", 10),
            ("Visible OUTSIDE text remains safe to generate from.", 40));
        var session = reconciler.Apply(Empty(), frame).Session;
        var postId = Assert.Single(observer.Build(session).Posts).Id;
        session = observer.ExcludeVisibleRegions(session, [new(90, 0, 610, 35)]);
        Assert.Equal(2, session.Blocks.Count);
        Assert.Contains(session.Snapshots[0].Observation.Blocks, b => b.Text.Contains("VIDEO"));
        Assert.Single(observer.Build(session).ExcludedBlockIds);
        Assert.DoesNotContain(observer.Scope(session, postId).Blocks, b => b.Text.Contains("VIDEO"));
        session = reconciler.Apply(session, Frame(("Visible OUTSIDE text remains safe to generate from.", 40))).Session;
        Assert.DoesNotContain(observer.Scope(session, postId).Blocks, b => b.Text.Contains("VIDEO"));
        // Clearing the mask by itself does not resurrect stale video text.
        session = observer.ExcludeVisibleRegions(session, []);
        Assert.DoesNotContain(observer.Scope(session, postId).Blocks, b => b.Text.Contains("VIDEO"));
        session = reconciler.Apply(session, frame).Session;
        Assert.Contains(observer.Scope(session, postId).Blocks, b => b.Text.Contains("VIDEO"));
    }

    [Fact]
    public void FullyMaskedEmptyOcrThenClearCanRestoreIdenticalLastFrame()
    {
        var frame = Frame(("The entire original frame belongs to a video transcript.", 10));
        var session = reconciler.Apply(Empty(), frame).Session;
        session = observer.ExcludeVisibleRegions(session, [new(0, 0, 800, 100)]);
        Assert.Empty(observer.Build(session).Posts);
        Assert.Single(session.Blocks);
        session = reconciler.Apply(session, Frame()).Session;
        Assert.Equal(1, session.Revision);
        Assert.Empty(observer.Build(session).Posts);
        // Identical text must bypass the normal unchanged-text shortcut when its
        // previous evidence was masked and is now genuinely observed again.
        session = reconciler.Apply(session, frame).Session;
        Assert.Equal(2, session.Revision);
        Assert.Single(observer.Build(session).Posts);
        Assert.DoesNotContain(session.Blocks, b => b.Source.Geometry?.Role == "masked");
    }

    [Fact]
    public void MaskDoesNotApplyCurrentScreenPositionsToHistoricalPosts()
    {
        var session = reconciler.Apply(Empty(), Frame(("@first", 10), ("Historical content no longer visible in the viewport.", 40))).Session;
        Guid first = Assert.Single(observer.Build(session).Posts).Id;
        session = reconciler.Apply(session, Frame(("@second", 10), ("Current video content occupying the same old screen position.", 40))).Session;
        session = observer.ExcludeVisibleRegions(session, [new(0, 0, 800, 100)]);
        var catalog = observer.Build(session);
        Assert.Single(catalog.Posts);
        Assert.Equal(first, catalog.Posts[0].Id);
        Assert.Equal(2, catalog.ExcludedBlockIds.Count);
        Assert.Contains(observer.Scope(session, first).Blocks, b => b.Text.Contains("Historical"));
    }

    [Fact]
    public void MissingVisibleSnapshotDoesNotGuessHistoricalMaskCoordinates()
    {
        var session = reconciler.Apply(Empty(), Frame(("Historical layout is no longer available.", 10))).Session with
        { Snapshots = [], IgnoreRegions = [new(0, 0, 800, 100)] };
        session = observer.ExcludeVisibleRegions(session, session.IgnoreRegions);
        var catalog = observer.Build(session);
        Assert.Single(catalog.Posts);
        Assert.Empty(catalog.ExcludedBlockIds);
        Assert.Contains(catalog.Warnings, w => w.Contains("No recent visible layout"));
    }
}
