using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ReportDiff.Core;

/// <summary>独立診断専用。数値の差を消さず、完全一致する周辺行から同一ページ内の対応IDだけを提案する。</summary>
internal static class IndependentNumericRows
{
    internal sealed record Row(PageSpace Side, int Page, int Index, int Top, int Height, double Left, string Text);
    internal sealed record Anchor(Row A, Row B);
    internal sealed record Pair(Row A, Row B, string Label, IReadOnlyList<Anchor> Anchors);
    internal sealed record Input(bool Prepared, bool SelectionLimited, IReadOnlyList<PageFlowPageKey> Pages, IReadOnlyList<Row> Rows);
    internal sealed record Decision(string Status, string? Reason, IReadOnlyList<Pair> Pairs);
    private static readonly Regex Number = new(@"^[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static Input Describe(PageFlowPlan plan, bool limited = false) => new(plan.Inference.Layouts.Status == "prepared", limited,
        plan.Inference.Layouts.A.Concat(plan.Inference.Layouts.B).Select(l => l.Page.Key).ToArray(),
        plan.Inference.Layouts.A.Concat(plan.Inference.Layouts.B).SelectMany(l => l.Body.Select((r, i) =>
            new Row(l.Page.Key.Side, l.Page.Key.Page, i, l.BodyStart + i * l.Pitch, l.Pitch, r.Bounds.Left, r.Text))).ToArray());

    internal static Decision Find(Input input)
    {
        if (!input.Prepared || input.SelectionLimited) return Skip("unprepared_or_limited");
        if (input.Pages.Count > PageFlowLimits.MaximumSelectedPages * 2 || input.Rows.Count > PageFlowLimits.MaximumLines) return Skip("resource_limit");
        var pa = input.Pages.Where(p => p.Side == PageSpace.A).Select(p => p.Page).Order().ToArray();
        var pb = input.Pages.Where(p => p.Side == PageSpace.B).Select(p => p.Page).Order().ToArray();
        if (!pa.SequenceEqual(pb) || !pa.SequenceEqual(Enumerable.Range(1, pa.Length))) return Skip("unpaired_or_missing_pages");
        if (input.Rows.Any(r => r.Side is not (PageSpace.A or PageSpace.B) || r.Page < 1 || r.Index < 0 || r.Top < 0 || r.Height <= 0
            || (long)r.Top + r.Height > int.MaxValue || !double.IsFinite(r.Left) || !input.Pages.Contains(new(r.Side, r.Page)))) return Skip("invalid_rows");
        foreach (var page in input.Rows.GroupBy(r => (r.Side, r.Page)))
        {
            var ordered = page.OrderBy(r => r.Index).ToArray();
            if (!ordered.Select(r => r.Index).SequenceEqual(Enumerable.Range(0, ordered.Length))
                || ordered.Select(r => r.Height).Distinct().Count() != 1
                || ordered.Zip(ordered.Skip(1), (a, b) => a.Top + a.Height == b.Top).Any(v => !v)) return Skip("invalid_rows");
        }
        var a = Ordered(PageSpace.A); var b = Ordered(PageSpace.B);
        if (a.Select(r => r.Text).Distinct().Count() != a.Length || b.Select(r => r.Text).Distinct().Count() != b.Length) return Skip("repeated_text");
        var byA = a.ToDictionary(r => r.Text); var byB = b.ToDictionary(r => r.Text);
        if (!a.Where(r => byB.ContainsKey(r.Text)).Select(r => r.Text).SequenceEqual(b.Where(r => byA.ContainsKey(r.Text)).Select(r => r.Text))) return Skip("reordered_text");
        var labelsA = a.Where(r => Label(r.Text) is not null).GroupBy(r => Label(r.Text)!).ToDictionary(g => g.Key, g => g.ToArray());
        var labelsB = b.Where(r => Label(r.Text) is not null).GroupBy(r => Label(r.Text)!).ToDictionary(g => g.Key, g => g.ToArray());
        var byPosition = input.Rows.ToDictionary(r => (r.Side, r.Page, r.Index));
        var pairs = new List<Pair>();
        foreach (var row in a)
        {
            var label = Label(row.Text);
            if (byB.ContainsKey(row.Text) || label is null || labelsA[label].Length != 1
                || !labelsB.TryGetValue(label, out var targets) || targets.Length != 1) continue;
            var other = targets[0];
            if (byA.ContainsKey(other.Text) || row.Page != other.Page || row.Height != other.Height || Math.Abs(row.Left - other.Left) > .5) continue;
            foreach (var offsets in new int[][] { [-2, -1], [1, 2], [-1, 1] })
            {
                var anchors = new List<Anchor>();
                foreach (var offset in offsets)
                {
                    if (!byPosition.TryGetValue((PageSpace.A, row.Page, row.Index + offset), out var before)
                        || !byPosition.TryGetValue((PageSpace.B, other.Page, other.Index + offset), out var after)
                        || before.Text != after.Text || before.Height != after.Height || Math.Abs(before.Left - after.Left) > .5
                        || before.Top - after.Top != row.Top - other.Top) break;
                    anchors.Add(new(before, after));
                }
                if (anchors.Count == 2) { pairs.Add(new(row, other, label, anchors.AsReadOnly())); break; }
            }
        }
        return new(pairs.Count > 0 ? "proposed" : "skipped", pairs.Count > 0 ? null : "no_supported_numeric_pair", pairs.AsReadOnly());
        Row[] Ordered(PageSpace side) => input.Rows.Where(r => r.Side == side).OrderBy(r => r.Page).ThenBy(r => r.Index).ToArray();
        static Decision Skip(string reason) => new("skipped", reason, []);
    }

    internal static string? Label(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries); var numeric = tokens.Select(t => Number.IsMatch(t)).ToArray();
        if (!numeric.Any(v => v) || numeric.Count(v => !v) < 2) return null;
        // 数字を含む未対応書式・英数字IDを数値欄へ暗黙変換しない。ラベル自体は完全一致で比較する。
        return string.Concat(tokens.Select((t, i) => numeric[i] ? "N;" : "T" + t.Length.ToString(CultureInfo.InvariantCulture) + ":" + t + ";"));
    }

    internal static RowTextResult Identified(PageFlowPageKey key, PageFlowPlan original, IReadOnlyList<Pair> pairs)
    {
        var layout = original.Inference.Layouts.A.Concat(original.Inference.Layouts.B).Single(l => l.Page.Key == key);
        // 診断用の論理ID。位置と元画像は変えない。元本文はoriginalとPairに保持する。
        return new("available", null, layout.Page.Lines.Select(line => new RowWord(
            key.Side == PageSpace.B ? pairs.SingleOrDefault(p => p.B.Page == key.Page && p.B.Text == line.Text)?.A.Text ?? line.Text : line.Text,
            line.Bounds, [line.Baseline])).ToArray());
    }

    internal static PageFlowPlan ForActualSupport(PageFlowPlan mapped, PageFlowPlan original, PageFlowDocumentDescriptor identifiedDocument)
    {
        // 写像は対応IDから作るが、採用評価の完全一致行は元の本文へ戻す。変更行を支持帯へ数えない。
        var constructor = typeof(PageFlowPlan).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return (PageFlowPlan)constructor.Invoke([identifiedDocument, mapped.Pages.ToDictionary(p => p.Number, _ => new ComparisonParameters()),
            original.Inference, mapped.Verifications.ToArray(), mapped.Links.ToArray(), mapped.Pages.ToArray(), mapped.Decision]);
    }
}
