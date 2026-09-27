using System.Runtime.Versioning;
using System.Text.Json;
using ReportDiff.Core;

/// <summary>既存の支持条件を変更せず、同一ページ二原因の配置と負例を実PDFで切り分ける。</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal static class SamePageSupportProbe
{
    private sealed record Geometry(string Id, int Capacity, int First, int Second, string Mutation, bool Grouped, bool ExistingUnmet = false);
    internal static void Run(string output)
    {
        Geometry[] definitions = [
            new("same-page-two", 6, 2, 4, "none", true, true),
            new("capacity7", 7, 2, 4, "none", false),
            new("capacity8", 8, 2, 4, "none", true),
            new("capacity9", 9, 2, 4, "none", true),
            new("capacity10", 10, 2, 4, "none", true),
            new("later-short", 8, 2, 5, "none", false),
            new("later-supported", 9, 2, 5, "none", true),
            new("both-later", 9, 3, 5, "none", true),
            new("before-short", 8, 1, 4, "none", false),
            new("between-short", 8, 2, 3, "none", false),
            new("content-tone", 8, 2, 4, "paired", true),
            new("carry-tone", 8, 2, 4, "pixels", false),
            new("repeated", 8, 2, 4, "repeated", false),
            new("reordered", 8, 2, 4, "reordered", false),
            new("faint-support", 8, 2, 4, "faint", false)
        ];
        var cases = definitions.Select(d =>
        {
            var a = Enumerable.Range(0, 2 * d.Capacity - 2).Select(i => $"ROW ITEM {(char)('A' + i)}{(char)('A' + i)}").ToArray();
            if (d.Mutation == "repeated") a[d.Capacity + 1] = a[d.Capacity];
            var b = new List<string>();
            for (var i = 0; i < a.Length; i++)
            {
                if (i == d.First || i == d.Second) b.Add($"ROW ADDED {(char)('A' + i)} A");
                b.Add(a[i]);
            }
            if (d.Mutation == "reordered") (b[^2], b[^3]) = (b[^3], b[^2]);
            var layout = new FlowLayout(Height: 300 + 24 * (d.Capacity - 6), FooterTop: 252 + 24 * (d.Capacity - 6));
            return new SharedCauseProbe.Case(d.Id, a.Chunk(d.Capacity).ToArray(), b.Chunk(d.Capacity).ToArray(),
                d.Mutation, d.Grouped, d.Grouped ? d.Mutation == "paired" ? 3 : 2 : null, layout,
                d.Mutation == "faint" ? new HashSet<string>(a.Skip(d.Second).Take(d.Capacity - d.Second - 2)) : null);
        }).ToArray();
        SharedCauseProbe.RunCases(output, cases, supportDiagnostics: true);
        File.WriteAllText(Path.Combine(output, "geometry.json"), JsonSerializer.Serialize(definitions, AggregationProbe.Json));
    }

    internal static object Describe(string run, PageFlowPlan plan)
    {
        var layouts = plan.Inference.Layouts.A.Concat(plan.Inference.Layouts.B).ToArray();
        var rows = layouts.SelectMany(l => l.Body.Select((r, i) => new SharedInferenceDiagnosis.Line(l.Page.Key, i,
            l.BodyStart + i * l.Pitch, l.Pitch, r.Text, r.Baseline, r.Bounds.Left))).ToArray();
        var inferred = SharedInferenceDiagnosis.Infer(rows, layouts, new() { Enabled = true }, 300);
        var gaps = new List<object>();
        foreach (var l in layouts)
        {
            var other = layouts.Single(x => x.Page.Key.Side != l.Page.Key.Side && x.Page.Key.Page == l.Page.Key.Page);
            var common = l.Body.Select(r => r.Text).Intersect(other.Body.Select(r => r.Text)).ToHashSet();
            for (var i = 0; i < l.Body.Count;)
            {
                if (common.Contains(l.Body[i].Text)) { i++; continue; }
                var start = i; while (i < l.Body.Count && !common.Contains(l.Body[i].Text)) i++;
                gaps.Add(new { page = l.Page.Key, top = l.BodyStart + start * l.Pitch, height = (i - start) * l.Pitch,
                    text = l.Body.Skip(start).Take(i - start).Select(r => r.Text).ToArray(),
                    before = l.Body.Take(start).Count(r => common.Contains(r.Text)), after = l.Body.Skip(i).Count(r => common.Contains(r.Text)) });
            }
        }
        return new { run, rows, inferred, gaps, original_candidates = plan.Inference.Proposals,
            resolved_candidates = plan.Links, verifications = plan.Verifications, range = plan.Decision };
    }
}
