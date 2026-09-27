using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;

// 推定器にはPDFから得た語・画素の記述だけを渡す。生成器・正解ID・正解座標は参照しない。
internal static class CandidateInference
{
    internal sealed record Page(int Number, Size Size, IReadOnlyList<RowLine> Lines, RowTextResult Extraction, bool[] Nonwhite, string[] RowHashes);
    internal sealed record Layout(Page Page, IReadOnlyList<RowLine> Body, int HeaderEnd, int FooterStart,
        int BodyStart, int BodyEnd, int Pitch)
    {
        internal bool Regular => Pitch > 0 && BodyEnd - BodyStart == Body.Count * Pitch
            && Body.Select((line, i) => line.Bounds.Top >= BodyStart + i * Pitch
                && line.Bounds.Bottom <= BodyStart + (i + 1) * Pitch).All(x => x);
    }
    internal sealed record Prepared(string Status, string? Reason, IReadOnlyList<Layout> A, IReadOnlyList<Layout> B);
    internal sealed record Band(string Side, int Page, int Start, int Length);
    internal sealed record Proposal(Band Source, Band Target, string[] Text, int Support, string Status, string? Reason);
    internal sealed record Inference(Prepared Layouts, IReadOnlyList<Proposal> Proposals);

    internal static Page Describe(int number, Mat image, RowTextResult extraction)
    {
        var lines = extraction.Status == "available"
            ? TextLineLayout.Lines(extraction.Words, new TextOptions().MinLineOverlap) : [];
        var height = image.Height;
        var nonwhite = new bool[height];
        var hashes = new string[height];
        var bytes = new byte[image.Width * 3];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(image.Ptr(y), bytes, 0, bytes.Length);
            nonwhite[y] = bytes.Any(b => b != 255);
            hashes[y] = Convert.ToHexString(SHA256.HashData(bytes));
        }
        return new(number, image.Size(), lines, extraction, nonwhite, hashes);
    }

    internal static Inference Find(IReadOnlyList<Page> a, IReadOnlyList<Page> b)
    {
        var prepared = Prepare(a, b);
        if (prepared.Status != "prepared") return new(prepared, []);
        var proposals = new List<Proposal>();
        Direction(prepared.A, prepared.B, "a", "b");
        Direction(prepared.B, prepared.A, "b", "a");
        return new(prepared, proposals);

        void Direction(IReadOnlyList<Layout> source, IReadOnlyList<Layout> target, string sourceSide, string targetSide)
        {
            var countSource = Counts(source); var countTarget = Counts(target);
            foreach (var s in source)
            {
                var same = target.SingleOrDefault(p => p.Page.Number == s.Page.Number);
                var next = target.SingleOrDefault(p => p.Page.Number == s.Page.Number + 1);
                if (same is null || next is null) continue;
                var shifts = s.Body.Where(l => countSource[Text(l)] == 1 && countTarget.GetValueOrDefault(Text(l)) == 1)
                    .Select(l => (Source: l, Target: same.Body.SingleOrDefault(t => Text(t) == Text(l))))
                    .Where(p => p.Target is not null && Math.Round(p.Target.Bounds.Left - p.Source.Bounds.Left) == 0)
                    .Select(p => (int)Math.Round(p.Target!.Baseline - p.Source.Baseline))
                    .Where(dy => dy > 0 && dy <= Units.RoundPixels(new RowOptions().MaxShiftMm, 300))
                    .GroupBy(dy => dy).Where(g => g.Count() >= new RowOptions().MinSupportBands).ToArray();
                if (shifts.Length == 0) continue;
                if (shifts.Length != 1)
                {
                    proposals.Add(new(new(sourceSide, s.Page.Number, 0, 0), new(targetSide, next.Page.Number, 0, 0), [], 0,
                        "skipped", "ambiguous_displacement"));
                    continue;
                }
                var length = shifts[0].Key;
                var from = new Band(sourceSide, s.Page.Number, s.BodyEnd - length, length);
                var to = new Band(targetSide, next.Page.Number, next.BodyStart, length);
                string? reason = null;
                if (!s.Regular || length % s.Pitch != 0 || length >= s.BodyEnd - s.BodyStart
                    || to.Start + length > next.FooterStart) reason = "unproven_body_bounds";
                var linesA = Within(s, from); var linesB = Within(next, to);
                var textA = linesA.Select(Text).ToArray(); var textB = linesB.Select(Text).ToArray();
                if (reason is null && (Crosses(s, from) || Crosses(next, to) || linesA.Length == 0 || linesB.Length == 0))
                    reason = "cut_crosses_text";
                if (reason is null && (textA.Any(t => countSource[t] != 1) || textB.Any(t => countTarget[t] != 1)))
                    reason = "repeated_body_text";
                if (reason is null && !textA.SequenceEqual(textB)) reason = "text_mismatch";
                proposals.Add(new(from, to, textA, shifts[0].Count(), reason is null ? "candidate" : "skipped", reason));
            }
        }
    }

    private static Prepared Prepare(IReadOnlyList<Page> a, IReadOnlyList<Page> b)
    {
        if (a.Count > 16 || b.Count > 16) return new("skipped", "probe_page_limit", [], []);
        if (a.Concat(b).Any(p => p.Extraction.Status != "available")) return new("skipped", "text_unavailable", [], []);
        var shared = a.Select(p => p.Number).Intersect(b.Select(p => p.Number)).ToHashSet();
        if (shared.Count < 2) return new("skipped", "fixed_parts_support", [], []);
        var paired = a.Concat(b).Where(p => shared.Contains(p.Number)).ToArray();
        var size = paired[0].Size;
        if (a.Concat(b).Any(p => p.Size != size)) return new("skipped", "size_mismatch", [], []);
        var fixedKeys = paired[0].Lines.Select(Key).ToHashSet();
        foreach (var page in paired.Skip(1)) fixedKeys.IntersectWith(page.Lines.Select(Key));
        var headerCount = paired.Min(p => p.Lines.TakeWhile(l => fixedKeys.Contains(Key(l))).Count());
        var footerCount = paired.Min(p => p.Lines.Reverse().TakeWhile(l => fixedKeys.Contains(Key(l))).Count());
        if (headerCount < 2 || footerCount < 2 || paired.Any(p => headerCount + footerCount >= p.Lines.Count))
            return new("skipped", "fixed_parts_support", [], []);
        var first = paired[0];
        var header = first.Lines.Take(headerCount).Select(Key).ToArray();
        var footer = first.Lines.TakeLast(footerCount).Select(Key).ToArray();
        var headerEnd = (int)Math.Ceiling(first.Lines[headerCount - 1].Bounds.Bottom);
        var footerStart = (int)Math.Floor(first.Lines[^footerCount].Bounds.Top);
        foreach (var page in a.Concat(b))
        {
            if (!page.Lines.Take(headerCount).Select(Key).SequenceEqual(header)
                || !page.Lines.TakeLast(footerCount).Select(Key).SequenceEqual(footer))
                return new("skipped", "fixed_parts_mismatch", [], []);
            if (!page.RowHashes.Take(headerEnd).SequenceEqual(first.RowHashes.Take(headerEnd))
                || !page.RowHashes.Skip(footerStart).SequenceEqual(first.RowHashes.Skip(footerStart)))
                return new("skipped", "fixed_parts_pixels", [], []);
        }
        var bodyByPage = a.Concat(b).ToDictionary(p => p, p => p.Lines.Skip(headerCount).SkipLast(footerCount).ToArray());
        var pitches = bodyByPage.Values.SelectMany(lines => lines.Zip(lines.Skip(1), (x, y) =>
            (int)Math.Round(y.Baseline - x.Baseline))).Distinct().ToArray();
        if (pitches.Length != 1 || pitches[0] <= 0) return new("skipped", "nonuniform_row_pitch", [], []);
        var layouts = new Dictionary<Page, Layout>();
        foreach (var (page, lines) in bodyByPage)
        {
            var ink = Enumerable.Range(headerEnd, footerStart - headerEnd).Where(y => page.Nonwhite[y]).ToArray();
            if (lines.Length == 0 || ink.Length == 0) return new("skipped", "empty_body", [], []);
            layouts[page] = new(page, lines, headerEnd, footerStart, ink[0], ink[^1] + 1, pitches[0]);
        }
        return new("prepared", null, a.Select(p => layouts[p]).ToArray(), b.Select(p => layouts[p]).ToArray());
    }

    internal static string Text(RowLine line) => string.Join(' ', line.Words.Select(w => w.Text));
    private static string Key(RowLine line) => $"{Text(line)}|{Math.Round(line.Baseline)}|{Math.Round(line.Bounds.Left)}|{Math.Round(line.Bounds.Right)}";
    private static Dictionary<string, int> Counts(IReadOnlyList<Layout> pages) => pages.SelectMany(p => p.Body)
        .GroupBy(Text).ToDictionary(g => g.Key, g => g.Count());
    private static RowLine[] Within(Layout page, Band band) => page.Body.Where(l => l.Bounds.Top >= band.Start
        && l.Bounds.Bottom <= band.Start + band.Length).ToArray();
    private static bool Crosses(Layout page, Band band) => page.Page.Lines.SelectMany(l => l.Words)
        .Any(w => (w.Bounds.Top < band.Start && w.Bounds.Bottom > band.Start)
            || (w.Bounds.Top < band.Start + band.Length && w.Bounds.Bottom > band.Start + band.Length));
}
