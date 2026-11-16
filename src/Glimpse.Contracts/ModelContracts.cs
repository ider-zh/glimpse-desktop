namespace Glimpse.Contracts;

public record AgentModelOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName == Id ? Id : $"{DisplayName} · {Id}";
}
public record AgentModelCatalog(string ProviderId, List<AgentModelOption> Models, string? CurrentModelId = null)
{
    public override string ToString() => $"Agent model catalog: provider={ProviderId}, count={Models.Count}";
}
