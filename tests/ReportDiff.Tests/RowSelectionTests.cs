using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class RowSelectionTests
{
    [Theory]
    [InlineData("R01")]
    [InlineData("R02")]
    [InlineData("R04")]
    [InlineData("R05")]
    [InlineData("R06")]
    [InlineData("R07")]
    public void Pdf_candidates_are_selected_symmetrically_and_keep_content_changes(string id)
    {
        using var input = new Inputs(id);
        var selected = RowSelector.Select(input.A, input.B, input.TextA, input.TextB, new(), new());
        Assert.True(selected.Candidate is not null, id + ": " + selected);
        var reverse = RowSelector.Select(input.B, input.A, input.TextB, input.TextA, new(), new());
        Assert.True(reverse.Candidate is not null, id + " reverse: " + reverse.Reason + "/" + reverse.Detail);
        var map = selected.Candidate!.Layout.DisplayMap;
        Assert.Equal(map.Segments.Select(s => s with { AStart = s.BStart, BStart = s.AStart }), reverse.Candidate!.Layout.DisplayMap.Segments);
        Assert.Equal(selected.Score, reverse.Score);
        using var a = selected.Candidate.Layout.Surface.ContentMap.Render(input.A, PageSpace.A);
        using var b = selected.Candidate.Layout.Surface.ContentMap.Render(input.B, PageSpace.B);
        using var compared = PageComparer.Compare(a, b, new());
        if (id == "R07") Assert.NotEmpty(compared.Clusters);
        else Assert.Equal(id == "R02" ? 1 : 0, compared.Clusters.Count);
        Assert.Equal(id == "R04" ? 2 : 1, selected.Candidate.Layout.Surface.OmittedBands.Count);
        if (id == "R06")
        {
            Assert.Equal(1, selected.Hypotheses);
            var equivalent = Assert.Single(selected.Candidate.EquivalentPositions);
            Assert.True(equivalent.LastStart > equivalent.FirstStart);
            Assert.Equal(100, equivalent.Step);
        }
        if (id == "R05") Assert.Contains(selected.Candidate.Layout.Kinds, k => k == RowBandKind.WhiteSpace);
    }

    [Theory]
    [InlineData("R08", "no_text")]
    [InlineData("R09", "column_conflict")]
    [InlineData("numeric_columns", "column_conflict")]
    public void No_text_and_conflicting_columns_do_not_produce_a_map(string id, string reason)
    {
        using var input = new Inputs(id);
        var result = RowSelector.Select(input.A, input.B, input.TextA, input.TextB, new(), new());
        Assert.Equal(reason, result.Reason); Assert.Null(result.Candidate);
        var reverse = RowSelector.Select(input.B, input.A, input.TextB, input.TextA, new(), new());
        Assert.Equal(reason, reverse.Reason); Assert.Null(reverse.Candidate);
    }

    [Fact]
    public void Word_enumeration_order_does_not_choose_a_different_repeated_position()
    {
        using var input = new Inputs("R06");
        var normal = RowSelector.Select(input.A, input.B, input.TextA, input.TextB, new(), new());
        var shuffled = RowSelector.Select(input.A, input.B, input.TextA with { Words = input.TextA.Words.Reverse().ToArray() },
            input.TextB with { Words = input.TextB.Words.Reverse().ToArray() }, new(), new());
        Assert.Equal("candidate", shuffled.Reason);
        Assert.Equal(normal.Candidate!.Layout.DisplayMap.Segments, shuffled.Candidate!.Layout.DisplayMap.Segments);
        Assert.Equal(normal.Candidate.EquivalentPositions, shuffled.Candidate.EquivalentPositions);
        Assert.Equal(normal.Score, shuffled.Score);
    }

    [Fact]
    public void Equal_words_with_different_fill_do_not_collapse_to_one_equivalent_position()
    {
        using var input = new Inputs("R06");
        Cv2.Rectangle(input.B, new(750, 550, 10, 10), Scalar.All(240), -1);
        var selected = RowSelector.Select(input.A, input.B, input.TextA, input.TextB, new(), new());
        Assert.True(selected.Hypotheses > 1, selected.ToString());
        Assert.False(selected.Candidate is not null && selected.Hypotheses == 1);
    }

    [Fact]
    public void Too_many_non_equivalent_hypotheses_are_not_pruned_to_a_winner()
    {
        const int repetitions = 66; const int width = 80; const int pitch = 100;
        var height = (repetitions + 6) * pitch;
        using var a = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));
        using var b = a.Clone();
        var wordsA = new List<RowWord>(); var wordsB = new List<RowWord>();
        for (var row = 0; row < repetitions + 4; row++)
        {
            var text = row < 2 ? "HEAD" + row : row >= repetitions + 2 ? "FOOT" + row : "SAME";
            var target = row < 2 ? row : row + 1;
            Draw(a, wordsA, row, text, row); Draw(b, wordsB, target, text, row);
        }
        Draw(b, wordsB, 2, "SAME", 200);
        var result = RowSelector.Select(a, b, new("available", null, wordsA), new("available", null, wordsB), new(), new());
        Assert.Equal("resource_limit", result.Reason); Assert.Equal("hypothesis_limit", result.Detail);
        Assert.Null(result.Candidate); Assert.Equal(65, result.Hypotheses);

        static void Draw(Mat image, List<RowWord> words, int row, string text, int tone)
        {
            var y = row * pitch;
            Cv2.Rectangle(image, new(10, y + 20, 30, 30), Scalar.All(0), -1);
            Cv2.Rectangle(image, new(50, y + 60, 10, 10), Scalar.All(tone), -1);
            words.Add(new(text, new(10, y + 20, 60, y + 80), [y + 79]));
        }
    }

    internal sealed class Inputs : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "reportdiff-row-select-" + Guid.NewGuid().ToString("N"));
        public Mat A { get; }
        public Mat B { get; }
        public RowTextResult TextA { get; }
        public RowTextResult TextB { get; }
        public Inputs(string id)
        {
            Directory.CreateDirectory(folder);
            var pa = Path.Combine(folder, "a.pdf"); var pb = Path.Combine(folder, "b.pdf");
            File.WriteAllBytes(pa, PdfFixture.RowScenario(id, false)); File.WriteAllBytes(pb, PdfFixture.RowScenario(id, true));
            using var ra = PdfReader.Open(pa); using var rb = PdfReader.Open(pb);
            using var a = ra.ReadPage(1, 300); using var b = rb.ReadPage(1, 300);
            using var ta = new PdfTextReader(pa); using var tb = new PdfTextReader(pb);
            A = a.Pixels.Clone(); B = b.Pixels.Clone();
            TextA = ta.ReadRowWords(1, A.Size(), 300); TextB = tb.ReadRowWords(1, B.Size(), 300);
        }
        public void Dispose() { A.Dispose(); B.Dispose(); Directory.Delete(folder, true); }
    }
}
