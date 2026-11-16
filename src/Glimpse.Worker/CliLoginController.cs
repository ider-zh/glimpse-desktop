using Glimpse.Contracts;
using Glimpse.Providers;

namespace Glimpse.Worker;

// Called only from the serialized IPC command loop. No terminal output is persisted.
internal sealed class CliLoginController(ProviderSettingsStore settings) : IAsyncDisposable
{
    private CodeBuddyLoginSession? session;
    public bool HasSession => session is not null;

    public async Task<CliLoginSnapshot> StartAsync(CancellationToken cancellation)
    {
        if (session is not null) throw new InvalidOperationException("登录终端已打开，请先结束当前登录会话。");
        var configured = await settings.LoadPublicAsync(cancellation);
        var profile = configured.Providers.Single(p => p.Id == configured.SelectedProviderId);
        if (profile.Kind != "codebuddy-cli") throw new InvalidOperationException("请先保存并切换到 CodeBuddy CLI。");
        cancellation.ThrowIfCancellationRequested();
        session = CodeBuddyLoginSession.Start(profile);
        return session.Read();
    }
    private CodeBuddyLoginSession Require(Guid id) => session is { } current && current.Id == id ? current :
        throw new InvalidOperationException("登录会话已结束，请重新打开登录终端。");
    public CliLoginSnapshot Read(Guid id) => Require(id).Read();
    public void Resize(CliLoginResize size) => Require(size.LoginId).Resize(size.Columns, size.Rows);
    public Task SendAsync(CliLoginInput input, CancellationToken cancellation) => Require(input.LoginId).SendAsync(input.Text, cancellation);
    public async Task StopAsync(Guid id)
    {
        var current = Require(id);
        session = null;
        await current.DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        var current = session; session = null;
        if (current is not null) await current.DisposeAsync();
    }
}
