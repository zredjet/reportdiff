namespace ReportDiff.Core;

/// <summary>候補帯の全行が同じ物理ページ内に対応し、境界を跨ぐ共通行がない証明。画素一致を意味しない。</summary>
public static class PageFlowNonflow
{
    public const string Reason = "same_page_rows_not_flow";
    public sealed record Match(PageFlowBand Row, PageFlowBand Counterpart, PageFlowBand OriginalRow, PageFlowBand OriginalCounterpart);
    public sealed class Proof
    {
        internal PageFlowDocumentDescriptor Document { get; }
        public int CandidateIndex { get; }
        public PageFlowBand Source { get; }
        public PageFlowBand Target { get; }
        public IReadOnlyList<Match> SourceRows { get; }
        public IReadOnlyList<Match> TargetRows { get; }
        internal Proof(PageFlowDocumentDescriptor document, int index, PageFlowBand source, PageFlowBand target, Match[] from, Match[] to)
        { Document = document; CandidateIndex = index; Source = source; Target = target; SourceRows = Array.AsReadOnly(from); TargetRows = Array.AsReadOnly(to); }
    }
    public sealed record Result(IReadOnlyList<Proof> Proofs, long DescriptorBytes, string? FailureReason);

    public static Result Find(PageFlowDocumentDescriptor document, PageFlowInference.Inference inference, bool selectionLimited,
        PageFlowNumericRows.Result? numeric = null)
        => FindCore(document, inference, selectionLimited, numeric, null, 0);

    internal static Result FindTerminal(PageFlowDocumentDescriptor document, PageFlowInference.Inference inference,
        IReadOnlyDictionary<int, ComparisonParameters> parameters, RowOptions options, bool selectionLimited, bool alignmentEnabled,
        PageFlowNumericRows.Result numeric)
    {
        if (numeric.Proofs.Count != 0 || !PageFlowTerminalScope.Eligible(document, inference, parameters, options, selectionLimited, alignmentEnabled)
            || !inference.Proposals.Any(p => p.Status == "skipped" && p.Reason == "text_mismatch")) return new([], 0, null);
        var workspace = PageFlowTerminalScope.NonflowWorkspace(inference);
        var remaining = PageFlowLimits.MaximumDescriptorBytes - document.Usage.DescriptorBytes - numeric.DescriptorBytes;
        if (workspace > remaining) return new([], 0, "flow_descriptor_limit");
        var scope = PageFlowTerminalScope.CreateReserved(document, inference, workspace, remaining);
        return FindCore(document, inference, selectionLimited, numeric, scope, workspace);
    }

    private static Result FindCore(PageFlowDocumentDescriptor document, PageFlowInference.Inference inference, bool selectionLimited,
        PageFlowNumericRows.Result? numeric, PageFlowTerminalScope? terminal, long workspace)
    {
        terminal?.VerifyBinding(document, inference);
        numeric?.VerifyBinding(document);
        numeric?.VerifyBinding(inference);
        if (selectionLimited || inference.Layouts.Status != "prepared"
            || !inference.Proposals.Any(p => p.Status == "skipped" && p.Reason == "text_mismatch")) return new([], 0, null);
        var layouts = inference.Layouts.A.Concat(inference.Layouts.B).ToArray();
        var aPages = inference.Layouts.A.Select(l => l.Page.Key.Page).Order().ToArray();
        var bPages = inference.Layouts.B.Select(l => l.Page.Key.Page).Order().ToArray();
        if ((terminal is null && !aPages.SequenceEqual(bPages)) || !aPages.SequenceEqual(Enumerable.Range(1, aPages.Length))
            || !layouts.Select(l => l.Page.Key).OrderBy(k => k.Side).ThenBy(k => k.Page)
                .SequenceEqual(document.Pages.Select(p => p.Key).OrderBy(k => k.Side).ThenBy(k => k.Page))
            || layouts.Any(l => !l.Regular)) return new([], 0, null);
        // 本文は既存文字列への参照だけ。画像・行ハッシュ・本文文字列を複製しない。
        var rows = layouts.SelectMany(l => l.Body.Select((r, i) => new PageFlowAggregation.Row(l.Page.Key.Side,
            l.Page.Key.Page, l.BodyStart + i * l.Pitch, l.Pitch, r.Text))).Select(r => numeric?.Identify(r) ?? r).ToArray();
        var a = Ordered(PageSpace.A); var b = Ordered(PageSpace.B);
        if (a.Select(r => r.Identity).Distinct().Count() != a.Length || b.Select(r => r.Identity).Distinct().Count() != b.Length) return new([], 0, null);
        var byA = a.ToDictionary(r => r.Identity); var byB = b.ToDictionary(r => r.Identity);
        if (!a.Where(r => byB.ContainsKey(r.Identity)).Select(r => r.Identity).SequenceEqual(b.Where(r => byA.ContainsKey(r.Identity)).Select(r => r.Identity)))
            return new([], 0, null);
        var descriptors = document.Pages.ToDictionary(p => p.Key);
        var proofs = new List<Proof>(); long bytes = workspace;
        foreach (var (p, index) in inference.Proposals.Select((p, i) => (p, i)))
        {
            if (p.Status != "skipped" || p.Reason != "text_mismatch" || p.Source is not { } source || p.Target is not { } target
                || source.Page.Side == target.Page.Side || target.Page.Page != source.Page.Page + 1) continue;
            if (a.Where(r => byB.ContainsKey(r.Identity)).Any(r => Math.Min(r.Page, byB[r.Identity].Page) <= source.Page.Page
                && Math.Max(r.Page, byB[r.Identity].Page) > source.Page.Page)) continue;
            var from = Counterparts(source); var to = Counterparts(target);
            if (from is null || to is null) continue;
            // 証明・読取専用配列の固定費と、論理／元帯4個・参照配列を含む行対応の保守的計上。
            var added = 256L + 512L * (from.Length + to.Length);
            if (document.Usage.DescriptorBytes + (numeric?.DescriptorBytes ?? 0) + bytes + added > PageFlowLimits.MaximumDescriptorBytes)
                return new([], 0, "flow_descriptor_limit");
            bytes += added;
            proofs.Add(new(document, index, source, target, from.Select(Make).ToArray(), to.Select(Make).ToArray()));
        }
        return new(proofs.AsReadOnly(), proofs.Count == 0 ? 0 : bytes, null);

        PageFlowAggregation.Row[] Ordered(PageSpace side) => rows.Where(r => r.Side == side).OrderBy(r => r.Page).ThenBy(r => r.Start).ToArray();
        PageFlowAggregation.Row[]? Counterparts(PageFlowBand band)
        {
            var contained = rows.Where(r => r.Side == band.Page.Side && r.Page == band.Page.Page && r.Start >= band.Top && r.Start + r.Length <= band.Bottom)
                .OrderBy(r => r.Start).ToArray();
            if (contained.Length == 0 || contained[0].Start != band.Top || contained[^1].Start + contained[^1].Length != band.Bottom
                || contained.Zip(contained.Skip(1), (x, y) => x.Start + x.Length == y.Start).Any(v => !v)) return null;
            var opposite = band.Page.Side == PageSpace.A ? byB : byA;
            if (contained.Any(r => !opposite.TryGetValue(r.Identity, out var other) || other.Page != r.Page || other.Length != r.Length
                || Original(r) is null || Original(other) is null)) return null;
            return contained;
        }
        PageFlowBand Band(PageFlowAggregation.Row r) => new(new(r.Side, r.Page), r.Start, r.Length);
        PageFlowBand? Original(PageFlowAggregation.Row r) => descriptors[new(r.Side, r.Page)].MapOriginalBand(Band(r));
        Match Make(PageFlowAggregation.Row r)
        {
            var other = (r.Side == PageSpace.A ? byB : byA)[r.Identity];
            return new(Band(r), Band(other), Original(r)!, Original(other)!);
        }
    }
}
