using System.Text;
using System.Text.RegularExpressions;
using Glimpse.Contracts;

namespace Glimpse.Core;

public interface ISequenceAligner
{
    ReconcileResult Apply(SessionData session, Observation observation);
}
public record ReconcileResult(SessionData Session, bool Effective, int NewBlocks, int ReusedBlocks);

public sealed partial class SessionReconciler : ISequenceAligner
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
    public static string Normalize(string value) => Spaces().Replace(value.Normalize(NormalizationForm.FormKC), " ").Trim();

    public ReconcileResult Apply(SessionData session, Observation observation)
    {
        var incoming = observation.Blocks.Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList();
        var normalized = incoming.Select(b => Normalize(b.Text)).ToArray();
        var last = session.Snapshots.LastOrDefault();
        if (last is not null && last.Observation.Blocks.Select(b => Normalize(b.Text)).SequenceEqual(normalized))
            return new(session, false, 0, normalized.Length);
        if (incoming.Count == 0) return new(session, false, 0, 0);

        // Align against recent ordered observations, not a global set of text hashes.
        // Exact long lines are anchors; short identical replies need a second neighbor.
        var candidates = session.Snapshots.AsEnumerable().Reverse().ToList();
        if (session.Blocks.Count > 0)
            candidates.Add(new(Guid.Empty, 0, observation with
            {
                Blocks = session.Blocks.Select(b =>
                new OcrBlock(b.Text, new(0, 0, 0, 0))).ToList()
            }, session.Blocks.Select(b => b.Id).ToList(), 0, 0, 0));
        Dictionary<int, Guid> matches = [];
        int bestScore = 0;
        foreach (var candidate in candidates)
        {
            var old = candidate.Observation.Blocks.Select(b => Normalize(b.Text)).ToArray();
            var pairs = Align(old, normalized);
            var adjacent = pairs.ToHashSet();
            pairs = pairs.Where(p => normalized[p.New].Length >= 8 ||
                adjacent.Contains((p.Old - 1, p.New - 1)) || adjacent.Contains((p.Old + 1, p.New + 1))).ToList();
            bool anchored = pairs.Any(p => old[p.Old] == normalized[p.New] && normalized[p.New].Length >= 16) ||
                pairs.Where(p => old[p.Old] == normalized[p.New] && normalized[p.New].Length >= 4)
                    .Select(p => normalized[p.New]).Distinct().Count() >= 2;
            if (!anchored) continue;
            int score = pairs.Sum(p => normalized[p.New].Length);
            if (score <= bestScore) continue;
            bestScore = score;
            matches = pairs.ToDictionary(p => p.New, p => candidate.BlockIds[p.Old]);
        }

        var blocks = session.Blocks.ToList();
        var ids = new List<Guid>();
        int sequence = session.Revision + 1;
        int added = 0, uncertain = 0;
        for (int i = 0; i < incoming.Count; i++)
        {
            if (matches.TryGetValue(i, out Guid existing))
            {
                int index = blocks.FindIndex(b => b.Id == existing);
                if (index < 0) throw new InvalidDataException("Missing durable block.");
                var previous = blocks[index];
                bool variant = previous.Normalized != normalized[i];
                var variants = (previous.Source.Variants ?? []).ToList();
                if (variant && !variants.Contains(incoming[i].Text)) variants.Add(incoming[i].Text);
                blocks[index] = previous with
                {
                    Status = variant ? "uncertain" : previous.Status,
                    Source = previous.Source with
                    {
                        Observations = previous.Source.Observations + 1,
                        LastObservedText = incoming[i].Text,
                        Variants = variants
                    }
                };
                if (variant) uncertain++;
                ids.Add(existing);
                continue;
            }
            bool ambiguous = matches.Count == 0 && session.Blocks.Count > 0 ||
                session.Blocks.Any(b => b.Normalized == normalized[i]);
            var block = new ContentBlock(Guid.NewGuid(), incoming[i].Text, normalized[i],
                ambiguous ? "uncertain" : "confirmed",
                new(observation.CapturedAtMs, sequence, incoming[i].Text, 1, incoming[i].Text));
            // Insert before the next aligned anchor, or after the preceding block. This
            // handles both upward and downward scrolls without moving existing evidence.
            int next = Enumerable.Range(i + 1, incoming.Count - i - 1).FirstOrDefault(matches.ContainsKey, -1);
            int insertion = next >= 0 ? blocks.FindIndex(b => b.Id == matches[next]) :
                ids.Count > 0 ? blocks.FindIndex(b => b.Id == ids[^1]) + 1 : blocks.Count;
            blocks.Insert(insertion, block);
            ids.Add(block.Id);
            added++;
            if (ambiguous) uncertain++;
        }
        // A reordered or changed observation reuses evidence, but exact text repeats above
        // never consume slots. New arrangement is useful recent alignment evidence.
        var snapshots = session.Snapshots.Append(new Snapshot(Guid.NewGuid(), sequence,
            observation with { Blocks = incoming }, ids, added, matches.Count, uncertain)).TakeLast(9).ToList();
        var gaps = session.Gaps.ToList();
        // No overlap alone is not evidence of a missed scroll; require prior overlapping
        // scrolling and continuing motion. Page/context swaps otherwise remain uncertain.
        if (last is not null && matches.Count == 0 && observation.MovementObserved &&
            last.ReusedBlocks >= 2 && last.NewBlocks > 0 && observation.ContextId is not null &&
            observation.ContextId == last.Observation.ContextId)
            gaps.Add(new(Guid.NewGuid(), last.Sequence, sequence,
                "Possible unread content: motion after an overlapping scroll ended without a text anchor."));
        var updated = session with
        {
            Blocks = blocks,
            Snapshots = snapshots,
            Gaps = gaps,
            Revision = sequence,
            UpdatedAtMs = observation.CapturedAtMs
        };
        return new(updated, true, added, matches.Count);
    }

    private static List<(int Old, int New)> Align(string[] old, string[] current)
    {
        var scores = new int[old.Length + 1, current.Length + 1];
        for (int i = old.Length - 1; i >= 0; i--)
            for (int j = current.Length - 1; j >= 0; j--)
                scores[i, j] = Similar(old[i], current[j]) ? scores[i + 1, j + 1] + Math.Max(1, old[i].Length) :
                    Math.Max(scores[i + 1, j], scores[i, j + 1]);
        var pairs = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < old.Length && y < current.Length)
        {
            if (Similar(old[x], current[y])) { pairs.Add((x++, y++)); }
            else if (scores[x + 1, y] >= scores[x, y + 1]) x++;
            else y++;
        }
        return pairs;
    }
    private static bool Similar(string a, string b)
    {
        if (a == b) return true;
        // A single possible OCR typo in a long line may align only alongside exact
        // anchors (checked by caller). Preserve both readings and flag uncertainty.
        if (a.Length < 24 || b.Length < 24 || Math.Abs(a.Length - b.Length) > 1) return false;
        if (!a.Where(char.IsDigit).SequenceEqual(b.Where(char.IsDigit)) ||
            a.Count(c => c is '不' or '无' or '未' or '没') != b.Count(c => c is '不' or '无' or '未' or '没')) return false;
        var longer = a.Length >= b.Length ? a : b;
        var shorter = a.Length >= b.Length ? b : a;
        int i = 0, j = 0, differences = 0;
        while (i < longer.Length && j < shorter.Length)
        {
            if (longer[i] == shorter[j]) { i++; j++; continue; }
            if (++differences > 1) return false;
            i++;
            if (longer.Length == shorter.Length) j++;
        }
        return differences + longer.Length - i <= 1;
    }
}
