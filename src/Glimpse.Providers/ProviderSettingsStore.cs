using System.Text;
using System.Text.Json;
using Glimpse.Agent;
using Glimpse.Contracts;

namespace Glimpse.Providers;

public sealed class ProviderSettingsStore
{
    private readonly string path;
    private readonly SemaphoreSlim mutex = new(1);
    public ProviderSettingsStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        path = Path.Combine(dataDirectory, "agent-providers.json");
    }
    public async Task<AgentSettings> LoadPublicAsync(CancellationToken cancellation = default)
    {
        await mutex.WaitAsync(cancellation);
        try { return Public(await Read(cancellation)); }
        finally { mutex.Release(); }
    }
    public async Task<AgentSettings> SaveAsync(AgentProviderUpdate update, CancellationToken cancellation = default)
    {
        await mutex.WaitAsync(cancellation);
        try
        {
            var stored = await Read(cancellation);
            int index = stored.Profiles.FindIndex(p => p.Profile.Id == update.Profile.Id);
            if (index < 0 || !stored.Profiles.Any(p => p.Profile.Id == update.SelectedProviderId))
                throw new InvalidOperationException("Unknown Agent provider.");
            if (update.ContextCharacterBudget is < 1024 or > 24000) throw new InvalidOperationException("上下文预算应为 1024–24000 字符。");
            var profile = Validate(update.Profile, stored.Profiles[index].Profile.Kind);
            if (update.ClearApiKey && !string.IsNullOrEmpty(update.NewApiKey))
                throw new InvalidOperationException("清除密钥与设置新密钥不能同时进行。");
            if (update.NewApiKey?.Length > 4096 || update.NewApiKey?.Any(char.IsControl) == true)
                throw new InvalidOperationException("无效的 API 密钥格式。");
            if (update.NewApiKey?.Length > 0 && string.IsNullOrWhiteSpace(update.NewApiKey))
                throw new InvalidOperationException("API 密钥不能只包含空格。");
            string? protectedKey = stored.Profiles[index].ProtectedApiKey;
            if (update.ClearApiKey) protectedKey = null;
            else if (!string.IsNullOrEmpty(update.NewApiKey)) protectedKey = WindowsSecretProtection.Protect(update.NewApiKey.Trim());
            stored.Profiles[index] = new(profile with { HasApiKey = false }, protectedKey);
            stored = stored with { SelectedProviderId = update.SelectedProviderId, ContextCharacterBudget = update.ContextCharacterBudget };
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(stored, Envelope.Json), new UTF8Encoding(false), cancellation);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Public(stored);
        }
        finally { mutex.Release(); }
    }
    public async Task<ILLMProvider> CreateProviderAsync(string? providerId = null, CancellationToken cancellation = default)
    {
        await mutex.WaitAsync(cancellation);
        try
        {
            var stored = await Read(cancellation);
            var configured = stored.Profiles.FirstOrDefault(p => p.Profile.Id == (providerId ?? stored.SelectedProviderId)) ??
                throw new InvalidOperationException("Unknown Agent provider.");
            var profile = Validate(configured.Profile, configured.Profile.Kind);
            if (profile.Kind == "codebuddy-cli") return new CodeBuddyCliProvider(profile);
            string? secret = null;
            if (configured.ProtectedApiKey is not null)
            {
                try { secret = WindowsSecretProtection.Unprotect(configured.ProtectedApiKey); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FormatException)
                { throw new InvalidOperationException("此 Windows 用户无法解密保存的 API 密钥，请重新输入并保存。"); }
            }
            return new OpenAiChatProvider(profile, secret);
        }
        finally { mutex.Release(); }
    }
    private async Task<StoredSettings> Read(CancellationToken cancellation)
    {
        if (!File.Exists(path)) return Defaults();
        var stored = JsonSerializer.Deserialize<StoredSettings>(await File.ReadAllTextAsync(path, cancellation), Envelope.Json) ??
            throw new InvalidOperationException("Agent 设置无效。");
        if (stored.Version != 1 || stored.Profiles.Count != 4) throw new InvalidOperationException("不支持此 Agent 设置版本。");
        return stored;
    }
    private static AgentProviderProfile Validate(AgentProviderProfile profile, string expectedKind)
    {
        if (profile.Kind != expectedKind || profile.Kind is not ("openai-chat" or "codebuddy-cli"))
            throw new InvalidOperationException("不能修改预设的引擎类型。");
        if (profile.TimeoutSeconds is < 5 or > 180) throw new InvalidOperationException("响应超时应为 5–180 秒。");
        if (profile.Model.Length > 200 || profile.BaseUrl.Length > 2048 || profile.CliPath.Length > 4096 || profile.CliHostPath.Length > 4096)
            throw new InvalidOperationException("Agent 设置字段过长。");
        if (profile.Kind == "openai-chat")
        {
            if (profile.BaseUrl.Length > 0) _ = OpenAiChatProvider.CompletionEndpoint(profile.BaseUrl);
        }
        return profile with
        {
            Model = profile.Model.Trim(),
            BaseUrl = profile.BaseUrl.Trim(),
            CliPath = profile.CliPath.Trim(),
            CliHostPath = profile.CliHostPath.Trim()
        };
    }
    private static AgentSettings Public(StoredSettings settings) => new(settings.SelectedProviderId,
        settings.Profiles.Select(p => p.Profile with { HasApiKey = !string.IsNullOrEmpty(p.ProtectedApiKey) }).ToList(), settings.ContextCharacterBudget);
    private static StoredSettings Defaults()
    {
        var (cli, host) = DetectCodeBuddy();
        return new(1, "deepseek", 12000,
        [
            new(new("deepseek", "DeepSeek", "openai-chat", "https://api.deepseek.com", "deepseek-flash", "", "", 60, false), null),
            new(new("xiaomi-mimo", "Xiaomi MiMo", "openai-chat", "https://api.xiaomimimo.com/v1", "mimo-v2.6-pro", "", "", 60, false), null),
            new(new("openai-compatible", "自定义 OpenAI 兼容 API", "openai-chat", "", "", "", "", 60, false), null),
            new(new("codebuddy-cli", "CodeBuddy CLI", "codebuddy-cli", "", "", cli, host, 120, false), null)
        ]);
    }
    private static (string Cli, string Host) DetectCodeBuddy()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string? native = paths.Select(p => Path.Combine(p.Trim('"'), "codebuddy.exe")).FirstOrDefault(File.Exists);
        if (native is not null) return (native, "");
        string entry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@tencent-ai", "codebuddy-code", "bin", "codebuddy");
        string? node = paths.Select(p => Path.Combine(p.Trim('"'), "node.exe")).FirstOrDefault(File.Exists);
        return File.Exists(entry) && node is not null ? (entry, node) : ("", "");
    }
    private record StoredProfile(AgentProviderProfile Profile, string? ProtectedApiKey);
    private record StoredSettings(int Version, string SelectedProviderId, int ContextCharacterBudget, List<StoredProfile> Profiles);
}
