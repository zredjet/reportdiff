using static ReportDiff.Core.PageFlowInference;

namespace ReportDiff.Core;

/// <summary>複数の適格変位を、境界を跨ぐ全行または全変位の同一ページ対応から再検証する。</summary>
public static class PageFlowAmbiguity
{
    public const string NonflowReason = "ambiguous_same_page_rows_not_flow";
    public sealed record Shift(int Dy, IReadOnlyList<string> SupportText);
    public sealed record Row(string Text, PageFlowNonflow.Match Match);
    public sealed record Alternative(int Dy, PageFlowBand Source, PageFlowBand Target,
        PageFlowBand OriginalSource, PageFlowBand OriginalTarget, IReadOnlyList<Row> SourceRows, IReadOnlyList<Row> TargetRows);
    public sealed class Proof
    {
        internal PageFlowDocumentDescriptor Document { get; }
        public int CandidateIndex { get; }
        public PageFlowPageKey Boundary { get; }
        public int? SelectedDy { get; }
        public IReadOnlyList<Shift> Shifts { get; }
        public IReadOnlyList<Row> CrossingRows { get; }
        public IReadOnlyList<Alternative> Alternatives { get; }
        internal Proof(PageFlowDocumentDescriptor document, int index, PageFlowPageKey boundary, int? selected,
            Shift[] shifts, Row[] crossing, Alternative[] alternatives)
        {
            Document = document; CandidateIndex = index; Boundary = boundary; SelectedDy = selected;
            Shifts = Array.AsReadOnly(shifts); CrossingRows = Array.AsReadOnly(crossing); Alternatives = Array.AsReadOnly(alternatives);
        }
    }
    public sealed class Result
    {
        public IReadOnlyList<Proof> Proofs { get; }
        public IReadOnlyList<Proposal> Proposals { get; }
        public long DescriptorBytes { get; }
        public string? FailureReason { get; }
        internal Result(Proof[] proofs, Proposal[] proposals, long bytes, string? reason = null)
        { Proofs = Array.AsReadOnly(proofs); Proposals = Array.AsReadOnly(proposals); DescriptorBytes = bytes; FailureReason = reason; }
        internal static Result Empty { get; } = new([], [], 0);
    }
    private sealed record Line(Layout Layout, int Index)
    {
        internal PageFlowLine Text => Layout.Body[Index];
        internal PageFlowBand Band => new(Layout.Page.Key, Layout.BodyStart + Index * Layout.Pitch, Layout.Pitch);
    }

    // 途中まで成立した境界は返さない。追加メモリは行索引・作業配列も含め確保前に予約する。
    public static Result Find(PageFlowDocumentDescriptor document, Inference inference, RowOptions options, int dpi,
        bool selectionLimited, long availableBytes)
        => FindCore(document, inference, options, dpi, selectionLimited, availableBytes, false);

    internal static Result FindTerminal(PageFlowDocumentDescriptor document, Inference inference,
        IReadOnlyDictionary<int, ComparisonParameters> parameters, RowOptions options, bool selectionLimited,
        bool alignmentEnabled, long availableBytes)
    {
        if (!PageFlowTerminalScope.Eligible(document, inference, parameters, options, selectionLimited, alignmentEnabled)) return Result.Empty;
        return FindCore(document, inference, options, parameters.Values.First().Dpi, selectionLimited, availableBytes, true);
    }

    private static Result FindCore(PageFlowDocumentDescriptor document, Inference inference, RowOptions options, int dpi,
        bool selectionLimited, long availableBytes, bool terminal)
    {
        options.Validated(dpi);
        if (!ReferenceEquals(inference.Document, document) || !ReferenceEquals(inference.BoundLayouts, inference.Layouts)
            || !ReferenceEquals(inference.BoundProposals, inference.Proposals)
            || inference.MaximumShiftPixels != Units.RoundPixels(options.MaxShiftMm, dpi) || inference.MinimumSupport != options.MinSupportBands) return Result.Empty;
        if (selectionLimited || inference.Layouts.Status != "prepared" || inference.Proposals.Count > PageFlowLimits.MaximumCandidates
            || !inference.Proposals.Any(p => p.AmbiguousSource is not null)) return Result.Empty;
        var rowCount = inference.Layouts.A.Sum(l => (long)l.Body.Count) + inference.Layouts.B.Sum(l => (long)l.Body.Count);
        var layoutCount = inference.Layouts.A.Count + inference.Layouts.B.Count;
        if (rowCount > PageFlowLimits.MaximumLines || layoutCount > PageFlowLimits.MaximumSelectedPages * 2) return Result.Empty;
        availableBytes = Math.Min(availableBytes, PageFlowLimits.MaximumDescriptorBytes - document.Usage.DescriptorBytes);
        long bytes = 1024 + rowCount * 2048 + layoutCount * 256L + inference.Proposals.Count * 512L;
        if (bytes > availableBytes) return Limited();
        var layouts = inference.Layouts.A.Concat(inference.Layouts.B).ToArray();
        var aa = inference.Layouts.A.Select(l => l.Page.Key.Page).Order().ToArray();
        var bb = inference.Layouts.B.Select(l => l.Page.Key.Page).Order().ToArray();
        if ((!terminal && !aa.SequenceEqual(bb)) || !aa.SequenceEqual(Enumerable.Range(1, aa.Length))
            || !bb.SequenceEqual(Enumerable.Range(1, bb.Length)) || layouts.Length != document.Pages.Count
            || layouts.Select(l => l.Page.Key).Distinct().Count() != layouts.Length
            || layouts.Any(l => !l.Regular || !document.Pages.Any(p => ReferenceEquals(p, l.Page))
                || l.Body.Any(r => !l.Page.Lines.Any(original => ReferenceEquals(original, r))))) return Result.Empty;
        var a = Lines(inference.Layouts.A); var b = Lines(inference.Layouts.B);
        if (a.Select(r => r.Text.Text).Distinct(StringComparer.Ordinal).Count() != a.Length
            || b.Select(r => r.Text.Text).Distinct(StringComparer.Ordinal).Count() != b.Length) return Result.Empty;
        var byA = a.ToDictionary(r => r.Text.Text, StringComparer.Ordinal); var byB = b.ToDictionary(r => r.Text.Text, StringComparer.Ordinal);
        if (!a.Where(r => byB.ContainsKey(r.Text.Text)).Select(r => r.Text.Text)
            .SequenceEqual(b.Where(r => byA.ContainsKey(r.Text.Text)).Select(r => r.Text.Text))) return Result.Empty;
        var common = a.Where(r => byB.ContainsKey(r.Text.Text)).Select(r => (A: r, B: byB[r.Text.Text])).ToArray();
        if (common.Any(p => Math.Abs(p.A.Layout.Page.Key.Page - p.B.Layout.Page.Key.Page) > 1)) return Result.Empty;
        if (terminal && Enumerable.Range(1, Math.Max(aa.Length, bb.Length) - 1).Any(n =>
            !common.Any(p => Math.Min(p.A.Layout.Page.Key.Page, p.B.Layout.Page.Key.Page) == n
                && Math.Max(p.A.Layout.Page.Key.Page, p.B.Layout.Page.Key.Page) == n + 1))) return Result.Empty;
        var proposals = inference.Proposals.ToArray(); var proofs = new List<Proof>(); var alternativesCount = 0;
        for (var index = 0; index < proposals.Length; index++)
        {
            var original = proposals[index];
            if (original.AmbiguousSource is not { } key) continue;
            if (original.Status != "skipped" || original.Reason != "ambiguous_displacement"
                || original.Source is not null || original.Target is not null) return Result.Empty;
            var source = layouts.SingleOrDefault(l => l.Page.Key == key);
            var opposite = key.Side == PageSpace.A ? PageSpace.B : PageSpace.A;
            var target = layouts.SingleOrDefault(l => l.Page.Key == new PageFlowPageKey(opposite, key.Page + 1));
            if (source is null || target is null) return Result.Empty;
            var lookup = key.Side == PageSpace.A ? byB : byA;
            var from = (key.Side == PageSpace.A ? a : b).Where(r => r.Layout == source).ToArray();
            var shifts = from.Where(r => lookup.TryGetValue(r.Text.Text, out var other) && other.Layout.Page.Key.Page == key.Page
                    && Math.Round(other.Text.Bounds.Left - r.Text.Bounds.Left) == 0)
                .Select(r => (Row: r, Dy: (int)Math.Round(lookup[r.Text.Text].Text.Baseline - r.Text.Baseline)))
                .Where(p => p.Dy > 0 && p.Dy <= Units.RoundPixels(options.MaxShiftMm, dpi)).GroupBy(p => p.Dy)
                .Where(g => g.Count() >= options.MinSupportBands).OrderBy(g => g.Key)
                .Select(g => new Shift(g.Key, Array.AsReadOnly(g.Select(p => p.Row.Text.Text).ToArray()))).ToArray();
            if (shifts.Length < 2) return Result.Empty;
            if ((alternativesCount += shifts.Length) > PageFlowLimits.MaximumCandidates) return new([], [], 0, "flow_candidate_limit");
            var crossing = common.Where(p => Math.Min(p.A.Layout.Page.Key.Page, p.B.Layout.Page.Key.Page) <= key.Page
                && Math.Max(p.A.Layout.Page.Key.Page, p.B.Layout.Page.Key.Page) > key.Page).ToArray();
            if (crossing.Length > 0)
            {
                var crossed = from.Where(r => lookup.TryGetValue(r.Text.Text, out var other) && other.Layout.Page.Key.Page == key.Page + 1).ToArray();
                // 全ての跨ぎが同方向に隣接し、末尾と先頭を隙間なく埋めること。最長変位だけでは選ばない。
                var height = crossed.Length * source.Pitch;
                if (crossed.Length != crossing.Length || height == 0 || height >= source.BodyEnd - source.BodyStart
                    || source.Pitch != target.Pitch || target.BodyStart + height > target.FooterStart
                    || !shifts.Any(s => s.Dy == height)
                    || crossed.Where((r, i) => r.Band.Top != source.BodyEnd - height + i * source.Pitch
                        || lookup[r.Text.Text].Band.Top != target.BodyStart + i * source.Pitch
                        || Math.Round(lookup[r.Text.Text].Text.Bounds.Left - r.Text.Bounds.Left) != 0).Any()) return Result.Empty;
                if (!Reserve(1024L + crossed.Length * 1024L)) return Limited();
                var rows = Matches(crossed, lookup); if (rows is null) return Result.Empty;
                proposals[index] = new(new(key, source.BodyEnd - height, height), new(target.Page.Key, target.BodyStart, height),
                    Array.AsReadOnly(crossed.Select(r => r.Text.Text).ToArray()), shifts.Single(s => s.Dy == height).SupportText.Count, "candidate", null);
                proofs.Add(new(document, index, key, height, shifts, rows, []));
            }
            else
            {
                if (terminal) return Result.Empty;
                var alternatives = new List<Alternative>();
                foreach (var shift in shifts)
                {
                    if (shift.Dy % source.Pitch != 0 || source.Pitch != target.Pitch || shift.Dy >= source.BodyEnd - source.BodyStart
                        || target.BodyStart + shift.Dy > target.BodyEnd) return Result.Empty;
                    if (!Reserve(1024L + 2L * (shift.Dy / source.Pitch) * 1024)) return Limited();
                    var sb = new PageFlowBand(key, source.BodyEnd - shift.Dy, shift.Dy);
                    var tb = new PageFlowBand(target.Page.Key, target.BodyStart, shift.Dy);
                    var sr = SamePage(sb, key.Side == PageSpace.A ? a : b, lookup);
                    var tr = SamePage(tb, key.Side == PageSpace.A ? b : a, key.Side == PageSpace.A ? byA : byB);
                    var so = source.Page.MapOriginalBand(sb); var to = target.Page.MapOriginalBand(tb);
                    if (sr is null || tr is null || so is null || to is null) return Result.Empty;
                    alternatives.Add(new(shift.Dy, sb, tb, so, to, Array.AsReadOnly(sr), Array.AsReadOnly(tr)));
                }
                proposals[index] = original with { Reason = NonflowReason };
                proofs.Add(new(document, index, key, null, shifts, [], alternatives.ToArray()));
            }
        }
        if (terminal && (proposals.Length != Math.Max(aa.Length, bb.Length) - 1
            || proposals.Any(p => p.Status != "candidate" || p.Source is null || p.Target is null)
            || proposals.Select(p => p.Source!.Page.Side).Distinct().Count() != 1
            || !proposals.Select(p => p.Source!.Page.Page).Order().SequenceEqual(Enumerable.Range(1, proposals.Length)))) return Result.Empty;
        return new(proofs.ToArray(), proposals, bytes);

        bool Reserve(long added) { if (added > availableBytes - bytes) return false; bytes += added; return true; }
        static Result Limited() => new([], [], 0, "flow_descriptor_limit");
        static Line[] Lines(IReadOnlyList<Layout> ls) => ls.OrderBy(l => l.Page.Key.Page)
            .SelectMany(l => Enumerable.Range(0, l.Body.Count).Select(i => new Line(l, i))).ToArray();
        static Row[]? Matches(Line[] rows, Dictionary<string, Line> lookup)
        {
            var result = new Row[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i]; var other = lookup[row.Text.Text];
                var ob = row.Layout.Page.MapOriginalBand(row.Band); var oo = other.Layout.Page.MapOriginalBand(other.Band);
                if (ob is null || oo is null) return null;
                result[i] = new(row.Text.Text, new(row.Band, other.Band, ob, oo));
            }
            return result;
        }
        static Row[]? SamePage(PageFlowBand band, Line[] rows, Dictionary<string, Line> lookup)
        {
            var contained = rows.Where(r => r.Layout.Page.Key == band.Page && r.Band.Top >= band.Top && r.Band.Bottom <= band.Bottom).ToArray();
            if (contained.Length == 0 || contained[0].Band.Top != band.Top || contained[^1].Band.Bottom != band.Bottom
                || contained.Zip(contained.Skip(1), (x, y) => x.Band.Bottom != y.Band.Top).Any(v => v)
                || contained.Any(r => !lookup.TryGetValue(r.Text.Text, out var other) || other.Layout.Page.Key.Page != band.Page.Page
                    || other.Layout.Pitch != r.Layout.Pitch)) return null;
            return Matches(contained, lookup);
        }
    }
}
