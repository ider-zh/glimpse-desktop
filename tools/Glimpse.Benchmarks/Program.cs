using System.Diagnostics;
using System.Text.Json;
using Glimpse.Contracts;
using Glimpse.Core;

// Entirely synthetic, labeled observations. No screenshots or user data.
var article = Enumerable.Range(0, 70).Select(i => new Line($"p{i}", $"Paragraph {i:D3}: labeled synthetic article text for session alignment.")).ToArray();
var repeatedA = new[] { new Line("a", "First author discussing the initial subject"), new Line("r1", "支持"), new Line("b", "First closing context") };
var repeatedB = new[] { new Line("c", "Second author discussing another subject"), new Line("r2", "支持"), new Line("d", "Second closing context") };
var cases = new[]
{
    new Scenario("downward-20-50pct", Enumerable.Range(0,20).Select(i => article[(i*3)..(i*3+6)]).ToArray()),
    new Scenario("upward-20-50pct", Enumerable.Range(0,20).Reverse().Select(i => article[(i*3)..(i*3+6)]).ToArray()),
    new Scenario("identical-10", Enumerable.Range(0,10).Select(_ => article[..6]).ToArray()),
    new Scenario("evicted-return", Enumerable.Range(0,20).Select(i => article[(i*3)..(i*3+6)]).Append(article[..6]).ToArray()),
    new Scenario("distinct-short-replies", [repeatedA, repeatedB]),
    new Scenario("ocr-typo", [article[..6], article[..6].Select((line,i) => i == 3 ? line with { Text = line.Text.Replace("labeled", "labelad") } : line).ToArray()]),
    new Scenario("page-swap", [article[..6], article[40..46]]),
    new Scenario("large-jump", [article[..6], article[3..9], article[40..46]]),
    new Scenario("partial-overlap", [article[..6], article[5..11], article[8..14]])
};
var reports = new List<object>();
bool passed = true;
foreach (var fixture in cases)
{
    var session = new SessionData(Guid.NewGuid(), fixture.Name, "paused", 0, 0, new(0, 0, 800, 600), 0, [], [], []);
    var aligner = new SessionReconciler();
    var assignments = new Dictionary<Guid, string>();
    var firstBlockForTruth = new Dictionary<string, Guid>();
    var expectedOrder = new List<string>();
    int duplicate = 0, merges = 0;
    var elapsed = new List<double>();
    foreach (var frame in fixture.Frames)
    {
        foreach (var line in frame) if (!expectedOrder.Contains(line.Id)) expectedOrder.Add(line.Id);
        var watch = Stopwatch.StartNew();
        var result = aligner.Apply(session, new(Guid.NewGuid().ToString(), "Fake OCR", 1, 0,
            frame.Select((l, i) => new OcrBlock(l.Text, new(0, i * 30, 700, 24))).ToList(), true, "fixture-app"));
        elapsed.Add(watch.Elapsed.TotalMilliseconds);
        session = result.Session;
        if (!result.Effective) continue;
        var snapshot = session.Snapshots[^1];
        for (int i = 0; i < frame.Length; i++)
        {
            Guid predicted = snapshot.BlockIds[i];
            string truth = frame[i].Id;
            if (assignments.TryGetValue(predicted, out var prior) && prior != truth) merges++;
            else assignments[predicted] = truth;
            if (firstBlockForTruth.TryGetValue(truth, out Guid first) && first != predicted) duplicate++;
            else firstBlockForTruth[truth] = predicted;
        }
    }
    var observedOrder = session.Blocks.Select(b => assignments[b.Id]).ToArray();
    var canonical = fixture.Name.StartsWith("upward") ? expectedOrder.OrderBy(id => int.Parse(id[1..])).ToArray() : expectedOrder.ToArray();
    int correctPairs = 0, pairs = 0;
    for (int i = 0; i < observedOrder.Length; i++)
        for (int j = i + 1; j < observedOrder.Length; j++)
        { pairs++; if (Array.IndexOf(canonical, observedOrder[i]) < Array.IndexOf(canonical, observedOrder[j])) correctPairs++; }
    double precision = session.Blocks.Count == 0 ? 1 : (session.Blocks.Count - duplicate - merges) / (double)session.Blocks.Count;
    double recall = firstBlockForTruth.Count / (double)expectedOrder.Count;
    double order = pairs == 0 ? 1 : correctPairs / (double)pairs;
    // Thresholds fixed for this synthetic baseline: no destructive merges/duplicates,
    // every labeled block recovered, exact pairwise reading order.
    bool ok = duplicate == 0 && merges == 0 && precision == 1 && recall == 1 && order == 1;
    passed &= ok;
    reports.Add(new
    {
        fixture.Name,
        frames = fixture.Frames.Length,
        uniqueBlocks = session.Blocks.Count,
        snapshots = session.Snapshots.Count,
        precision,
        recall,
        duplicate,
        erroneousMerges = merges,
        orderAccuracy = order,
        p95AlignmentMs = elapsed.Order().ElementAt((int)Math.Ceiling(elapsed.Count * .95) - 1),
        passed = ok
    });
}
Console.WriteLine(JsonSerializer.Serialize(new { corpus = "synthetic-v1", thresholds = "precision=recall=order=1; duplicate=erroneousMerges=0", passed, reports }, new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;

record Line(string Id, string Text);
record Scenario(string Name, Line[][] Frames);
