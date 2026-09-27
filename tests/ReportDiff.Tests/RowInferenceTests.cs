using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class RowInferenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Insertion_and_number_change_are_recovered_from_pdf_baselines(bool changed, bool reverse)
    {
        var folder = Path.Combine(Path.GetTempPath(), "reportdiff-rows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var oldPath = Path.Combine(folder, "前.pdf"); var newPath = Path.Combine(folder, "後.pdf");
            File.WriteAllBytes(oldPath, PdfFixture.RowProbePage(false, false, 1.44));
            File.WriteAllBytes(newPath, PdfFixture.RowProbePage(true, changed, 1.44));
            var aPath = reverse ? newPath : oldPath; var bPath = reverse ? oldPath : newPath;
            using var aPdf = PdfReader.Open(aPath); using var bPdf = PdfReader.Open(bPath);
            using var a = aPdf.ReadPage(1, 300); using var b = bPdf.ReadPage(1, 300);
            using var aText = new PdfTextReader(aPath); using var bText = new PdfTextReader(bPath);
            var wordsA = aText.ReadRowWords(1, a.Pixels.Size(), 300); var wordsB = bText.ReadRowWords(1, b.Pixels.Size(), 300);
            Assert.Equal("available", wordsA.Status); Assert.Equal("available", wordsB.Status);
            var linesA = TextLineLayout.Lines(wordsA.Words, 0.5); var linesB = TextLineLayout.Lines(wordsB.Words, 0.5);
            Assert.Equal(reverse ? 8 : 7, linesA.Count); Assert.Equal(reverse ? 7 : 8, linesB.Count);
            var paths = RowMatching.Find(linesA, linesB, new(), 300);
            Assert.Null(paths.Detail); Assert.Single(paths.Paths);
            var candidate = RowCandidateBuilder.Build(a.Pixels, b.Pixels, linesA, linesB, paths.Paths[0], new(), new());
            Assert.True(candidate.Candidate is not null, candidate.Reason + ": " + candidate.Detail);
            var surface = candidate.Candidate!.Surface;
            using var ca = surface.ContentMap.Render(a.Pixels, PageSpace.A); using var cb = surface.ContentMap.Render(b.Pixels, PageSpace.B);
            using var result = PageComparer.Compare(ca, cb, new());
            Assert.Equal(changed ? 1 : 0, result.Clusters.Count);
            Assert.Equal(changed ? "different" : "same", result.Status);
            Assert.Single(surface.OmittedBands);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Word_matching_keeps_digits_case_and_unicode_forms_distinct()
    {
        RowWord W(string text) => new(text, new(0, 0, 20, 10), [8]);
        Assert.Equal(2.0 / 3, RowMatching.WordMatch([W("ITEM"), W("ALPHA"), W("01")], [W("ITEM"), W("ALPHA"), W("02")]));
        Assert.Equal(0, RowMatching.WordMatch([W("A")], [W("a")]));
        Assert.Equal(0, RowMatching.WordMatch([W("1")], [W("１")]));
        Assert.Equal(0.5, RowMatching.WordMatch([W("X"), W("X")], [W("X"), W("Y")]));
    }

    [Fact]
    public void Line_baseline_is_letter_median_and_lines_do_not_chain_overlap()
    {
        var words = new[] { new RowWord(" A\u0001 \t", new(0, 0, 10, 10), [3, 4, 5]),
            new RowWord("B", new(20, 4, 30, 14), [9]), new RowWord("C", new(40, 8, 50, 18), [15]) };
        var lines = TextLineLayout.Lines(words, 0.5);
        Assert.Equal(2, lines.Count); Assert.Equal(4.5, lines[0].Baseline); Assert.Equal("A", lines[0].Words[0].Text);
        Assert.Equal("C", lines[1].Words[0].Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Unavailable_pdf_text_never_supplies_partial_alignment_words(int scenario)
    {
        var path = Path.Combine(Path.GetTempPath(), "reportdiff-row-text-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, PdfFixture.CreateTextPage([new("ALPHA BETA", 40, 220)],
            rotation: scenario == 0 ? 90 : 0, userUnit: scenario == 1 ? 2 : 1));
        try
        {
            var options = scenario switch { 2 => new TextOptions { MaxLettersPerPage = 1 },
                3 => new TextOptions { MaxWordsPerPage = 1 }, _ => new TextOptions() };
            using var reader = new PdfTextReader(path, options);
            var result = reader.ReadRowWords(1, scenario == 4 ? new(10, 10) : new(240, 300), 72);
            Assert.Equal("text_unavailable", result.Status); Assert.Empty(result.Words);
            Assert.Equal(new[] { "rotated_page", "user_unit", "letter_limit", "word_limit", "page_coordinates" }[scenario], result.Detail);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Empty_text_and_parser_failure_remain_distinct()
    {
        using var missing = new PdfTextReader("存在しない秘密の入力.pdf");
        var unavailable = missing.ReadRowWords(1, new(240, 300), 72);
        Assert.Equal("text_unavailable", unavailable.Status); Assert.Equal("open_failed", unavailable.Detail);
        Assert.DoesNotContain("秘密", unavailable.Detail);
        var path = Path.Combine(Path.GetTempPath(), "reportdiff-no-rows-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, PdfFixture.CreatePages((240, 300)));
        try
        {
            using var reader = new PdfTextReader(path);
            var empty = reader.ReadRowWords(1, new(240, 300), 72);
            Assert.Equal("no_text", empty.Status); Assert.Empty(empty.Words);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Repeated_line_lcs_lists_distinct_matches_without_skip_order_duplicates()
    {
        RowLine L(int index) => new([new("SAME", new(0, index * 10, 30, index * 10 + 5), [index * 10 + 4])],
            new(0, index * 10, 30, index * 10 + 5), index * 10 + 4);
        var paths = RowMatching.Find([L(0), L(1)], [L(0), L(1), L(2)], new(), 300);
        Assert.Null(paths.Detail); Assert.Equal(3, paths.Paths.Count);
        Assert.All(paths.Paths, p => Assert.Equal(2, p.Count));
        Assert.Equal(3, paths.Paths.Select(p => string.Join(',', p.Select(m => m.B))).Distinct().Count());
    }

    [Fact]
    public void Dp_limits_are_checked_before_allocating_or_dropping_competing_hypotheses()
    {
        var word = new RowWord("X", new(0, 0, 10, 10), [8]);
        var line = new RowLine([word], word.Bounds, 8);
        var rows = Enumerable.Repeat(line, 2000).ToArray();
        Assert.Equal("row_dp_limit", RowMatching.Find(rows, rows, new(), 300).Detail);
        var manyWords = line with { Words = Enumerable.Repeat(word, 3000).ToArray() };
        Assert.Equal("word_dp_limit", RowMatching.Find([manyWords], [manyWords], new(), 300).Detail);
        var repeated = RowMatching.Find(Enumerable.Repeat(line, 8).ToArray(), Enumerable.Repeat(line, 16).ToArray(), new(), 300);
        Assert.Equal("hypothesis_trace_limit", repeated.Detail); Assert.Empty(repeated.Paths);
    }

    [Theory]
    [InlineData("max_shift_mm")]
    [InlineData("min_word_match")]
    [InlineData("refine_mm")]
    [InlineData("min_improvement")]
    [InlineData("min_score_gap")]
    [InlineData("min_support_bands")]
    [InlineData("min_support_ink_mm2")]
    [InlineData("max_segments")]
    public void Invalid_row_settings_identify_their_key(string key)
    {
        var options = key switch
        {
            "max_shift_mm" => new RowOptions { MaxShiftMm = 0 },
            "min_word_match" => new() { MinWordMatch = double.NaN },
            "refine_mm" => new() { RefineMm = 2.01 },
            "min_improvement" => new() { MinImprovement = 0 },
            "min_score_gap" => new() { MinScoreGap = double.PositiveInfinity },
            "min_support_bands" => new() { MinSupportBands = 1 },
            "min_support_ink_mm2" => new() { MinSupportInkMm2 = -1 },
            _ => new() { MaxSegments = 65 }
        };
        Assert.Contains("rows." + key, Assert.Throws<ArgumentException>(() => options.Validated(300)).Message);
        Assert.NotNull(new RowOptions().Validated(72));
        Assert.NotNull(new RowOptions { MaxShiftMm = 100, RefineMm = 2, MinSupportInkMm2 = 1000,
            MinSupportBands = 100, MaxSegments = 64 }.Validated(1200));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Different_safe_cuts_cannot_hide_a_gray_rule_change(bool reverse)
    {
        var folder = Path.Combine(Path.GetTempPath(), "reportdiff-row-cuts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var oldPath = Path.Combine(folder, "a.pdf"); var newPath = Path.Combine(folder, "b.pdf");
            File.WriteAllBytes(oldPath, PdfFixture.RowProbePage(false, false, 0, mappedTone: new(false)));
            File.WriteAllBytes(newPath, PdfFixture.RowProbePage(true, false, 0, mappedTone: new(true)));
            var aPath = reverse ? newPath : oldPath; var bPath = reverse ? oldPath : newPath;
            using var aPdf = PdfReader.Open(aPath); using var bPdf = PdfReader.Open(bPath);
            using var a = aPdf.ReadPage(1, 300); using var b = bPdf.ReadPage(1, 300);
            using var ta = new PdfTextReader(aPath); using var tb = new PdfTextReader(bPath);
            var la = TextLineLayout.Lines(ta.ReadRowWords(1, a.Pixels.Size(), 300).Words, 0.5);
            var lb = TextLineLayout.Lines(tb.ReadRowWords(1, b.Pixels.Size(), 300).Words, 0.5);
            var paths = RowMatching.Find(la, lb, new(), 300);
            var path = Assert.Single(paths.Paths);
            var attempt = RowCandidateBuilder.Build(a.Pixels, b.Pixels, la, lb, path, new(), new());
            Assert.Null(attempt.Candidate);
            Assert.Equal("ambiguous", attempt.Reason);
            Assert.Equal("non_equivalent_safe_cuts", attempt.Detail);
            using var baseline = PageComparer.Compare(a.Pixels, b.Pixels, new());
            Assert.NotEmpty(baseline.Clusters);
        }
        finally { Directory.Delete(folder, true); }
    }
}
