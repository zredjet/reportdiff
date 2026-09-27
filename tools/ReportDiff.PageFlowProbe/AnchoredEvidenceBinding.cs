using System.Text.Json;
using ReportDiff.Core;
using static AnchoredContentSurface;

/// <summary>物理的被覆だけでは拒否できない共通行の取り違えを、再読込した本文と原因証拠へ結び付ける。</summary>
internal static class AnchoredEvidenceBinding
{
    internal static bool Matches(AnchoredContentProbe.Input content, PageFlowInference.Layout[] layouts, CrossPageSupportEvidence.Proof proof)
    {
        var source = proof.Candidate.Source!.Page.Side; var target = source == PageSpace.A ? PageSpace.B : PageSpace.A;
        var lookup = layouts.Where(l => l.Page.Key.Side == target).SelectMany(l => l.Body.Select((r, i) =>
            (r.Text, Origin: new Origin(target + l.Page.Key.Page.ToString(), l.BodyStart + i * l.Pitch)))).ToDictionary(p => p.Text, p => p.Origin);
        var sourceText = layouts.Where(l => l.Page.Key.Side == source).SelectMany(l => l.Body).Select(r => r.Text).ToHashSet();
        var expected = new List<Canvas>(); var omitted = new List<Omission>();
        foreach (var page in new[] { 1, 2 })
        {
            var s = layouts.Single(l => l.Page.Key == new PageFlowPageKey(source, page));
            var t = layouts.Single(l => l.Page.Key == new PageFlowPageKey(target, page));
            var pieces = new List<Piece> { new(0, s.BodyStart, new("A"+page,0), new("B"+page,0)) };
            for (var i = 0; i < s.Body.Count; i++)
            {
                var top = s.BodyStart + i * s.Pitch; var one = new Origin(source + page.ToString(), top); var other = lookup[s.Body[i].Text];
                pieces.Add(new(top, s.Pitch, source == PageSpace.A ? one : other, source == PageSpace.B ? one : other));
            }
            var tail = Math.Max(s.BodyEnd, t.BodyEnd);
            if (tail > s.BodyEnd)
                pieces.Add(new(s.BodyEnd, tail - s.BodyEnd, source == PageSpace.A ? new("A"+page,s.BodyEnd) : null,
                    source == PageSpace.B ? new("B"+page,s.BodyEnd) : null));
            pieces.Add(new(tail, s.Page.Size.Height-tail, new("A"+page,tail), new("B"+page,tail)));
            expected.Add(new(page, source + page.ToString(), pieces.ToArray()));
            for (var i = 0; i < t.Body.Count; i++)
                if (!sourceText.Contains(t.Body[i].Text)) omitted.Add(new(target + page.ToString(), t.BodyStart + i * t.Pitch, t.Pitch));
        }
        return JsonSerializer.Serialize(expected, AggregationProbe.Json) == JsonSerializer.Serialize(content.Canvases.OrderBy(c => c.Page), AggregationProbe.Json)
            && JsonSerializer.Serialize(omitted, AggregationProbe.Json) == JsonSerializer.Serialize(content.Omitted.OrderBy(o => o.Key).ThenBy(o => o.Top), AggregationProbe.Json);
    }
}
