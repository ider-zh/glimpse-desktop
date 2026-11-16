using System.IO;
using System.Text.Json;
using Glimpse.Agent;
using Glimpse.Contracts;
using Glimpse.Providers;
using Glimpse.Storage;
using Glimpse.Worker;

namespace Glimpse.Windows.Probe;

internal static class AgentControllerProbes
{
    private const string Output = """{"schema":"summary.v1","keyPoints":["A copyable offline Agent result."]}""";

    public static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-agent-controller-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var repository = new SessionRepository(Path.Combine(directory, "sessions.db"));
        var settings = new ProviderSettingsStore(directory);
        SessionData? current = Session("Synthetic source text. Contact private@example.com; API key: sk-fixture-sensitive-secret.");
        await repository.SaveAsync(current);
        int providerCreations = 0;
        var provider = new FakeProvider();
        var delayed = new List<FakeProvider>();
        var running = new List<Task<AgentResult>>();
        using var controller = new AgentController(settings, repository,
            cancellation => { cancellation.ThrowIfCancellationRequested(); return Task.FromResult<SessionData?>(current); },
            (_, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested(); providerCreations++;
                return Task.FromResult<ILLMProvider>(provider);
            });
        AgentTaskRequest Request(string prompt = "Summarize the selected evidence.") =>
            new(current?.Id ?? throw new InvalidOperationException("Probe requires a current session."), "summary", prompt);
        try
        {
            // Preview does not construct an engine, load credentials or invoke inference.
            var preview = await controller.PreviewAsync(Request(), CancellationToken.None);
            Require(providerCreations == 0 && provider.Calls == 0, "Preview invoked a provider.");
            Require(!preview.UserPrompt.Contains("private@example.com", StringComparison.Ordinal) &&
                !preview.UserPrompt.Contains("sk-fixture-sensitive-secret", StringComparison.Ordinal) && preview.RedactionCount >= 2,
                "Preview did not filter sensitive fixture text.");
            var completed = await controller.RunAsync(preview.PreviewId, CancellationToken.None);
            Require(completed.Status == "completed" && completed.Cards.Count == 1 &&
                completed.Cards[0].Text == "A copyable offline Agent result.", "Agent result failed local card validation.");
            var history = await repository.ListAgentResultsAsync(current.Id);
            Require(history.Count == 1 && history[0].PreviewId == preview.PreviewId &&
                history[0].DocumentRevision == preview.DocumentRevision, "Validated result was not persisted with frozen provenance.");
            await Rejected(() => controller.RunAsync(preview.PreviewId, CancellationToken.None), "Consumed preview could be replayed.");

            var switchedPreview = await controller.PreviewAsync(Request(), CancellationToken.None);
            var original = current;
            current = Session("Another synthetic session.");
            await repository.SaveAsync(current);
            int beforeSwitch = providerCreations;
            await Rejected(() => controller.RunAsync(switchedPreview.PreviewId, CancellationToken.None), "Old session preview survived a session switch.");
            Require(providerCreations == beforeSwitch, "Rejected session preview constructed a provider.");
            current = original;

            var invalidated = await controller.PreviewAsync(Request(), CancellationToken.None);
            var configured = await settings.LoadPublicAsync();
            var other = configured.Providers.First(p => p.Id != configured.SelectedProviderId && p.Kind == "openai-chat");
            await settings.SaveAsync(new(other.Id, other));
            controller.Invalidate();
            await Rejected(() => controller.RunAsync(invalidated.PreviewId, CancellationToken.None), "Settings invalidation retained a cached preview.");

            // Also reject a settings mutation if a caller omitted explicit invalidation.
            var changedProfilePreview = await controller.PreviewAsync(Request(), CancellationToken.None);
            var selected = (await settings.LoadPublicAsync()).Providers.First(p => p.Id == other.Id);
            await settings.SaveAsync(new(selected.Id, selected with { Model = "offline-model-changed" }));
            await Rejected(() => controller.RunAsync(changedProfilePreview.PreviewId, CancellationToken.None), "Changed engine profile reused an outdated preview.");

            provider = new FakeProvider(delayed: true); delayed.Add(provider);
            var frozen = await controller.PreviewAsync(Request(), CancellationToken.None);
            var frozenRun = controller.RunAsync(frozen.PreviewId, CancellationToken.None); running.Add(frozenRun);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            const string laterText = "OCR added this marker after payload preview; it must not be transmitted silently.";
            current = current with
            {
                Revision = current.Revision + 1,
                Blocks = current.Blocks.Append(Block(laterText, current.Revision + 1)).ToList()
            };
            await repository.SaveAsync(current);
            var busyPreview = await controller.PreviewAsync(Request(), CancellationToken.None);
            await Rejected(() => controller.RunAsync(busyPreview.PreviewId, CancellationToken.None), "Parallel Agent generation was not rejected.");
            provider.Finish.TrySetResult(new(Output, "offline"));
            var frozenResult = await frozenRun.WaitAsync(TimeSpan.FromSeconds(3));
            Require(frozenResult.Status == "completed" && frozenResult.DocumentRevision == frozen.DocumentRevision &&
                provider.LastRequest?.SystemPrompt == frozen.SystemPrompt && provider.LastRequest.UserPrompt == frozen.UserPrompt &&
                !provider.LastRequest.UserPrompt.Contains(laterText, StringComparison.Ordinal),
                "Continuing OCR mutated the reviewed outbound payload.");

            provider = new FakeProvider(delayed: true); delayed.Add(provider);
            var canceledPreview = await controller.PreviewAsync(Request(), CancellationToken.None);
            var canceledRun = controller.RunAsync(canceledPreview.PreviewId, CancellationToken.None); running.Add(canceledRun);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            controller.Cancel(Guid.NewGuid());
            Require(!provider.Token.IsCancellationRequested && !canceledRun.IsCompleted, "Canceling another preview canceled the active request.");
            controller.Cancel(canceledPreview.PreviewId);
            var canceled = await canceledRun.WaitAsync(TimeSpan.FromSeconds(3));
            Require(canceled.Status == "canceled" && provider.Token.IsCancellationRequested,
                "An ignoring-cancellation provider blocked cancellation or returned a successful result.");
            provider.Finish.TrySetResult(new(Output, "offline"));
            Require((await repository.ListAgentResultsAsync(current.Id)).Any(r => r.PreviewId == canceledPreview.PreviewId && r.Status == "canceled"),
                "Canceled request status was not persisted.");

            provider = new FakeProvider(delayed: true); delayed.Add(provider);
            var removedPreview = await controller.PreviewAsync(Request(), CancellationToken.None);
            var removedRun = controller.RunAsync(removedPreview.PreviewId, CancellationToken.None); running.Add(removedRun);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Guid deletedId = current.Id;
            await repository.DeleteAsync(deletedId);
            current = null;
            provider.Finish.TrySetResult(new(Output, "offline"));
            _ = await removedRun.WaitAsync(TimeSpan.FromSeconds(3));
            Require(await repository.LoadAsync(deletedId) is null && (await repository.ListAgentResultsAsync(deletedId)).Count == 0,
                "A late Agent result resurrected a deleted session or its result history.");

            current = Session("Safe exception fixture."); await repository.SaveAsync(current);
            const string actualSecret = "sk-runtime-private-error-value";
            provider = new FakeProvider(failure: actualSecret);
            var errorPreview = await controller.PreviewAsync(Request(), CancellationToken.None);
            var error = await controller.RunAsync(errorPreview.PreviewId, CancellationToken.None);
            Require(error.Status == "error" && error.Error is not null && !error.Error.Contains(actualSecret, StringComparison.Ordinal),
                "Provider exception leaked a sensitive runtime value.");
            string serialized = JsonSerializer.Serialize(await repository.ListAgentResultsAsync(current.Id), Envelope.Json);
            Require(!serialized.Contains(actualSecret, StringComparison.Ordinal), "Persisted result history leaked an exception secret.");

            Console.WriteLine("AGENT CONTROLLER PASS: no-inference preview, redaction, validated SQLite history, one-shot previews, session/config invalidation, frozen payload under OCR updates, busy rejection, targeted cancellation, deleted-session safety, exception redaction.");
        }
        finally
        {
            controller.Cancel();
            foreach (var fake in delayed) fake.Finish.TrySetResult(new(Output, "offline"));
            foreach (var task in running)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException) { }
            }
            await ProbeCleanup.DeleteAsync(directory);
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task Rejected(Func<Task<AgentResult>> operation, string failure)
    {
        try { _ = await operation(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(failure);
    }
    private static SessionData Session(string text) => new(Guid.NewGuid(), "Agent controller fixture", "paused", 1, 1,
        new(0, 0, 800, 600), 7, [Block(text, 1)], [], []);
    private static ContentBlock Block(string text, int sequence) => new(Guid.NewGuid(), text, text, "confirmed",
        new(1, sequence, text, 1, text));
    private sealed class FakeProvider(bool delayed = false, string? failure = null) : ILLMProvider
    {
        public string Id => "offline-controller-fixture";
        public int Calls { get; private set; }
        public ProviderRequest? LastRequest { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProviderResponse> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProviderResponse> GenerateAsync(ProviderRequest request, CancellationToken cancellation)
        {
            Calls++; LastRequest = request; Token = cancellation; Started.TrySetResult();
            if (failure is not null) throw new IOException(failure);
            // The delayed fake intentionally ignores its cancellation token.
            return delayed ? Finish.Task : Task.FromResult(new ProviderResponse(Output, "offline"));
        }
    }
}
