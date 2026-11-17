using System.Text.RegularExpressions;
using Glimpse.Contracts;

namespace Glimpse.Core;

/// <summary>Conservative local layout partitioning. No model calls or shared model history.</summary>
public sealed partial class PostSegmenter
{
    [GeneratedRegex(@"^(?:(?:点赞|喜欢|评论|回复|转发|分享|收藏|赞|Like|Reply|Comment|Share|Repost|Bookmark)[\s\d.,万千kKmM]*[·|•]?\s*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex ActionStrip();
    [GeneratedRegex(@"点赞|喜欢|评论|回复|转发|分享|收藏|赞|Like|Reply|Comment|Share|Repost|Bookmark", RegexOptions.IgnoreCase)]
    private static partial Regex Actions();
    [GeneratedRegex(@"^(?:@[^\s]{1,40}(?:\s.*)?|.{1,35}\s[·•]\s(?:\d+\s*(?:分钟|小时|天|周|mo|h|d|m)|刚刚|昨天).*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Author();
    private static readonly HashSet<string> Navigation = new(StringComparer.OrdinalIgnoreCase)
    { "首页", "发现", "推荐", "消息", "通知", "搜索", "设置", "Home", "Explore", "Notifications", "Messages", "Search", "Settings" };

    public PostCatalog Build(SessionData session)
    {
        var annotated = Annotate(session);
        var visible = session.Snapshots.LastOrDefault()?.BlockIds.ToHashSet() ?? [];
        var posts = annotated.Blocks.Where(b => b.Source.Geometry?.PostId is not null && !Excluded(b.Source.Geometry.Role))
            .GroupBy(b => b.Source.Geometry!.PostId!.Value)
            .Select(group =>
            {
                var blocks = group.ToList();
                string label = blocks.FirstOrDefault(b => b.Source.Geometry?.Role == "author")?.Text ?? blocks[0].Text;
                label = SessionReconciler.Normalize(label);
                if (label.Length > 45) label = label[..45] + "…";
                return new ObservedPost(group.Key, label, blocks.Select(b => b.Id).ToList(),
                    blocks.Select(b => new PostEvidence(b.Id, b.Source.Geometry!.Bounds,
                        b.Source.Geometry.Sequence, b.Source.Geometry.Role ?? "body")).ToList(),
                    blocks.Any(b => visible.Contains(b.Id)),
                    blocks.All(b => b.Source.Geometry?.Role != "author") ||
                    blocks.Any(b => b.Status != "confirmed" || b.Source.Geometry?.Role == "unclassified"));
            }).ToList();
        var unassigned = annotated.Blocks.Where(b => b.Source.Geometry?.PostId is null && !Excluded(b.Source.Geometry?.Role)).Select(b => b.Id).ToList();
        var excluded = annotated.Blocks.Where(b => Excluded(b.Source.Geometry?.Role)).Select(b => b.Id).ToList();
        List<string> warnings = [];
        if (unassigned.Count > 0) warnings.Add("Some observed text has no reliable post boundary and is excluded from post generation.");
        if (posts.Any(p => p.Uncertain)) warnings.Add("Post boundaries or OCR may be uncertain; check the selected source before using a result.");
        if (session.Blocks.Any(b => b.Source.Geometry is null)) warnings.Add("Older observations lack durable layout evidence; their content is conservatively grouped and may contain interface text.");
        if (annotated.Blocks.Any(b => b.Source.Geometry?.Role == "masked"))
            warnings.Add("Previously observed text covered by an ignored region is retained in raw records but excluded from generation until actually observed again.");
        if (session.IgnoreRegions is { Count: > 0 } && session.Snapshots.Count == 0 && session.Blocks.Count > 0)
            warnings.Add("No recent visible layout exists to reconcile old text with ignored regions; historical positions are not assumed to match the current screen.");
        return new(session.Id, session.Revision, posts, unassigned, excluded, warnings);
    }

    /// <summary>Exclude only currently visible evidence intersecting a new mask.
    /// Raw OCR and durable text remain intact; clearing a mask alone never restores
    /// potentially stale text. A subsequent actual OCR observation can restore it.</summary>
    public SessionData ExcludeVisibleRegions(SessionData session, IReadOnlyList<PixelRect> regions)
    {
        if (regions.Count == 0 || session.Snapshots.LastOrDefault() is not { } visible) return session;
        var annotated = Annotate(session);
        var covered = Enumerable.Range(0, Math.Min(visible.BlockIds.Count, visible.Observation.Blocks.Count))
            .Where(i => regions.Any(region => Intersects(visible.Observation.Blocks[i].Bounds, region)))
            .ToDictionary(i => visible.BlockIds[i], i => visible.Observation.Blocks[i].Bounds);
        return annotated with
        {
            Blocks = annotated.Blocks.Select(block => !covered.TryGetValue(block.Id, out var bounds) ? block :
                block with { Source = block.Source with
                { Geometry = new(visible.Sequence, bounds, visible.Observation.ContextId, null, "masked") } }).ToList()
        };
    }

    public SessionData Scope(SessionData session, Guid postId)
    {
        var catalog = Build(session);
        var post = catalog.Posts.SingleOrDefault(p => p.Id == postId)
            ?? throw new InvalidOperationException("The selected post is no longer available. Select a current post and retry.");
        var allowed = post.BlockIds.ToHashSet();
        return session with
        {
            Blocks = session.Blocks.Where(b => allowed.Contains(b.Id)).ToList(),
            Snapshots = session.Snapshots.Select(snapshot =>
            {
                var indices = Enumerable.Range(0, Math.Min(snapshot.BlockIds.Count, snapshot.Observation.Blocks.Count))
                    .Where(i => allowed.Contains(snapshot.BlockIds[i])).ToList();
                return snapshot with
                {
                    BlockIds = indices.Select(i => snapshot.BlockIds[i]).ToList(),
                    Observation = snapshot.Observation with { Blocks = indices.Select(i => snapshot.Observation.Blocks[i]).ToList() }
                };
            }).Where(s => s.BlockIds.Count > 0).ToList()
        };
    }

    /// <summary>Returns a new session; the caller persists the annotated provenance atomically.</summary>
    public SessionData Annotate(SessionData session)
    {
        var blocks = session.Blocks.ToDictionary(b => b.Id);
        foreach (var snapshot in session.Snapshots.OrderBy(s => s.Sequence))
        {
            var items = Enumerable.Range(0, Math.Min(snapshot.BlockIds.Count, snapshot.Observation.Blocks.Count))
                .Where(i => blocks.ContainsKey(snapshot.BlockIds[i]))
                .Where(i => blocks[snapshot.BlockIds[i]].Source.Geometry is not { Role: "masked" } geometry ||
                    geometry.Sequence < snapshot.Sequence)
                .Select(i => new Item(snapshot.BlockIds[i], snapshot.Observation.Blocks[i])).OrderBy(i => i.Ocr.Bounds.Y).ThenBy(i => i.Ocr.Bounds.X).ToList();
            if (items.Count == 0) continue;
            double height = Math.Max(12, items.Select(i => i.Ocr.Bounds.Height).Order().ElementAt(items.Count / 2));
            double? divider = ColumnDivider(items, height);
            var crossing = divider is double split
                ? items.Where(i => i.Ocr.Bounds.X < split && i.Ocr.Bounds.X + i.Ocr.Bounds.Width > split).ToList() : [];
            foreach (var item in crossing)
            {
                var block = blocks[item.Id];
                if (block.Source.Geometry is not { } geometry || geometry.Sequence <= snapshot.Sequence)
                    blocks[item.Id] = block with { Source = block.Source with
                    { Geometry = new(snapshot.Sequence, item.Ocr.Bounds, snapshot.Observation.ContextId, null, "unassigned") } };
            }
            var remaining = items.Except(crossing).ToList();
            List<List<Item>> columns = divider is double x
                ? [remaining.Where(i => i.Ocr.Bounds.X < x).ToList(), remaining.Where(i => i.Ocr.Bounds.X >= x).ToList()]
                : [remaining];
            foreach (var column in columns.Where(c => c.Count > 0))
            {
            var rows = new List<List<Item>>();
            foreach (var item in column)
            {
                var row = rows.LastOrDefault();
                if (row is null || Math.Abs(row[0].Ocr.Bounds.Y - item.Ocr.Bounds.Y) > height * .65) rows.Add([item]);
                else row.Add(item);
            }
            // A vertical edge navigation column needs multiple distinct labels. A lone
            // sentence such as "回复" must never disappear merely for matching a word.
            var nav = column.Where(i => Navigation.Contains(i.Ocr.Text.Trim()) &&
                (i.Ocr.Bounds.X < session.Rect.Width * .12 || i.Ocr.Bounds.X > session.Rect.Width * .82)).ToList();
            var navIds = nav.Select(i => i.Ocr.Text.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2
                ? nav.Select(i => i.Id).ToHashSet() : [];
            List<Item> pending = [];
            var commentAuthors = new HashSet<Guid>();
            double? bottom = null;
            void Set(Item item, Guid? post, string role)
            {
                var block = blocks[item.Id];
                if (block.Source.Geometry is { } previous && previous.Sequence > snapshot.Sequence) return;
                blocks[item.Id] = block with { Source = block.Source with
                { Geometry = new(snapshot.Sequence, item.Ocr.Bounds, snapshot.Observation.ContextId, post, role) } };
            }
            void Flush()
            {
                if (pending.Count == 0) return;
                // Reused blocks provide identity across scrolls. Conflicting known
                // identities are split rather than silently merging two posts.
                var known = pending.Select(i => blocks[i.Id].Source.Geometry?.PostId).Where(id => id is not null).Distinct().ToList();
                if (known.Count > 1)
                {
                    foreach (var item in pending)
                    {
                        var old = blocks[item.Id].Source.Geometry;
                        Set(item, old?.PostId, old?.PostId is null ? "unassigned" : old.Role ?? "body");
                    }
                }
                else
                {
                    bool substantial = pending.Sum(i => i.Ocr.Text.Trim().Length) >= 8 || pending.Count >= 2;
                    Guid? id = known.FirstOrDefault() ?? (substantial ? pending[0].Id : null);
                    foreach (var item in pending) Set(item, id, id is null ? "unassigned" : commentAuthors.Contains(item.Id) ? "comment" : Author().IsMatch(item.Ocr.Text.Trim()) ? "author" : "body");
                }
                pending.Clear(); bottom = null;
            }
            foreach (var row in rows)
            {
                var content = row.Where(i => !navIds.Contains(i.Id)).OrderBy(i => i.Ocr.Bounds.X).ToList();
                foreach (var item in row.Where(i => navIds.Contains(i.Id))) Set(item, null, "control");
                if (content.Count == 0) continue;
                string rowText = string.Join(" ", content.Select(i => i.Ocr.Text.Trim()));
                bool controls = ActionStrip().IsMatch(rowText) && Actions().Matches(rowText)
                    .Select(m => m.Value.ToLowerInvariant()).Distinct().Count() >= 2;
                if (controls)
                {
                    Flush();
                    foreach (var item in content) Set(item, null, "control");
                    continue;
                }
                double top = content.Min(i => i.Ocr.Bounds.Y);
                bool author = Author().IsMatch(content[0].Ocr.Text.Trim());
                bool indentedComment = author && pending.Count > 0 &&
                    content[0].Ocr.Bounds.X - pending.Min(i => i.Ocr.Bounds.X) >= Math.Max(36, height * 1.5);
                if (indentedComment) commentAuthors.Add(content[0].Id);
                var incomingIdentity = content.Select(i => blocks[i.Id].Source.Geometry?.PostId).FirstOrDefault(id => id is not null);
                var currentIdentity = pending.Select(i => blocks[i.Id].Source.Geometry?.PostId).FirstOrDefault(id => id is not null);
                if (pending.Count > 0 && (author && !indentedComment || bottom is not null && top - bottom > Math.Max(56, height * 3) ||
                    incomingIdentity is not null && currentIdentity is not null && incomingIdentity != currentIdentity)) Flush();
                pending.AddRange(content);
                bottom = content.Max(i => i.Ocr.Bounds.Y + i.Ocr.Bounds.Height);
            }
            Flush();
            }
        }
        // Backward-compatible sessions without surviving coordinates remain usable,
        // but are explicitly uncertain rather than pretending to know post boundaries.
        var legacy = session.Blocks.Where(b => blocks[b.Id].Source.Geometry is null).ToList();
        if (legacy.Count > 0)
            foreach (var block in legacy)
                blocks[block.Id] = block with { Source = block.Source with
                { Geometry = new(block.Source.FirstSequence, new(0, 0, 0, 0), null, legacy[0].Id, "unclassified") } };
        return session with { Blocks = session.Blocks.Select(b => blocks[b.Id]).ToList() };
    }

    private static bool Excluded(string? role) => role is "control" or "masked";
    private static bool Intersects(TextBox bounds, PixelRect region) =>
        bounds.Width > 0 && bounds.Height > 0 && bounds.X < region.X + region.Width &&
        bounds.Y < region.Y + region.Height && bounds.X + bounds.Width > region.X && bounds.Y + bounds.Height > region.Y;

    private static double? ColumnDivider(List<Item> items, double height)
    {
        // Require co-visible substantive text on both sides of a real gutter.
        // This avoids treating an author plus a small Follow button as two posts.
        var candidates = new List<(double Divider, int Support)>();
        foreach (var left in items)
        foreach (var right in items)
        {
            double gap = right.Ocr.Bounds.X - left.Ocr.Bounds.X - left.Ocr.Bounds.Width;
            if (gap < Math.Max(32, height * 1.5) || Math.Abs(left.Ocr.Bounds.Y - right.Ocr.Bounds.Y) > height * .65 ||
                left.Ocr.Text.Trim().Length < 8 || right.Ocr.Text.Trim().Length < 8) continue;
            double divider = left.Ocr.Bounds.X + left.Ocr.Bounds.Width + gap / 2;
            int support = items.Count(i => i.Ocr.Bounds.X + i.Ocr.Bounds.Width <= divider &&
                items.Any(j => j.Ocr.Bounds.X >= divider && Math.Abs(i.Ocr.Bounds.Y - j.Ocr.Bounds.Y) <= height * .65));
            candidates.Add((divider, support));
        }
        return candidates.OrderByDescending(c => c.Support).Select(c => (double?)c.Divider).FirstOrDefault();
    }

    private sealed record Item(Guid Id, OcrBlock Ocr);
}
