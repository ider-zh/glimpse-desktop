using Glimpse.Contracts;

namespace Glimpse.Agent;

public interface ILLMProvider
{
    string Id { get; }
    Task<ProviderResponse> GenerateAsync(ProviderRequest request, CancellationToken cancellation);
}

public interface IContextBuilder
{
    AgentContextPreview Build(SessionData session, AgentTaskRequest request);
}
