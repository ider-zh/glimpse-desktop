using System.IO;
using System.Text.Json;
using Glimpse.Agent;
using Glimpse.Contracts;
using Glimpse.Core;
using Glimpse.Providers;
using Glimpse.Storage;
using Glimpse.Worker;

namespace Glimpse.Windows.Probe;

internal static class PostAgentControllerProbes
{
    private const string Output = """{"schema":"summary.v1","keyPoints":["Isolated synthetic post result."]}""";
    public static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-post-controller-" + Guid.NewGuid());
        var repo = new SessionRepository(Path.Combine(directory, "sessions.db"));
        var settings = new ProviderSettingsStore(directory);
        SessionData current = Fixture();
        await repo.SaveAsync(current);
        var wave = new Wave();
        using var controller = new AgentController(settings, repo, cancellation =>
        {
            cancellation.ThrowIfCancellationRequested(); return Task.FromResult<SessionData?>(current);
        }, (_, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult<ILLMProvider>(wave.Create());
        });
        var running = new List<Task<AgentResult>>();
        List<Guid> ids = current.Blocks.Select(b => b.Source.Geometry!.PostId!.Value).Distinct().ToList();
        AgentTaskRequest Request(List<Guid>? selection = null) => new(current.Id, "summary", "Summarize only each assigned source.",
            ContextCharacterBudget: 8000, PostIds: selection ?? ids);
        try
        {
            var preview = await controller.PreviewAsync(Request(), CancellationToken.None);
            Require(wave.Providers.Count == 0, "Preview constructed a provider.");
            var children = preview.Children ?? throw new InvalidOperationException("No isolated child previews.");
            Require(children.Count == 4 && children.Select(c => c.PreviewId).Distinct().Count() == 4, "Child IDs are not independent.");
            foreach (var child in children)
            {
                var owned = current.Blocks.Where(b => b.Source.Geometry?.PostId == child.PostId).ToList();
                Require(child.SelectedBlockIds.All(id => owned.Any(b => b.Id == id)), "Child selected evidence from another post.");
                foreach (var block in current.Blocks)
                    Require(child.UserPrompt.Contains(block.Text[..22], StringComparison.Ordinal) == owned.Contains(block), "Cross-post marker leaked into isolated payload.");
            }
            int totalEvidenceCharacters = children.Sum(child =>
            {
                using var document = JsonDocument.Parse(child.UserPrompt);
                return document.RootElement.GetProperty("untrustedScreenContext").GetRawText().Length;
            });
            Require(totalEvidenceCharacters <= 8000, "Per-child processing multiplied the screen-context budget.");

            var run = controller.RunAsync(preview.PreviewId, CancellationToken.None); running.Add(run);
            await Until(() => wave.Calls == 2, "Two children did not start concurrently.");
            Require(wave.Providers.Count == 2 && wave.MaximumActive == 2 && !run.IsCompleted, "Scheduler did not cap active jobs at two.");
            current = current with { Revision = current.Revision + 1, Blocks = current.Blocks.Append(Block(ids[0], "LATER_OCR_MUST_NOT_UPLOAD", 3)).ToList() };
            await repo.SaveAsync(current);
            foreach (var provider in wave.Providers) provider.Finish.TrySetResult(new(Output));
            await Until(() => wave.Calls == 4, "Queued child jobs did not resume.");
            Require(wave.Providers.Count == 4 && wave.MaximumActive == 2, "Providers were reused or concurrency exceeded two.");
            foreach (var provider in wave.Providers) provider.Finish.TrySetResult(new(Output));
            var completed = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Require(completed.Status == "completed" && completed.PostResults?.Count == 4, "Batch did not preserve four post results.");
            Require(wave.Providers.Distinct().Count() == 4, "Posts reused the same provider instance.");
            foreach (var provider in wave.Providers)
            {
                var outbound = provider.Request ?? throw new InvalidOperationException("Provider missing request.");
                using var json = JsonDocument.Parse(outbound.UserPrompt);
                Guid id = json.RootElement.GetProperty("postId").GetGuid();
                var frozen = children.Single(c => c.PostId == id);
                Require(outbound.SystemPrompt == frozen.SystemPrompt && outbound.UserPrompt == frozen.UserPrompt &&
                    !outbound.UserPrompt.Contains("LATER_OCR_MUST_NOT_UPLOAD", StringComparison.Ordinal), "Frozen child payload changed during the batch.");
            }
            var history = await repo.ListAgentResultsAsync(current.Id);
            var stored = history.Single(r => r.PreviewId == preview.PreviewId);
            Require(stored.PostResults?.Count == 4 && stored.PostResults.All(r => r.Status == "completed") &&
                stored.DocumentRevision == preview.DocumentRevision && stored.PostResults.Select(r => r.PostId).SequenceEqual(ids.Select(id => (Guid?)id)),
                "SQLite did not preserve ordered child results and frozen revision.");
            await Rejected(() => controller.RunAsync(preview.PreviewId, CancellationToken.None), "Consumed parent preview replayed.");
            Console.WriteLine("POST CONTROLLER PASS: four isolated frozen payloads, fresh providers, max-two concurrency, shared evidence budget and SQLite child result roundtrip.");

            await Rejected(() => controller.PreviewAsync(Request([ids[0], ids[0]]), CancellationToken.None), "Duplicate posts accepted.");
            await Rejected(() => controller.PreviewAsync(Request(ids.Append(Guid.NewGuid()).ToList()), CancellationToken.None), "More than four posts accepted.");
            await Rejected(() => controller.PreviewAsync(Request([ids[0], Guid.NewGuid()]), CancellationToken.None), "Unknown post accepted.");
            await Rejected(() => controller.PreviewAsync(Request() with { ContextCharacterBudget = 3999 }, CancellationToken.None), "Insufficient shared context budget accepted.");
            await Rejected(() => controller.PreviewAsync(Request() with { PostId = ids[0] }, CancellationToken.None), "Conflicting single and multi selection accepted.");
            Require(wave.Providers.Count == 4, "Invalid selection constructed a provider.");

            var beforeRun = await controller.PreviewAsync(Request(), CancellationToken.None);
            controller.Cancel(beforeRun.PreviewId);
            await Rejected(() => controller.RunAsync(beforeRun.PreviewId, CancellationToken.None), "Canceled queued parent invoked children.");
            Require(wave.Providers.Count == 4, "Cancel-before-start constructed a provider.");
            foreach (bool invalidate in new[] { false, true })
            {
                wave = new Wave();
                var cancelPreview = await controller.PreviewAsync(Request(), CancellationToken.None);
                var cancelRun = controller.RunAsync(cancelPreview.PreviewId, CancellationToken.None); running.Add(cancelRun);
                await Until(() => wave.Calls == 2, "Cancellation batch did not start.");
                controller.Cancel(Guid.NewGuid());
                Require(!wave.Providers.Any(p => p.Token.IsCancellationRequested), "Unrelated preview canceled the active batch.");
                if (invalidate) controller.Invalidate(); else controller.Cancel(cancelPreview.PreviewId);
                var canceled = await cancelRun.WaitAsync(TimeSpan.FromSeconds(5));
                Require(canceled.Status == "canceled" && canceled.PostResults?.Count == 4 &&
                    canceled.PostResults.All(r => r.Status == "canceled") && wave.Calls == 2 && wave.Providers.Count == 2,
                    "Batch cancellation invoked a queued post or failed to cancel every child.");
                Require(wave.Providers.All(p => p.Token.IsCancellationRequested), "Batch cancellation did not propagate to both active providers.");
                Require((await repo.ListAgentResultsAsync(current.Id)).Any(r => r.PreviewId == cancelPreview.PreviewId && r.PostResults?.Count == 4 && r.Status == "canceled"),
                    "Canceled child statuses were not persisted.");
            }
            Console.WriteLine("POST CANCEL PASS: duplicate/unknown/over-limit/budget rejection, cancel-before-start, cancel and invalidate propagate to active children; queued jobs never construct or invoke providers.");
            wave = new Wave();
            var timedPreview = await controller.PreviewAsync(Request() with { TimeoutSeconds = 1 }, CancellationToken.None);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var timedRun = controller.RunAsync(timedPreview.PreviewId, CancellationToken.None); running.Add(timedRun);
            await Until(() => wave.Calls == 2, "Deadline batch did not start.");
            var timed = await timedRun.WaitAsync(TimeSpan.FromSeconds(4));
            Require(timed.Status == "timeout" && timed.PostResults?.Count == 4 && timed.PostResults.All(r => r.Status == "timeout") &&
                wave.Calls == 2 && wave.Providers.Count == 2 && wave.Providers.All(p => p.Token.IsCancellationRequested) &&
                timer.Elapsed < TimeSpan.FromSeconds(3), "Batch deadline was multiplied by queued waves or misreported as user cancellation.");
            Console.WriteLine("POST DEADLINE PASS: one shared deadline cancels active jobs, never starts queued jobs, and reports all four children as timeout.");
        }
        finally
        {
            controller.Cancel();
            foreach (var provider in wave.Providers) provider.Finish.TrySetResult(new(Output));
            try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            await ProbeCleanup.DeleteAsync(directory);
        }
    }

    private static SessionData Fixture()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        return new(Guid.NewGuid(), "Synthetic four-post controller fixture", "paused", 1, 1, new(0, 0, 800, 600), 1,
            ids.SelectMany((id, i) => new[] { Block(id, $"POST_{i}_EXCLUSIVE_MARKER_ALPHA with reliable synthetic content.", i * 2),
                Block(id, $"POST_{i}_EXCLUSIVE_MARKER_BRAVO additional source detail.", i * 2 + 1) }).ToList(), [], []);
    }
    private static ContentBlock Block(Guid postId, string text, int index) => new(Guid.NewGuid(), text, SessionReconciler.Normalize(text), "confirmed",
        new(1, 1, text, 1, text, Geometry: new(1, new(0, index * 30, 500, 24), "synthetic-controller", postId, "body")));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { while (!condition()) await Task.Delay(20, timeout.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException(message); }
    }
    private static async Task Rejected(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
    private sealed class Wave
    {
        private readonly object sync = new();
        private readonly List<FakeProvider> providers = [];
        private int active, maximum, calls;
        public List<FakeProvider> Providers { get { lock (sync) return providers.ToList(); } }
        public int Calls { get { lock (sync) return calls; } }
        public int MaximumActive { get { lock (sync) return maximum; } }
        public FakeProvider Create() { lock (sync) { var provider = new FakeProvider(this); providers.Add(provider); return provider; } }
        public void Enter() { lock (sync) { active++; calls++; maximum = Math.Max(maximum, active); } }
        public void Exit() { lock (sync) active--; }
    }
    private sealed class FakeProvider(Wave wave) : ILLMProvider
    {
        public string Id => "isolated-post-fixture";
        public TaskCompletionSource<ProviderResponse> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public async Task<ProviderResponse> GenerateAsync(ProviderRequest request, CancellationToken cancellation)
        {
            Request = request; Token = cancellation; wave.Enter();
            try { return await Finish.Task.WaitAsync(cancellation); }
            finally { wave.Exit(); }
        }
    }
}
