namespace Glimpse.Agent;

public enum ProviderFailureKind
{
    CliAuthenticationRequired,
    CliExit,
    CliInvalidResponse,
    CliEmptyResponse,
    CliTurnLimit,
    OutputLimit
}

// Only fixed, locally-authored messages cross into the result UI. Raw provider
// stderr/response bodies and arbitrary exception messages remain private.
public sealed class ProviderFailureException(ProviderFailureKind kind, int? exitCode = null)
    : InvalidOperationException(MessageFor(kind, exitCode))
{
    public ProviderFailureKind Kind { get; } = kind;
    private static string MessageFor(ProviderFailureKind kind, int? code) => kind switch
    {
        ProviderFailureKind.CliAuthenticationRequired => "CodeBuddy CLI 认证失效。请在终端运行 codebuddy 并通过 /login 重新登录，然后返回应用重新预览、生成。",
        ProviderFailureKind.CliExit => $"CodeBuddy CLI 未完成调用（退出代码 {code}）。请在终端检查安装、账号及模型状态。",
        ProviderFailureKind.CliInvalidResponse => "CodeBuddy CLI 未返回有效的结果格式，请检查 CLI 版本及模型状态。",
        ProviderFailureKind.CliEmptyResponse => "CodeBuddy CLI 未返回回答。请在终端检查登录和模型状态后重试。",
        ProviderFailureKind.CliTurnLimit => "CodeBuddy CLI 达到调用轮数上限，未完成回答。请缩短任务后重试。",
        _ => "引擎输出超过大小上限，调用已停止。请缩短任务后重试。"
    };
}
