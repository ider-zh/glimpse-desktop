using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Glimpse.Contracts;
using Glimpse.Core;

namespace Glimpse.UI;

public partial class DockedToolbarWindow
{
    private PostCatalog? postCatalog;
    private Guid? selectedPostId;
    private bool allPosts;
    private bool userSelectedPost;
    private string catalogMaskKey = "";

    public IReadOnlyList<Guid> SelectedPostIds => postCatalog is null ? [] : allPosts ?
        postCatalog.Posts.Select(p => p.Id).ToList() : selectedPostId is Guid id ? [id] : [];

    public void SetPostCatalog(SessionData? session)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetPostCatalog(session)); return; }
        string maskKey = string.Join(";", session?.IgnoreRegions?.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}") ?? []);
        if (session is not null && postCatalog?.SessionId == session.Id && postCatalog.DocumentRevision == session.Revision && catalogMaskKey == maskKey) return;
        catalogMaskKey = maskKey;
        var previous = postCatalog;
        postCatalog = session is null ? null : new PostSegmenter().Build(session);
        if (previous?.SessionId != session?.Id) { selectedPostId = null; allPosts = false; userSelectedPost = false; }
        var posts = postCatalog?.Posts ?? [];
        if (!posts.Any(p => p.Id == selectedPostId)) userSelectedPost = false;
        if (!userSelectedPost)
            selectedPostId = posts.Where(p => p.IsVisible).OrderBy(p => DistanceFromCenter(p, session!.Rect.Height))
                .FirstOrDefault()?.Id ?? posts.LastOrDefault()?.Id;
        if (posts.Count is < 2 or > 4) allPosts = false;
        PostButtons.Children.Clear();
        foreach (var post in posts)
        {
            string label = post.Label.Length > 22 ? post.Label[..22] + "…" : post.Label;
            var button = new RadioButton
            {
                Content = label, ToolTip = post.Label + (post.IsVisible ? "\n当前画面可见" : "\n已观察的历史内容，当前画面不可见") +
                    (post.Uncertain ? "\n边界由 OCR 推断，可能不完整。" : ""),
                GroupName = "ObservedPost", Tag = post.Id, IsChecked = post.Id == selectedPostId,
                Margin = new Thickness(0, 0, 7, 7), Padding = new Thickness(4), MaxWidth = 285
            };
            AutomationProperties.SetName(button, "选择帖子：" + post.Label);
            button.Checked += SelectPost;
            PostButtons.Children.Add(button);
        }
        AllPostsToggle.IsChecked = allPosts;
        PostScopeNotice.Text = posts.Count == 0 ? "尚未识别到正文，请继续观察或调整区域。" :
            $"识别 {posts.Count} 条内容 · 排除 {postCatalog!.ExcludedBlockIds.Count} 块控件或忽略文字 · 未归属 {postCatalog.UnassignedBlockIds.Count} 块。" +
            (posts.Any(p => p.Uncertain) ? " 边界为推断，请核对。" : "");
        UpdatePostControls();
    }

    private void SelectPost(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: Guid id }) { selectedPostId = id; userSelectedPost = true; }
    }
    private void AllPostsChanged(object sender, RoutedEventArgs e)
    {
        allPosts = AllPostsToggle.IsChecked == true;
        UpdatePostControls();
    }
    private void UpdatePostControls()
    {
        if (PostButtons is null) return;
        PostButtons.IsEnabled = !generating && !allPosts;
        AllPostsToggle.IsEnabled = !generating && postCatalog?.Posts.Count is >= 2 and <= 4;
        AllPostsToggle.ToolTip = postCatalog?.Posts.Count > 4 ? "批量处理最多 4 条，请缩小观察范围或逐条选择。" :
            "本次点击任务将分别调用每条帖子，最多并行两路；共享总上下文预算。";
    }
    private static double DistanceFromCenter(ObservedPost post, int height)
    {
        int latest = post.Evidence.Max(e => e.Sequence);
        var evidence = post.Evidence.Where(e => e.Sequence == latest).ToList();
        double top = evidence.Min(e => e.Bounds.Y), bottom = evidence.Max(e => e.Bounds.Y + e.Bounds.Height);
        return Math.Abs((top + bottom) / 2 - height / 2d);
    }
}
