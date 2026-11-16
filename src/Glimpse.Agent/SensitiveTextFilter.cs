using System.Text.RegularExpressions;

namespace Glimpse.Agent;

public record FilteredText(string Text, int RedactionCount);

public sealed partial class SensitiveTextFilter
{
    [GeneratedRegex(@"(?i)\b(?:sk|sk-proj)-[a-z0-9_-]{8,}\b")]
    private static partial Regex ApiKeys();
    [GeneratedRegex(@"(?i)\bBearer\s+[a-z0-9._~+/-]{8,}=*")]
    private static partial Regex BearerTokens();
    [GeneratedRegex(@"(?i)(?:api[_ -]?key|access[_ -]?token|secret|password|密码|令牌|密钥)\s*[:=：]\s*[^\s,;，；]{4,}")]
    private static partial Regex LabeledSecrets();
    [GeneratedRegex(@"(?i)\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b")]
    private static partial Regex Emails();
    [GeneratedRegex(@"(?<!\d)(?:\+?86[- ]?)?1[3-9]\d{9}(?!\d)")]
    private static partial Regex ChinesePhones();
    [GeneratedRegex(@"(?<!\d)\d{17}[\dXx](?!\d)")]
    private static partial Regex IdentityNumbers();

    // Best effort, not a claim to remove all PII. Called only on local copies destined
    // for the explicit provider request; stored OCR evidence is never modified.
    public FilteredText Redact(string text)
    {
        int redactions = 0;
        foreach (var (pattern, replacement) in new[]
        {
            (ApiKeys(), "[REDACTED_SECRET]"), (BearerTokens(), "[REDACTED_SECRET]"),
            (LabeledSecrets(), "[REDACTED_SECRET]"), (Emails(), "[REDACTED_EMAIL]"),
            (ChinesePhones(), "[REDACTED_PHONE]"), (IdentityNumbers(), "[REDACTED_ID]")
        })
            text = pattern.Replace(text, _ => { redactions++; return replacement; });
        return new(text, redactions);
    }
}
