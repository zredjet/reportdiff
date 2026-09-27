using OpenCvSharp;
using ReportDiff.Core;
using static ReportDiff.Core.PageFlowInference;
using Built = ReportDiff.Core.PageFlowSurface.Built;
using Removed = ReportDiff.Core.PageFlowSurface.Removed;

/// <summary>診断専用。前側1行の内部原因を仮置きし、後続の既存採用検査を観測する。製品の証明には使わない。</summary>
internal static class UnpairedLocalSupportSurface
{
    // PageFlowSurface の数値対応なしの手順を固定した診断コピー。唯一の差分は Gap の experimental 分岐。
    internal static Built Create(Layout a, Layout b, Mat imageA, Mat imageB, IReadOnlyList<Proposal> verified, ComparisonParameters parameters)
    {
        if (!a.Regular || !b.Regular || a.BodyStart != b.BodyStart || a.Pitch != b.Pitch)
            return Fail("unproven_regular_layout");
        if (a.Body.GroupBy(r => r.Text).Any(g => g.Count() > 1) || b.Body.GroupBy(r => r.Text).Any(g => g.Count() > 1))
            return Fail("ambiguous_page_text");
        var keysB = b.Body.Select(line => line.Text).ToList();
        var matches = a.Body.Select((line, i) => (A: i, B: keysB.IndexOf(line.Text))).Where(p => p.B >= 0).ToArray();
        if (matches.Length < 2 || matches.Zip(matches.Skip(1), (x, y) => x.B < y.B).Any(v => !v))
            return Fail("insufficient_monotone_support");
        var segments = new List<PageSegment>(); var kinds = new List<RowBandKind>(); var removed = new List<Removed>();
        var ay = 0; var by = 0; var dy = 0;
        Add(a.BodyStart, true, true, RowBandKind.Paired);
        foreach (var match in matches)
        {
            if (!Gap(a, a.BodyStart + match.A * a.Pitch, true) || !Gap(b, b.BodyStart + match.B * b.Pitch, false))
                return Fail("unverified_structural_band");
            Add(a.Pitch, true, true, RowBandKind.Paired);
        }
        if (!Gap(a, a.BodyEnd, true) || !Gap(b, b.BodyEnd, false)) return Fail("unverified_edge_band");
        var end = Math.Max(ay, by);
        Add(end - ay, true, false, RowBandKind.WhiteSpace);
        Add(end - by, false, true, RowBandKind.WhiteSpace);
        Add(imageA.Height - end, true, true, RowBandKind.Paired);
        var map = new PageMap(imageA.Size(), imageB.Size(), new(imageA.Width, dy), segments);
        var built = RowComparisonSurface.Create(imageA, imageB, map, kinds, parameters);
        return built.Success ? new("built", null, map, Array.AsReadOnly(kinds.ToArray()), Array.AsReadOnly(removed.ToArray()), built.Surface) : Fail(built.Detail!);

        bool Gap(Layout layout, int until, bool isA)
        {
            var start = isA ? ay : by; var length = until - start;
            if (length == 0) return true;
            if (length < 0) return false;
            var band = new PageFlowBand(layout.Page.Key, start, length);
            var carry = verified.Any(p => p.Status == "band_verified" && p.Reason is null && (p.Source == band || p.Target == band));
            var first = (start - layout.BodyStart) / layout.Pitch;
            var last = (until - layout.BodyStart) / layout.Pitch;
            var before = matches.Count(p => (isA ? p.A : p.B) < first);
            var after = matches.Count(p => (isA ? p.A : p.B) >= last);
            var local = first > 0 && last < layout.Body.Count && before >= 2 && after >= 2;
            // 前ページからの先頭送り帯は局所支持へ加算しない。原因を仮置きする条件のみに使う。
            var incoming = verified.Any(p => p.Status == "band_verified" && p.Reason is null
                && p.Target?.Page == layout.Page.Key && p.Source?.Page.Page == layout.Page.Key.Page - 1
                && p.Target.Top == layout.BodyStart && p.Target.Bottom <= start);
            var experimental = first > 0 && last < layout.Body.Count && length == layout.Pitch
                && before == 1 && after >= 2 && incoming;
            if (!carry && !local && !experimental) return false;
            removed.Add(new(band, carry ? "verified_carry_range" : local ? "interior_unmatched_with_two_sided_support"
                : "experimental_single_before_cause", segments.Count));
            Add(length, isA, !isA, RowBandKind.Structural);
            return true;
        }
        void Add(int length, bool hasA, bool hasB, RowBandKind kind)
        {
            if (length < 0) throw new InvalidOperationException("推定写像が逆転しています。");
            if (length == 0) return;
            segments.Add(new(dy, length, hasA ? ay : null, hasB ? by : null)); kinds.Add(kind);
            if (hasA) ay += length; if (hasB) by += length; dy += length;
        }
        static Built Fail(string reason) => new("skipped", reason, null, [], [], null);
    }
}
