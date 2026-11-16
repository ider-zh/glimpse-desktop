namespace Glimpse.Contracts;

public record AgentTaskRequest(Guid SessionId, string TaskType, string Prompt = "",
    int ContextCharacterBudget = 12000, int TimeoutSeconds = 60);

public record ProviderRequest(string SystemPrompt, string UserPrompt, string Schema,
    string JsonSchema, int MaxOutputTokens = 1500);
public record ProviderResponse(string Text, string? Model = null);

// Preview strings are the exact filtered messages that will be passed to the provider.
// They are local UI data and must never be written into ordinary logs.
public record AgentContextPreview(Guid PreviewId, Guid SessionId, int DocumentRevision,
    string TaskType, string Schema, string JsonSchema, string SystemPrompt, string UserPrompt,
    List<Guid> SelectedBlockIds, List<string> Warnings, int RedactionCount, int EstimatedTokens);

public record AgentCard(string Title, string Text, string? Tone = null);
public record AgentResult(Guid PreviewId, Guid SessionId, int DocumentRevision, string ProviderId,
    string TaskType, string Schema, string Status, List<AgentCard> Cards, string? FallbackText,
    string? Error, long ElapsedMs, List<string> Warnings);

// Login terminal contents and URLs can contain short-lived authentication secrets.
// They are ephemeral UI data, never session history or ordinary diagnostic text.
public record CliLoginSnapshot(Guid LoginId, string Status, string ScreenText, List<string> Links, int? ExitCode = null,
    string TerminalOutput = "", long OutputStart = 0, int Columns = 110, int Rows = 32)
{
    public override string ToString() => $"CLI login: {Status}, screenChars={ScreenText.Length}, terminalChars={TerminalOutput.Length}, links={Links.Count}";
}
public record CliLoginRequest(Guid LoginId);
public record CliLoginResize(Guid LoginId, int Columns, int Rows);
public record CliLoginInput(Guid LoginId, string Text)
{
    public override string ToString() => $"CLI login input: chars={Text.Length}";
}
