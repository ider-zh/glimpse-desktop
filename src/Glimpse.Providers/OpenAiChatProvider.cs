using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Glimpse.Agent;
using Glimpse.Contracts;

namespace Glimpse.Providers;

public sealed class OpenAiChatProvider : ILLMProvider
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly AgentProviderProfile profile;
    private readonly string? apiKey;
    private readonly HttpClient client;
    public string Id => profile.Id;
    public OpenAiChatProvider(AgentProviderProfile profile, string? apiKey, HttpClient? client = null)
    {
        this.profile = profile;
        this.apiKey = apiKey;
        this.client = client ?? SharedClient;
        _ = CompletionEndpoint(profile.BaseUrl);
        if (string.IsNullOrWhiteSpace(profile.Model)) throw new InvalidOperationException("请填写模型 ID。");
    }

    public static Uri CompletionEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("API 地址须为 HTTPS（本机地址可用 HTTP），且不能包含凭据、查询或片段。");
        string normalized = uri.AbsoluteUri.TrimEnd('/');
        return new Uri(normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? normalized : normalized + "/chat/completions");
    }

    public async Task<ProviderResponse> GenerateAsync(ProviderRequest request, CancellationToken cancellation)
    {
        var endpoint = CompletionEndpoint(profile.BaseUrl);
        if (string.IsNullOrEmpty(apiKey) && !endpoint.IsLoopback)
            throw new InvalidOperationException("此 API 引擎尚未配置密钥，请先在引擎设置中保存密钥。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (profile.Id == "xiaomi-mimo") message.Headers.Add("api-key", apiKey);
            else message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        var body = new Dictionary<string, object>
        {
            ["model"] = profile.Model,
            ["messages"] = new[] { new { role = "system", content = request.SystemPrompt }, new { role = "user", content = request.UserPrompt } },
            ["stream"] = false,
            // JSON mode is widely supported; the local validator remains authoritative.
            ["response_format"] = new { type = "json_object" },
            [profile.Id == "xiaomi-mimo" ? "max_completion_tokens" : "max_tokens"] = request.MaxOutputTokens
        };
        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API 认证失败，请检查密钥和模型访问权限。",
                    HttpStatusCode.TooManyRequests => "API 请求限流或额度不足，请稍后重试并检查供应商账户。",
                    _ => $"API 请求失败（HTTP {(int)response.StatusCode}）。请检查地址、模型和供应商状态。"
                });
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(bytes, timeout.Token);
                if (count == 0) break;
                if (buffer.Length + count > 1024 * 1024) throw new InvalidOperationException("API 输出超过 1 MiB 上限。");
                buffer.Write(bytes, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var answer) || !answer.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("API 未返回可用的文本回答。");
            string text = content.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("API 返回了空回答。");
            return new(text, root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() : profile.Model);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("API 引擎响应超时。"); }
        catch (JsonException) { throw new InvalidOperationException("API 返回了无效的响应格式。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接 API 引擎，请检查网络和地址。"); }
    }
}
