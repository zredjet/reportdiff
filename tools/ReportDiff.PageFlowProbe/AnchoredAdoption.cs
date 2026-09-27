using System.Reflection;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;

/// <summary>既存の支持検査をそのまま呼ぶ診断。Cの偽のRowComparisonSurfaceは作らない。</summary>
internal static class AnchoredAdoption
{
    internal sealed record Result(bool Accepted, string Reason, JsonElement Validation, JsonElement? Score,
        RowLine[] LinesA, RowLine[] LinesB, RowMatch[] Matches, int BaselinePixels, int CandidatePixels, double? Improvement);
    internal static Result Evaluate(Mat a, Mat b, PageMap display, PageFlowPageDescriptor da, PageFlowPageDescriptor db,
        PageComparison candidate, ComparisonParameters parameters, RowOptions options)
    {
        var segments = new List<PageSegment>();
        foreach (var s in display.Segments)
        {
            var previous = segments.LastOrDefault();
            if (previous is not null && s.AStart is not null && s.BStart is not null && previous.AStart is not null && previous.BStart is not null
                && previous.AStart + previous.Length == s.AStart && previous.BStart + previous.Length == s.BStart)
                segments[^1] = previous with { Length = previous.Length + s.Length };
            else segments.Add(s);
        }
        var map = new PageMap(a.Size(), b.Size(), display.CanvasSize, segments);
        var aa = Lines(da); var bb = Lines(db); var matches = new List<RowMatch>();
        for (var i = 0; i < aa.Length; i++)
        {
            var la = aa[i]; var band = segments.FirstOrDefault(s => s.AStart is int start && s.BStart is not null
                && la.Bounds.Top >= start && la.Bounds.Bottom <= start + s.Length);
            if (band is null) continue;
            var choices = Enumerable.Range(0, bb.Length).Where(j => aa[i].Words[0].Text == bb[j].Words[0].Text
                && bb[j].Bounds.Top >= band.BStart && bb[j].Bounds.Bottom <= band.BStart + band.Length
                && Math.Abs(la.Baseline - bb[j].Baseline - band.Dy!.Value) <= .5).ToArray();
            if (choices.Length == 1) matches.Add(new(i, choices[0]));
        }
        var asm = typeof(RowOptions).Assembly;
        var validated = asm.GetType("ReportDiff.Core.RowGroupValidator")!.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [a, b, aa, bb, matches, parameters, options])!;
        var json = JsonSerializer.SerializeToElement(validated, AggregationProbe.Json);
        var groups = validated.GetType().GetProperty("Groups")!.GetValue(validated);
        using var baseline = PageComparer.Compare(a, b, parameters);
        double? improvement = baseline.RawPixels == 0 ? null : (baseline.RawPixels - candidate.RawPixels) / (double)baseline.RawPixels;
        if (groups is null) return Finish(false, json.GetProperty("reason").GetString()!, null);
        foreach (var group in json.GetProperty("groups").EnumerateArray())
        foreach (var match in group.GetProperty("matches").EnumerateArray())
            if ((int)Math.Round(aa[match.GetProperty("a").GetInt32()].Baseline - bb[match.GetProperty("b").GetInt32()].Baseline) != group.GetProperty("dy").GetInt32())
                return Finish(false, "refined_mapping_changed", null);
        var layoutType = asm.GetType("ReportDiff.Core.RowLayoutCandidate")!;
        var layout = Activator.CreateInstance(layoutType, [map, Array.Empty<RowBandKind>(), groups, null])!;
        var type = asm.GetType("ReportDiff.Core.CanonicalRowCandidate")!;
        var canonical = Activator.CreateInstance(type, [layout, Array.Empty<RowEquivalentPosition>()])!;
        var array = Array.CreateInstance(type, 1); array.SetValue(canonical, 0);
        var eligible = new bool[aa.Length * bb.Length]; foreach (var m in matches) eligible[m.A * bb.Length + m.B] = true;
        var matching = Activator.CreateInstance(asm.GetType("ReportDiff.Core.RowMatchingResult")!,
            [new IReadOnlyList<RowMatch>[] { matches }, null, eligible, bb.Length])!;
        var scores = asm.GetType("ReportDiff.Core.RowSupport")!.GetMethod("Score", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [a, b, aa, bb, array, parameters, options, matching]);
        var score = scores is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(scores, AggregationProbe.Json);
        return Finish(scores is not null && improvement >= options.MinImprovement,
            scores is null ? "common_support" : improvement >= options.MinImprovement ? "applied" : "low_improvement", score);
        Result Finish(bool accepted, string reason, JsonElement? score) => new(accepted, reason, json, score, aa, bb,
            matches.ToArray(), baseline.RawPixels, candidate.RawPixels, improvement);
        static RowLine[] Lines(PageFlowPageDescriptor p) => p.Lines.Select(l => new RowLine([new(l.Text, l.Bounds, [l.Baseline])], l.Bounds, l.Baseline)).ToArray();
    }
}
