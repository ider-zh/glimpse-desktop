using System.Diagnostics;
using Glimpse.Contracts;

namespace Glimpse.Agent;

public sealed class AgentRunner(ILLMProvider provider, IContextBuilder? contextBuilder = null)
{
    private readonly IContextBuilder contexts = contextBuilder ?? new SessionContextBuilder();
    public AgentContextPreview BuildPreview(SessionData session, AgentTaskRequest request) => contexts.Build(session, request);

    // Never re-reads or expands context: this sends precisely the two strings the user
    // inspected. Session/revision validation and explicit triggering belong to Worker.
    public async Task<AgentResult> RunPreparedAsync(AgentContextPreview preview, int timeoutSeconds = 60,
        CancellationToken cancellation = default)
    {
        if (timeoutSeconds is < 1 or > 180) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        if (preview.Children is { Count: > 0 })
            throw new InvalidOperationException("A local dispatch envelope must never be submitted as a model prompt.");
        var timer = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<ProviderResponse>? pending = null;
        AgentResult Result(string status, List<AgentCard>? cards = null, string? fallback = null, string? error = null) =>
            new(preview.PreviewId, preview.SessionId, preview.DocumentRevision, provider.Id, preview.TaskType, preview.Schema,
                status, cards ?? [], fallback, error, timer.ElapsedMilliseconds, preview.Warnings.ToList(), preview.PostId, preview.PostTitle);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var request = new ProviderRequest(preview.SystemPrompt, preview.UserPrompt, preview.Schema, preview.JsonSchema);
            pending = provider.GenerateAsync(request, linked.Token);
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellation);
            var validation = CardValidator.Validate(response.Text, preview.Schema);
            if (validation.IsValid) return Result("completed", validation.Cards);
            string fallback = response.Text.Length <= CardValidator.MaximumResponseCharacters ? response.Text :
                response.Text[..CardValidator.MaximumResponseCharacters] + "\n[输出过长，后续内容已截断]";
            return Result("fallback", fallback: fallback, error: validation.Error);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            linked.Cancel(); return Result("canceled", error: "调用已取消。");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            linked.Cancel(); return Result("timeout", error: "调用超时，已请求取消。请重试或切换引擎。");
        }
        catch (ProviderFailureException ex)
        {
            linked.Cancel(); return Result("error", error: ex.Message);
        }
        catch (Exception ex)
        {
            linked.Cancel(); return Result("error", error: $"引擎调用失败（{ex.GetType().Name}）。请检查配置后重试。");
        }
        finally
        {
            // A misbehaving provider may ignore cancellation. Observe a late failure
            // without awaiting it, so a timeout cannot stall capture/IPC or cancellation.
            if (pending is { IsCompleted: false })
                _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
