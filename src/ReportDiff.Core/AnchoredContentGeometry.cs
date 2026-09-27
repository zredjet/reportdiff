using static ReportDiff.Core.PageFlowInference;

namespace ReportDiff.Core;

public sealed partial class AnchoredContentPlan
{
    private static AnchoredContentPiece[] BuildPieces(Layout source, Layout target, Dictionary<string, RowLocation> lookup, Action<long> reserve)
    {
        var count = 0; Walk((_, _, _, _, _, _) => count++);
        reserve(checked(64 + 264L * count));
        var result = new AnchoredContentPiece[count]; var index = 0;
        Walk((top, height, a, ay, b, by) => result[index++] = new(top, height,
            a is null ? null : new(a, ay, height), b is null ? null : new(b, by, height)));
        return result;

        void Walk(Action<int, int, PageFlowPageKey?, int, PageFlowPageKey?, int> add)
        {
            var a = source.Page.Key.Side == PageSpace.A ? source.Page.Key : target.Page.Key;
            var b = source.Page.Key.Side == PageSpace.B ? source.Page.Key : target.Page.Key;
            add(0, source.BodyStart, a, 0, b, 0);
            for (var i = 0; i < source.Body.Count; i++)
            {
                var top = checked(source.BodyStart + i * source.Pitch); var other = lookup[source.Body[i].Text];
                add(top, source.Pitch, source.Page.Key.Side == PageSpace.A ? source.Page.Key : other.Layout.Page.Key,
                    source.Page.Key.Side == PageSpace.A ? top : other.Top,
                    source.Page.Key.Side == PageSpace.B ? source.Page.Key : other.Layout.Page.Key,
                    source.Page.Key.Side == PageSpace.B ? top : other.Top);
            }
            var tail = Math.Max(source.BodyEnd, target.BodyEnd);
            if (tail > source.BodyEnd)
            {
                White(source.Page, source.BodyEnd, tail - source.BodyEnd);
                add(source.BodyEnd, tail - source.BodyEnd, source.Page.Key.Side == PageSpace.A ? a : null, source.BodyEnd,
                    source.Page.Key.Side == PageSpace.B ? b : null, source.BodyEnd);
            }
            if (tail < source.Page.Size.Height) add(tail, source.Page.Size.Height - tail, a, tail, b, tail);
        }
    }

    private static AnchoredDisplayPlan BuildDisplay(Layout a, Layout b, Dictionary<string, RowLocation> lookup,
        AnchoredContentEvidence proof, Action<long> reserve)
    {
        var count = 0; Walk((_, _, _, _, _) => count++);
        reserve(checked(64 + 264L * count));
        var segments = new AnchoredDisplaySegment[count]; var index = 0;
        Walk((top, height, ay, by, role) => segments[index++] = new(new(top, height,
            ay is null ? null : new(a.Page.Key, ay.Value, height), by is null ? null : new(b.Page.Key, by.Value, height)), role));
        return new(a.Page.Key.Page, a.Page.Size, segments);

        void Walk(Action<int, int, int?, int?, AnchoredBandRole> add)
        {
            int ay = 0, by = 0, y = 0;
            Add(a.BodyStart, true, true, AnchoredBandRole.Paired);
            for (var i = 0; i < a.Body.Count; i++)
            {
                if (!lookup.TryGetValue(a.Body[i].Text, out var match) || match.Layout != b) continue;
                Gap(a, checked(a.BodyStart + i * a.Pitch), true); Gap(b, match.Top, false);
                Add(a.Pitch, true, true, AnchoredBandRole.Paired);
            }
            Gap(a, a.BodyEnd, true); Gap(b, b.BodyEnd, false);
            var end = Math.Max(ay, by);
            if (ay < end) { White(a.Page, ay, end - ay); Add(end - ay, true, false, AnchoredBandRole.WhiteSpace); }
            if (by < end) { White(b.Page, by, end - by); Add(end - by, false, true, AnchoredBandRole.WhiteSpace); }
            Add(a.Page.Size.Height - end, true, true, AnchoredBandRole.Paired);

            void Gap(Layout layout, int until, bool isA)
            {
                var start = isA ? ay : by; var length = until - start;
                if (length == 0) return;
                if (length < 0) Reject("nonmonotone_display");
                var role = proof.Causes.Any(c => Matches(c.Span)) ? AnchoredBandRole.Cause
                    : Matches(proof.Source) || Matches(proof.Target) ? AnchoredBandRole.Carry : (AnchoredBandRole?)null;
                if (role is null) Reject("unverified_structural_band");
                Add(length, isA, !isA, role!.Value);
                bool Matches(OriginalRowSpan span) => span.Page == layout.Page.Key && span.Top == start && span.Height == length;
            }
            void Add(int length, bool hasA, bool hasB, AnchoredBandRole role)
            {
                if (length < 0) Reject("invalid_display_bounds");
                if (length == 0) return;
                add(y, length, hasA ? ay : null, hasB ? by : null, role);
                y = checked(y + length); if (hasA) ay = checked(ay + length); if (hasB) by = checked(by + length);
            }
        }
    }

    private static void White(PageFlowPageDescriptor page, int top, int length)
    {
        if (top < 0 || length < 0 || (long)top + length > page.Size.Height) Reject("invalid_white_range");
        // 収集時に全BGRを確認した非白フラグ。A2で再読込画素のSHAも検査する。
        for (var y = top; y < top + length; y++) if (page.RowHasNonwhite(y)) Reject("nonwhite_space");
    }

    private static void ValidateCoverage(PageFlowDocumentDescriptor document, AnchoredContentSurface[] surfaces,
        AnchoredContentEvidence proof, Action<long> reserve)
    {
        var count = surfaces.Sum(s => s.Pieces.Sum(p => (p.A is null ? 0 : 1) + (p.B is null ? 0 : 1))) + proof.Causes.Count;
        reserve(checked(64 + 8L * count));
        var spans = new OriginalRowSpan[count]; var index = 0;
        foreach (var s in surfaces)
        foreach (var p in s.Pieces)
        { if (p.A is { } a) spans[index++] = a; if (p.B is { } b) spans[index++] = b; }
        foreach (var cause in proof.Causes) spans[index++] = cause.Span;
        Array.Sort(spans, static (a, b) => a.Page.Side != b.Page.Side ? a.Page.Side.CompareTo(b.Page.Side)
            : a.Page.Page != b.Page.Page ? a.Page.Page.CompareTo(b.Page.Page) : a.Top.CompareTo(b.Top));
        var cursor = 0;
        foreach (var side in new[] { PageSpace.A, PageSpace.B })
        for (var number = 1; number <= 2; number++)
        {
            var page = document.Pages.Single(p => p.Key.Side == side && p.Key.Page == number); var top = 0;
            while (cursor < spans.Length && spans[cursor].Page == page.Key)
            {
                var span = spans[cursor++];
                if (span.Top != top || span.Bottom > page.Size.Height) Reject("source_coverage");
                top = span.Bottom;
            }
            if (top != page.Size.Height) Reject("source_coverage");
        }
        if (cursor != spans.Length) Reject("source_coverage");
    }
}
