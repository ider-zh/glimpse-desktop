namespace Glimpse.UI;

internal static class AgentWarningText
{
    public static string Display(string warning) => warning switch
    {
        "This isolated task includes only the selected post; other posts and interface controls are excluded." => "本次只发送所选帖子的文字，已排除其他帖子和界面控件。",
        "Post boundaries are inferred from OCR layout and may be incomplete; do not invent relationships." => "帖子边界由 OCR 布局推断，内容可能不完整。",
        "Some observed text has no reliable post boundary and is excluded from post generation." => "部分文字无法可靠归属，未参与生成。",
        "Post boundaries or OCR may be uncertain; check the selected source before using a result." => "帖子边界或识别存在不确定性，请核对来源。",
        "Older observations lack durable layout evidence; their content is conservatively grouped and may contain interface text." => "旧记录缺少位置依据，作为一组不确定内容处理，可能包含界面文字。",
        "Previously observed text covered by an ignored region is retained in raw records but excluded from generation until actually observed again." => "忽略区内已识别的旧文字仍保留在记录中，重新观察前不参与生成。",
        "No recent visible layout exists to reconcile old text with ignored regions; historical positions are not assumed to match the current screen." => "没有近期位置依据，无法判断哪些旧文字对应当前忽略区，请核对发送内容。",
        "Only task-selected portions of the session are included; this is not the full document." => "仅发送与任务相关的部分文字，并非完整文档。",
        "Selected text contains uncertain OCR or ordering; do not present it as verified." => "部分文字的识别或顺序不确定。",
        "Some selected blocks were shortened to fit the context budget." => "部分文字因上下文预算被截短。",
        "The session contains no readable text. State this limitation; do not invent context." => "当前没有可用的正文文字。",
        _ when warning.StartsWith("Session has ", StringComparison.Ordinal) => "存在可能未读到的内容，结果可能不完整。",
        _ when warning.StartsWith("Best-effort privacy filtering", StringComparison.Ordinal) => "已做敏感信息过滤，请核对本次发送内容。",
        _ => warning
    };
}
