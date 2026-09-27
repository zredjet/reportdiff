using System.Globalization;
using System.Text;
using static ReportDiff.Core.PageFlowInference;

namespace ReportDiff.Core;

/// <summary>全文とは別の行対応。数値変更は同一ページの写像と集約だけに使い、送り・支持の完全一致には使わない。</summary>
public static class PageFlowNumericRows
{
    public sealed record Row(PageFlowBand Band, PageFlowBand OriginalBand, string Text);
    public sealed record Anchor(Row A, Row B);
    public sealed class Proof
    {
        public int Id { get; }
        public Row A { get; }
        public Row B { get; }
        public IReadOnlyList<int> ChangedTokenIndices { get; }
        public IReadOnlyList<Anchor> Anchors { get; }
        internal Proof(int id, Row a, Row b, int[] changed, Anchor[] anchors)
        { Id = id; A = a; B = b; ChangedTokenIndices = Array.AsReadOnly(changed); Anchors = Array.AsReadOnly(anchors); }
    }
    public sealed class Result
    {
        private readonly PageFlowDocumentDescriptor? document;
        private readonly IReadOnlyList<Layout> layouts;
        private readonly Dictionary<(PageSpace, int, int, int, string), PageFlowRowIdentity> identities;
        public IReadOnlyList<Proof> Proofs { get; }
        public long DescriptorBytes { get; }
        public string? FailureReason { get; }
        internal Result(PageFlowDocumentDescriptor? document, IReadOnlyList<Layout> layouts, Proof[] proofs, long bytes, string? reason)
        {
            this.document = document; this.layouts = layouts; Proofs = Array.AsReadOnly(proofs); DescriptorBytes = bytes; FailureReason = reason;
            identities = proofs.SelectMany(p => new[] { (p.A, p.Id), (p.B, p.Id) }).ToDictionary(
                x => (x.Item1.Band.Page.Side, x.Item1.Band.Page.Page, x.Item1.Band.Top, x.Item1.Band.Height, x.Item1.Text),
                x => new PageFlowRowIdentity(null, x.Id));
        }
        internal static Result Empty { get; } = new(null, [], [], 0, null);
        internal Result Discard(string reason) => new(document, layouts, [], 0, reason);
        internal void VerifyBinding(PageFlowDocumentDescriptor expected)
        {
            if (!ReferenceEquals(document, expected)) throw new ArgumentException("数値行の対応証拠が別の文書に属しています。");
        }
        internal void VerifyBinding(Layout a, Layout b)
        {
            if (!layouts.Any(l => ReferenceEquals(l, a)) || !layouts.Any(l => ReferenceEquals(l, b)))
                throw new ArgumentException("数値行の対応証拠が別の行配置に属しています。");
        }
        internal void VerifyBinding(Inference inference)
        {
            var actual = inference.Layouts.A.Concat(inference.Layouts.B).ToArray();
            if (actual.Length != layouts.Count || actual.Distinct(ReferenceEqualityComparer.Instance).Count() != actual.Length
                || actual.Any(l => !layouts.Any(x => ReferenceEquals(x, l))))
                throw new ArgumentException("数値行の根拠にした行配置が変更されています。");
        }
        internal PageFlowAggregation.Row Identify(PageFlowAggregation.Row row) => row with
        { NumericIdentity = identities.TryGetValue((row.Side, row.Page, row.Start, row.Length, row.Text), out var identity) ? identity : null };
        internal PageFlowRowIdentity Identity(Layout layout, int index) => Identify(new(layout.Page.Key.Side, layout.Page.Key.Page,
            layout.BodyStart + index * layout.Pitch, layout.Pitch, layout.Body[index].Text)).Identity;
    }

    public static Result Find(PageFlowDocumentDescriptor document, Inference inference, bool selectionLimited,
        Func<int, ComparisonParameters> forPage, RowOptions options)
    {
        var layouts = inference.Layouts.A.Concat(inference.Layouts.B).ToArray();
        Result Empty(string? reason = null) => new(document, layouts, [], 0, reason);
        if (selectionLimited || inference.Layouts.Status != "prepared") return Empty();
        if (layouts.Length > PageFlowLimits.MaximumSelectedPages * 2 || layouts.Sum(l => (long)l.Body.Count) > PageFlowLimits.MaximumLines)
            return Empty("flow_descriptor_limit");
        var ap = inference.Layouts.A.Select(l => l.Page.Key.Page).Order().ToArray();
        var bp = inference.Layouts.B.Select(l => l.Page.Key.Page).Order().ToArray();
        var descriptors = document.Pages.ToHashSet(ReferenceEqualityComparer.Instance);
        if (!ap.SequenceEqual(bp) || !ap.SequenceEqual(Enumerable.Range(1, ap.Length)) || layouts.Length != document.Pages.Count
            || layouts.Any(l => !l.Regular || !descriptors.Contains(l.Page))) return Empty();
        foreach (var layout in layouts)
        {
            var originalLines = layout.Page.Lines.ToHashSet(ReferenceEqualityComparer.Instance);
            if (layout.Body.Any(r => !originalLines.Contains(r))) return Empty();
        }
        var rows = layouts.SelectMany(l => l.Body.Select((r, i) => new Item(l, i, r))).ToArray();
        if (rows.Sum(r => (long)r.Line.Text.Length) > PageFlowLimits.MaximumTextCharacters) return Empty("flow_descriptor_limit");
        var a = Ordered(PageSpace.A); var b = Ordered(PageSpace.B);
        if (a.Select(r => r.Line.Text).Distinct().Count() != a.Length || b.Select(r => r.Line.Text).Distinct().Count() != b.Length) return Empty();
        var byA = a.ToDictionary(r => r.Line.Text); var byB = b.ToDictionary(r => r.Line.Text);
        if (!a.Where(r => byB.ContainsKey(r.Line.Text)).Select(r => r.Line.Text)
            .SequenceEqual(b.Where(r => byA.ContainsKey(r.Line.Text)).Select(r => r.Line.Text))) return Empty();
        var labelsA = Labels(a); var labelsB = Labels(b);
        var positions = rows.ToDictionary(r => (r.Layout.Page.Key, r.Index));
        var proofs = new List<Proof>(); long bytes = 0;
        foreach (var row in a)
        {
            if (byB.ContainsKey(row.Line.Text) || Label(row.Line.Text) is not { } label || labelsA[label].Length != 1
                || !labelsB.TryGetValue(label, out var choices) || choices.Length != 1) continue;
            var other = choices[0];
            if (byA.ContainsKey(other.Line.Text) || row.Layout.Page.Key.Page != other.Layout.Page.Key.Page
                || row.Layout.Pitch != other.Layout.Pitch || Math.Abs(row.Line.Bounds.Left - other.Line.Bounds.Left) > .5
                || !Eligible(row) || !Eligible(other) || Evidence(row) is not { } ea || Evidence(other) is not { } eb) continue;
            foreach (var offsets in new int[][] { [-2, -1], [1, 2], [-1, 1] })
            {
                var anchors = new List<Anchor>();
                foreach (var offset in offsets)
                {
                    if (!positions.TryGetValue((row.Layout.Page.Key, row.Index + offset), out var x)
                        || !positions.TryGetValue((other.Layout.Page.Key, other.Index + offset), out var y)
                        || x.Line.Text != y.Line.Text || x.Layout.Pitch != y.Layout.Pitch
                        || Math.Abs(x.Line.Bounds.Left - y.Line.Bounds.Left) > .5 || Top(x) - Top(y) != Top(row) - Top(other)
                        || !Eligible(x) || !Eligible(y) || Evidence(x) is not { } ex || Evidence(y) is not { } ey) break;
                    anchors.Add(new(ex, ey));
                }
                if (anchors.Count != 2) continue;
                var ta = Tokens(row.Line.Text); var tb = Tokens(other.Line.Text);
                var changed = Enumerable.Range(0, ta.Length).Where(i => ta[i] != tb[i]).ToArray();
                // 証拠6行の論理/O帯・本文参照・索引・読取専用配列を含め、本文と画像は複製しない。
                var added = 2048L + 4L * changed.Length + (proofs.Count == 0 ? 256L + 8L * layouts.Length : 0);
                if (document.Usage.DescriptorBytes + bytes + added > PageFlowLimits.MaximumDescriptorBytes) return Empty("flow_descriptor_limit");
                bytes += added; proofs.Add(new(proofs.Count + 1, ea, eb, changed, anchors.ToArray())); break;
            }
        }
        return new(document, layouts, proofs.ToArray(), bytes, null);

        Item[] Ordered(PageSpace side) => rows.Where(r => r.Layout.Page.Key.Side == side).OrderBy(r => r.Layout.Page.Key.Page).ThenBy(r => r.Index).ToArray();
        Row? Evidence(Item item)
        {
            var band = new PageFlowBand(item.Layout.Page.Key, Top(item), item.Layout.Pitch);
            return item.Layout.Page.MapOriginalBand(band) is { } original ? new(band, original, item.Line.Text) : null;
        }
        bool Eligible(Item item)
        {
            var p = forPage(item.Layout.Page.Key.Page);
            var radius = item.Layout.Page.Key.Side == PageSpace.B ? Units.RoundPixels(options.MaxShiftMm, p.Dpi) : 0;
            return !RowExclusions.Rectangles(p).Select(r => PageMap.ContinuousPixels(r, p.Dpi)).Any(e =>
                item.Line.Bounds.Left < e.Right && item.Line.Bounds.Right > e.Left
                && item.Line.Bounds.Top < e.Bottom + radius && item.Line.Bounds.Bottom > e.Top - radius);
        }
    }

    private sealed record Item(Layout Layout, int Index, PageFlowLine Line);
    private static int Top(Item row) => row.Layout.BodyStart + row.Index * row.Layout.Pitch;
    private static Dictionary<string, Item[]> Labels(Item[] rows) => rows.Select(r => (Row: r, Label: Label(r.Line.Text)))
        .Where(r => r.Label is not null).GroupBy(r => r.Label!).ToDictionary(g => g.Key, g => g.Select(r => r.Row).ToArray());
    private static string[] Tokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    private static string? Label(string text)
    {
        var tokens = Tokens(text); var count = 0; var label = new StringBuilder();
        foreach (var token in tokens)
        {
            if (Number(token)) { count++; label.Append("N;"); }
            else label.Append('T').Append(token.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(token).Append(';');
        }
        return count > 0 && tokens.Length - count >= 2 ? label.ToString() : null;
    }
    private static bool Number(string token)
    {
        var start = token[0] is '+' or '-' ? 1 : 0; var dot = false; var digits = 0;
        for (var i = start; i < token.Length; i++)
        {
            if (token[i] is >= '0' and <= '9') { digits++; continue; }
            if (token[i] != '.' || dot || i == token.Length - 1) return false;
            dot = true;
        }
        return digits > 0;
    }
}

/// <summary>元本文とは型を分離した比較キー。数値対応の番号は同じ文書の検証済み証拠からだけ設定する。</summary>
internal readonly record struct PageFlowRowIdentity(string? Text, int Number = 0);
