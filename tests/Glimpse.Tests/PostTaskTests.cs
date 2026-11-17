using Glimpse.Agent;
using Glimpse.Contracts;
using Xunit;

namespace Glimpse.Tests;

public class PostTaskTests
{
    internal static SessionData Fixture()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        ContentBlock Block(string text, Guid id, int y, string role = "body") => new(Guid.NewGuid(), text, text, "confirmed",
            new(1, 1, text, 1, text, Geometry: new(1, new(30, y, 600, 28), "fixture", id, role)));
        return new(Guid.NewGuid(), "Synthetic isolated posts", "paused", 1, 1, new(0, 0, 800, 600), 4,
            [Block("ORANGE_POST: weekend library reading club.", first, 30),
             Block("ORANGE_POST: bring a book and a discussion question.", first, 70),
             Block("PURPLE_POST: Sunday mountain walk starts at nine.", second, 300),
             Block("PURPLE_POST: bring water and wear walking shoes.", second, 340),
             Block("Like Reply Share", Guid.NewGuid(), 400, "control")], [], []);
    }

    [Fact]
    public void BatchChildrenHaveDisjointEvidenceAndNoOtherPostOrControlText()
    {
        var session = Fixture();
        var ids = session.Blocks.Take(4).Select(b => b.Source.Geometry!.PostId!.Value).Distinct().ToList();
        var plan = PostTaskPlanner.Build(session, new(session.Id, "summary", PostIds: ids));
        Assert.NotNull(plan.Children);
        Assert.Equal(2, plan.Children.Count);
        Assert.Empty(plan.Children[0].SelectedBlockIds.Intersect(plan.Children[1].SelectedBlockIds));
        Assert.Contains("ORANGE_POST", plan.Children[0].UserPrompt);
        Assert.DoesNotContain("PURPLE_POST", plan.Children[0].UserPrompt);
        Assert.Contains("PURPLE_POST", plan.Children[1].UserPrompt);
        Assert.DoesNotContain("ORANGE_POST", plan.Children[1].UserPrompt);
        Assert.All(plan.Children, child => Assert.DoesNotContain("Like Reply Share", child.UserPrompt));
        Assert.All(plan.Children, child => Assert.Equal(session.Revision, child.DocumentRevision));
    }

    [Fact]
    public void UnknownPostAndOversizedBatchAreRejectedBeforeInference()
    {
        var session = Fixture();
        Assert.Throws<InvalidOperationException>(() => PostTaskPlanner.Build(session, new(session.Id, "summary", PostId: Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => PostTaskPlanner.Build(session,
            new(session.Id, "summary", PostIds: Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList())));
    }

    [Fact]
    public void UnscopedMultiplePostsAreRejectedRatherThanMixed()
    {
        var session = Fixture();
        Assert.Throws<InvalidOperationException>(() => new SessionContextBuilder().Build(session, new(session.Id, "summary")));
    }

    [Fact]
    public void PartialResultsRetainPostIdentityAndCopyableCompletedText()
    {
        var session = Fixture();
        var ids = session.Blocks.Take(4).Select(b => b.Source.Geometry!.PostId!.Value).Distinct().ToList();
        var plan = PostTaskPlanner.Build(session, new(session.Id, "summary", PostIds: ids));
        var children = plan.Children!.Select((p, i) => new AgentResult(p.PreviewId, p.SessionId, p.DocumentRevision,
            "fake", p.TaskType, p.Schema, i == 0 ? "completed" : "timeout", i == 0 ? [new("Summary", "EXACT TEXT")] : [],
            null, i == 0 ? null : "Timeout", 20, p.Warnings, p.PostId, p.PostTitle)).ToList();
        var combined = PostTaskPlanner.Combine(plan, "fake", children, 30);
        Assert.Equal("partial", combined.Status);
        Assert.Equal("EXACT TEXT", Assert.Single(combined.Cards).Text);
        Assert.Equal(ids, combined.PostResults!.Select(r => r.PostId!.Value));
        Assert.Contains(combined.Warnings, w => w.Contains("timeout"));
    }
}
