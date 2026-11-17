using System.Text.Json;
using Glimpse.Contracts;

namespace Glimpse.Agent;

public record CardValidation(bool IsValid, List<AgentCard> Cards, string? Error);

public static class CardValidator
{
    public const int MaximumResponseCharacters = 128000;
    public static string SchemaFor(string task) => task switch
    {
        "comment" => "comment.v1",
        "reply" => "reply.v1",
        "summary" => "summary.v1",
        "analysis" or "custom" => "analysis.v1",
        _ => throw new ArgumentException("Unknown Agent task.", nameof(task))
    };
    public static string OutputExampleFor(string task) => SchemaFor(task) switch
    {
        "comment.v1" => "{\"schema\":\"comment.v1\",\"options\":[{\"tone\":\"简洁\",\"text\":\"根据阅读内容撰写的实际评论\"}]}",
        "reply.v1" => "{\"schema\":\"reply.v1\",\"options\":[{\"tone\":\"礼貌\",\"text\":\"根据阅读内容撰写的实际回复\"}]}",
        "summary.v1" => "{\"schema\":\"summary.v1\",\"keyPoints\":[\"根据阅读内容提炼的实际要点\"]}",
        _ => "{\"schema\":\"analysis.v1\",\"findings\":[\"根据阅读内容得出的实际结论\"],\"caveats\":[]}"
    };
    public static string JsonSchemaFor(string task)
    {
        string schema = SchemaFor(task);
        return schema switch
        {
            "comment.v1" or "reply.v1" => """
                {"type":"object","additionalProperties":false,"required":["schema","options"],"properties":{"schema":{"const":"$SCHEMA"},"options":{"type":"array","minItems":1,"maxItems":6,"items":{"type":"object","additionalProperties":false,"required":["tone","text"],"properties":{"tone":{"type":"string","minLength":1,"maxLength":40},"text":{"type":"string","minLength":1,"maxLength":6000}}}}}}
                """.Replace("$SCHEMA", schema, StringComparison.Ordinal),
            "summary.v1" => """
                {"type":"object","additionalProperties":false,"required":["schema","keyPoints"],"properties":{"schema":{"const":"summary.v1"},"keyPoints":{"type":"array","minItems":1,"maxItems":12,"items":{"type":"string","minLength":1,"maxLength":4000}}}}
                """,
            _ => """
                {"type":"object","additionalProperties":false,"required":["schema","findings","caveats"],"properties":{"schema":{"const":"analysis.v1"},"findings":{"type":"array","minItems":1,"maxItems":12,"items":{"type":"string","minLength":1,"maxLength":4000}},"caveats":{"type":"array","maxItems":12,"items":{"type":"string","minLength":1,"maxLength":4000}}}}
                """
        };
    }
    public static CardValidation Validate(string text, string expectedSchema)
    {
        try
        {
            if (text.Length > MaximumResponseCharacters) return Invalid();
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.String || schema.GetString() != expectedSchema) return Invalid();
            var cards = new List<AgentCard>();
            if (expectedSchema is "comment.v1" or "reply.v1")
            {
                if (!Properties(root, "schema", "options") || !Array(root.GetProperty("options"), 1, 6)) return Invalid();
                int number = 0;
                foreach (var option in root.GetProperty("options").EnumerateArray())
                {
                    if (!Properties(option, "tone", "text") || !String(option.GetProperty("tone"), 40) ||
                        !String(option.GetProperty("text"), 6000)) return Invalid();
                    string tone = option.GetProperty("tone").GetString()!;
                    cards.Add(new($"{++number}. {tone}", option.GetProperty("text").GetString()!, tone));
                }
            }
            else if (expectedSchema == "summary.v1")
            {
                if (!Properties(root, "schema", "keyPoints") || !Array(root.GetProperty("keyPoints"), 1, 12)) return Invalid();
                int number = 0;
                foreach (var point in root.GetProperty("keyPoints").EnumerateArray())
                {
                    if (!String(point, 4000)) return Invalid();
                    cards.Add(new($"要点 {++number}", point.GetString()!));
                }
            }
            else if (expectedSchema == "analysis.v1")
            {
                if (!Properties(root, "schema", "findings", "caveats") || !Array(root.GetProperty("findings"), 1, 12) ||
                    !Array(root.GetProperty("caveats"), 0, 12)) return Invalid();
                foreach (var (property, title) in new[] { ("findings", "发现"), ("caveats", "注意") })
                {
                    int number = 0;
                    foreach (var item in root.GetProperty(property).EnumerateArray())
                    {
                        if (!String(item, 4000)) return Invalid();
                        cards.Add(new($"{title} {++number}", item.GetString()!));
                    }
                }
            }
            else return Invalid();
            return new(true, cards, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return Invalid(); }
    }
    private static CardValidation Invalid() => new(false, [], "模型输出未通过本地卡片格式校验，以下为原始文本。");
    private static bool String(JsonElement element, int maximum) => element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString()) && element.GetString()!.Length <= maximum;
    private static bool Array(JsonElement element, int minimum, int maximum) => element.ValueKind == JsonValueKind.Array &&
        element.GetArrayLength() >= minimum && element.GetArrayLength() <= maximum;
    private static bool Properties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var actual = element.EnumerateObject().Select(p => p.Name).ToArray();
        return actual.Length == expected.Length && actual.Distinct(StringComparer.Ordinal).Count() == actual.Length &&
            actual.All(name => expected.Contains(name, StringComparer.Ordinal));
    }
}
