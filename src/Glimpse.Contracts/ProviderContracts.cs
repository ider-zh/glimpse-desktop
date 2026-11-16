namespace Glimpse.Contracts;

public record AgentProviderProfile(string Id, string DisplayName, string Kind, string BaseUrl,
    string Model, string CliPath, string CliHostPath, int TimeoutSeconds, bool HasApiKey);

public record AgentSettings(string SelectedProviderId, List<AgentProviderProfile> Providers,
    int ContextCharacterBudget = 12000);

// NewApiKey is a one-time write-only input. Never include it in normal diagnostics.
public record AgentProviderUpdate(string SelectedProviderId, AgentProviderProfile Profile,
    string? NewApiKey = null, bool ClearApiKey = false, int ContextCharacterBudget = 12000)
{
    public override string ToString() => $"AgentProviderUpdate {{ ProviderId = {Profile.Id}, ClearApiKey = {ClearApiKey} }}";
}
