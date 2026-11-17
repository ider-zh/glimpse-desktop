using System.Collections.Concurrent;
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
        var preview = new SessionContextBuilder().Build(session, request with
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
            var provider = providerFactory is null ? await settings.CreateProviderAsync(profile.Id, linked.Token) :
                await providerFactory(profile.Id, linked.Token);
            var result = await new AgentRunner(provider).RunPreparedAsync(prepared.Preview, prepared.Timeout, linked.Token);
            await repository.SaveAgentResultAsync(result, profile.Model, CancellationToken.None);
            return result;
        }
        finally
        {
            lock (state) { active = null; activePreview = null; }
            execution.Release();
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
