using System.Collections.Concurrent;
using System.Diagnostics;
using Glimpse.Agent;
using Glimpse.Contracts;
using Glimpse.Providers;
using Glimpse.Storage;

namespace Glimpse.Worker;

public sealed class AgentController(ProviderSettingsStore settings, SessionRepository repository,
    Func<CancellationToken, Task<SessionData?>> currentSession,
    Func<string, CancellationToken, Task<ILLMProvider>>? providerFactory = null) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Prepared> previews = new();
    private readonly SemaphoreSlim execution = new(1);
    private readonly object state = new();
    private CancellationTokenSource? active;
    private Guid? activePreview;
    public bool IsRunning { get { lock (state) return active is not null; } }

    public async Task<AgentContextPreview> PreviewAsync(AgentTaskRequest request, CancellationToken cancellation)
    {
        var session = await currentSession(cancellation);
        if (session?.Id != request.SessionId) throw new InvalidOperationException("请先打开要使用的会话。");
        if (request.TimeoutSeconds is < 1 or > 180) throw new InvalidOperationException("调用时限必须在 1–180 秒之间。");
        var configured = await settings.LoadPublicAsync(cancellation);
        var profile = configured.Providers.First(p => p.Id == configured.SelectedProviderId);
        var preview = PostTaskPlanner.Build(session, request with
        { ContextCharacterBudget = Math.Min(request.ContextCharacterBudget, configured.ContextCharacterBudget) });
        if (previews.Count >= 4) previews.Clear();
        previews[preview.PreviewId] = new(preview, profile, Math.Min(request.TimeoutSeconds, profile.TimeoutSeconds), DateTimeOffset.UtcNow);
        return preview;
    }

    public async Task<AgentResult> RunAsync(Guid previewId, CancellationToken cancellation)
    {
        if (!await execution.WaitAsync(0, cancellation)) throw new InvalidOperationException("已有一个 Agent 调用正在运行，请先取消或等待完成。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            lock (state) { active = linked; activePreview = previewId; }
            if (!previews.TryRemove(previewId, out var prepared) || DateTimeOffset.UtcNow - prepared.Created > TimeSpan.FromMinutes(15))
                throw new InvalidOperationException("预览已失效，请重新预览后再生成。");
            var session = await currentSession(linked.Token);
            if (session?.Id != prepared.Preview.SessionId)
                throw new InvalidOperationException("会话已切换，请为当前会话重新预览。");
            var configured = await settings.LoadPublicAsync(linked.Token);
            var profile = configured.Providers.First(p => p.Id == configured.SelectedProviderId);
            if (profile != prepared.Profile)
                throw new InvalidOperationException("引擎设置已变化，请重新预览后再生成。");
            // The frozen preview is the entire outbound prompt. New OCR revisions never
            // silently add text between review and generation.
            AgentResult result;
            if (prepared.Preview.Children is { Count: > 0 } children)
            {
                var timer = Stopwatch.StartNew();
                using var slots = new SemaphoreSlim(PostTaskPlanner.Parallelism);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(prepared.Timeout));
                var tasks = children.Select(async child =>
                {
                    bool acquired = false;
                    try
                    {
                        await slots.WaitAsync(deadline.Token); acquired = true;
                        var childResult = await RunChildAsync(child, profile.Id, prepared.Timeout, deadline.Token);
                        return childResult.Status == "canceled" && !linked.IsCancellationRequested && deadline.IsCancellationRequested
                            ? childResult with { Status = "timeout", Error = "本次批量任务已达到总等待上限。" } : childResult;
                    }
                    catch (OperationCanceledException)
                    {
                        return new AgentResult(child.PreviewId, child.SessionId, child.DocumentRevision, profile.Id,
                            child.TaskType, child.Schema, linked.IsCancellationRequested ? "canceled" : "timeout", [], null,
                            linked.IsCancellationRequested ? "调用已取消。" : "本次批量任务已达到总等待上限。", 0, child.Warnings, child.PostId, child.PostTitle);
                    }
                    finally { if (acquired) slots.Release(); }
                }).ToArray();
                result = PostTaskPlanner.Combine(prepared.Preview, profile.Id, (await Task.WhenAll(tasks)).ToList(), timer.ElapsedMilliseconds);
            }
            else result = await RunChildAsync(prepared.Preview, profile.Id, prepared.Timeout, linked.Token);
            await repository.SaveAgentResultAsync(result, profile.Model, CancellationToken.None);
            return result;
        }
        finally
        {
            lock (state) { active = null; activePreview = null; }
            execution.Release();
        }
    }

    private async Task<AgentResult> RunChildAsync(AgentContextPreview child, string profileId, int timeout, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // A fresh provider/CLI process per post; no shared conversation or other post payload.
            var provider = providerFactory is null ? await settings.CreateProviderAsync(profileId, cancellation) :
                await providerFactory(profileId, cancellation);
            return await new AgentRunner(provider).RunPreparedAsync(child, timeout, cancellation);
        }
        catch (OperationCanceledException)
        {
            return new(child.PreviewId, child.SessionId, child.DocumentRevision, profileId, child.TaskType, child.Schema,
                "canceled", [], null, "调用已取消。", 0, child.Warnings, child.PostId, child.PostTitle);
        }
        catch (Exception)
        {
            return new(child.PreviewId, child.SessionId, child.DocumentRevision, profileId, child.TaskType, child.Schema,
                "error", [], null, "无法启动帖子引擎，请检查配置后重试。", 0, child.Warnings, child.PostId, child.PostTitle);
        }
    }

    public void Cancel(Guid? previewId = null)
    {
        // A cancel can reach the serialized pipe loop before its background run
        // starts. Retire that prepared ID as well as canceling an active call.
        if (previewId is Guid preparedId) previews.TryRemove(preparedId, out _);
        lock (state)
            if (previewId is null || activePreview == previewId) active?.Cancel();
    }
    public void Invalidate()
    {
        previews.Clear(); Cancel();
    }
    public void Dispose() { Cancel(); execution.Dispose(); }
    private record Prepared(AgentContextPreview Preview, AgentProviderProfile Profile, int Timeout, DateTimeOffset Created);
}
