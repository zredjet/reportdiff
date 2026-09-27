namespace ReportDiff.Core;

/// <summary>末尾1枚の片側ページに限定した追加経路。対象範囲を実際の文書・推定結果へ結合する。</summary>
internal sealed class PageFlowTerminalScope
{
    private readonly PageFlowDocumentDescriptor document;
    private readonly PageFlowInference.Inference inference;
    private PageFlowTerminalScope(PageFlowDocumentDescriptor document, PageFlowInference.Inference inference)
    { this.document = document; this.inference = inference; }

    internal static bool Eligible(PageFlowDocumentDescriptor document, PageFlowInference.Inference inference,
        IReadOnlyDictionary<int, ComparisonParameters> parameters, RowOptions rows, bool selectionLimited, bool alignmentEnabled)
    {
        if (selectionLimited || alignmentEnabled || !rows.Enabled || !rows.CarryEnabled
            || !ReferenceEquals(inference.Document, document) || !ReferenceEquals(inference.BoundLayouts, inference.Layouts)
            || !ReferenceEquals(inference.BoundProposals, inference.Proposals) || inference.Layouts.Status != "prepared"
            || document.Pages.Any(p => p.GlobalMap is not null)) return false;
        var a = inference.Layouts.A; var b = inference.Layouts.B;
        if (Math.Min(a.Count, b.Count) < 2 || Math.Abs(a.Count - b.Count) != 1
            || document.Pages.Count != a.Count + b.Count || Math.Max(a.Count, b.Count) > PageFlowLimits.MaximumSelectedPages
            || !a.Select(l => l.Page.Key.Page).SequenceEqual(Enumerable.Range(1, a.Count))
            || !b.Select(l => l.Page.Key.Page).SequenceEqual(Enumerable.Range(1, b.Count))) return false;
        var normal = new ComparisonParameters();
        if (parameters.Count != Math.Max(a.Count, b.Count) || parameters.Any(p => p.Key < 1 || p.Key > parameters.Count
            || p.Value.Dpi != normal.Dpi || p.Value.Diff != normal.Diff || p.Value.Ink != normal.Ink
            || p.Value.Cluster != normal.Cluster || p.Value.Move != normal.Move
            || p.Value.Exclude.Count != 0 || p.Value.Regions.Count != 0)) return false;
        return a.Concat(b).All(l => l.Regular && document.Pages.Any(p => ReferenceEquals(l.Page, p)))
            && a.All(l => l.Page.Key.Side == PageSpace.A) && b.All(l => l.Page.Key.Side == PageSpace.B);
    }

    // 大きな索引・配列と証拠自身を確保する前に呼ぶ。本文文字列と画像を複製しない。
    internal static long NonflowWorkspace(PageFlowInference.Inference inference) => checked(1024L
        + inference.Layouts.A.Concat(inference.Layouts.B).Sum(l => (long)l.Body.Count) * 2048
        + (long)(inference.Layouts.A.Count + inference.Layouts.B.Count) * 256 + (long)inference.Proposals.Count * 512);
    internal static long AggregationWorkspace(PageFlowAggregation.Input input) => checked(1024L
        + (long)input.Rows.Count * 2048 + (long)input.Pages.Count * 512 + (long)input.Links.Count * 1024
        + input.Pages.Sum(p => (long)p.Structures.Count) * 2048);
    internal static PageFlowTerminalScope CreateReserved(PageFlowDocumentDescriptor document, PageFlowInference.Inference inference,
        long requiredBytes, long remainingBytes)
    {
        if (requiredBytes < 1024 || requiredBytes > remainingBytes) throw new ArgumentException("末尾ページの作業領域が予約されていません。");
        return new(document, inference);
    }
    internal void VerifyBinding(PageFlowDocumentDescriptor value, PageFlowInference.Inference inferred)
    {
        if (!ReferenceEquals(value, document) || !ReferenceEquals(inferred, inference)
            || !ReferenceEquals(inferred.Layouts, inference.BoundLayouts) || !ReferenceEquals(inferred.Proposals, inference.BoundProposals))
            throw new ArgumentException("末尾ページの証拠が別の文書・候補を参照しています。");
    }

    internal bool Matches(PageFlowAggregation.Input input)
    {
        var count = Math.Max(inference.Layouts.A.Count, inference.Layouts.B.Count);
        if (!input.GateReady || input.SelectionLimited || input.Pages.Count != count
            || input.Pages.Select(p => p.Number).Distinct().Count() != count
            || input.Pages.Any(p => p.Number < 1 || p.Number > count || p.Paired != (p.Number < count))) return false;
        var expected = inference.Layouts.A.Concat(inference.Layouts.B).SelectMany(l => l.Body.Select((r, i) =>
            (l.Page.Key.Side, l.Page.Key.Page, Start: l.BodyStart + i * l.Pitch, Length: l.Pitch, r.Text))).ToHashSet();
        return expected.Count == input.Rows.Count && input.Rows.All(r => r.NumericIdentity is null
            && expected.Remove((r.Side, r.Page, r.Start, r.Length, r.Text))) && expected.Count == 0;
    }
}
