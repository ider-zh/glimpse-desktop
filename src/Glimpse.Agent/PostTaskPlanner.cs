using System.Text.Json;
using Glimpse.Contracts;

namespace Glimpse.Agent;

// Local orchestration only. The parent envelope is inspectable but is NEVER sent to a model.
public static class PostTaskPlanner
{
    public const int MaximumPosts = 4;
    public const int Parallelism = 2;

    public static AgentContextPreview Build(SessionData session, AgentTaskRequest request)
    {
        var builder = new SessionContextBuilder();
        if (request.PostIds is not { Count: > 0 } ids) return builder.Build(session, request);
        if (request.PostId is not null || ids.Count > MaximumPosts || ids.Distinct().Count() != ids.Count)
            throw new InvalidOperationException("请选择 1–4 条不同的帖子，或仅选择一条帖子。");
        if (ids.Count == 1) return builder.Build(session, request with { PostId = ids[0], PostIds = null });
        if (request.ContextCharacterBudget is < 1000 or > 24000)
            throw new ArgumentException("Context budget must be 1000–24000 characters.", nameof(request));
        // Budget is shared across children, rather than multiplying the configured upload allowance.
        if (request.ContextCharacterBudget / ids.Count < 1000)
            throw new InvalidOperationException("当前上下文预算不足以分条处理，请减少帖子或提高预算。");
        var children = ids.Select(id => builder.Build(session, request with
        {
            PostId = id, PostIds = null, ContextCharacterBudget = request.ContextCharacterBudget / ids.Count
        })).ToList();
        var first = children[0];
        return new(Guid.NewGuid(), session.Id, session.Revision, first.TaskType, first.Schema, first.JsonSchema,
            "Local post dispatcher. Each child below is sent independently; this parent is never sent to a provider.",
            JsonSerializer.Serialize(children, Envelope.Json), children.SelectMany(c => c.SelectedBlockIds).Distinct().ToList(),
            [$"分条处理 {children.Count} 条帖子；最多并行 {Parallelism} 路，各任务上下文独立。"],
            children.Sum(c => c.RedactionCount), children.Sum(c => c.EstimatedTokens), Children: children);
    }

    public static AgentResult Combine(AgentContextPreview parent, string provider, List<AgentResult> children, long elapsedMs)
    {
        var cards = new List<AgentCard>();
        var warnings = new List<string>(parent.Warnings);
        warnings.AddRange(children.SelectMany(c => c.Warnings).Distinct());
        foreach (var child in children)
        {
            string title = child.PostTitle ?? "帖子";
            cards.AddRange(child.Cards.Select(c => c with { Title = $"{title} · {c.Title}" }));
            if (child.Status == "fallback" && child.FallbackText is not null)
                cards.Add(new($"{title} · 原始回答（格式未通过校验）", child.FallbackText));
            if (child.Status != "completed") warnings.Add($"{title}：{child.Status}。{child.Error}");
        }
        string status = children.All(c => c.Status == "completed") ? "completed" : cards.Count > 0 ? "partial" :
            children.Any(c => c.Status == "canceled") ? "canceled" : children.Any(c => c.Status == "timeout") ? "timeout" : "error";
        return new(parent.PreviewId, parent.SessionId, parent.DocumentRevision, provider, parent.TaskType, parent.Schema,
            status, cards, null, status is "error" or "timeout" ? "分条任务未生成有效结果，请查看各帖子状态。" : null,
            elapsedMs, warnings.Distinct().ToList(), PostResults: children);
    }
}
