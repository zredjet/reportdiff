using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
public sealed class RowStructureTests
{
    [Theory]
    [InlineData("R01", false, 2)]
    [InlineData("R01", true, 2)]
    [InlineData("R02", false, 3)]
    [InlineData("R02", true, 3)]
    [InlineData("R04", false, 4)]
    [InlineData("R04", true, 4)]
    [InlineData("R05", false, 2)]
    [InlineData("R05", true, 2)]
    [InlineData("R06", false, 2)]
    [InlineData("R06", true, 2)]
    [InlineData("R07", false, 6)]
    [InlineData("R07", true, 6)]
    public void Adopted_pdf_counts_content_and_structures_once(string id, bool reverse, int expected)
    {
        using var input = new RowSelectionTests.Inputs(id);
        var a = reverse ? input.B : input.A; var b = reverse ? input.A : input.B;
        using var result = RowComparer.Compare(a, b, new(), new() { Enabled = true },
            () => reverse ? (input.TextB, input.TextA) : (input.TextA, input.TextB));
        Assert.Equal("applied", result.Decision.Status); Assert.NotNull(result.Display);
        Assert.Equal("different", result.Status); Assert.Equal(expected, result.DifferenceCount);
        Assert.True(result.Display.DifferenceCountComplete);
        var structures = result.Display.StructuralChanges;
        Assert.Equal(id == "R04" ? 4 : 2, structures.Count);
        Assert.Equal(Enumerable.Range(1, structures.Count), structures.Select(c => c.Id));
        var operations = structures.Where(c => c.Kind == (reverse ? "deleted" : "inserted")).ToArray();
        Assert.Equal(id == "R04" ? 2 : 1, operations.Length);
        Assert.All(operations, c =>
        {
            Assert.Equal(100, c.DisplayBounds.Height); Assert.Null(c.DisplacementPx); Assert.False(c.Excluded);
            if (reverse) { Assert.NotNull(c.SourceA); Assert.Null(c.SourceB); }
            else { Assert.Null(c.SourceA); Assert.NotNull(c.SourceB); }
        });
        var moves = structures.Where(c => c.Kind == "block_moved").ToArray();
        Assert.Equal(id == "R04" ? [100, 200] : [100], moves.Select(c => Math.Abs(c.DisplacementPx!.Dy)));
        Assert.All(moves, c =>
        {
            Assert.Equal(reverse ? -1 : 1, Math.Sign(c.DisplacementPx!.Dy));
            Assert.NotNull(c.SourceA); Assert.NotNull(c.SourceB); Assert.False(c.Excluded);
        });
        if (id == "R05") Assert.True(result.Display.OmissionAudit.PreservedWhitePixels > 0);
        if (id == "R06")
        {
            var position = Assert.Single(Assert.Single(operations).EquivalentPositions);
            Assert.True(position.LastStart > position.FirstStart); Assert.Equal(100, position.Step);
        }
        Assert.True(result.Display.OmissionAudit.OmittedContentPixels > 0);
        Assert.Null(result.Display.OmissionAudit.OmittedCandidateRawPixels);
        Assert.Equal("not_compared_in_content_canvas", result.Display.OmissionAudit.RawCountReason);
        if (id == "R02" && !reverse && Environment.GetEnvironmentVariable("REPORTDIFF_ROW_PREVIEW_DIR") is { } folder)
        {
            Directory.CreateDirectory(folder);
            using var db = result.Surface!.DisplayMap.Render(b, PageSpace.B);
            using var overlay = ReportImages.RowOverlay(db, result.Display, 300);
            File.WriteAllBytes(Path.Combine(folder, "r02-overlay.png"), overlay.ImEncode(".png"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Adjacent_bands_with_the_same_operation_or_displacement_count_once(bool reverse)
    {
        using var a = new Mat(160, 80, MatType.CV_8UC3, Scalar.All(128));
        using var b = new Mat(200, 80, MatType.CV_8UC3, Scalar.All(128));
        var segments = new PageSegment[] { new(0, 40, 0, 0), new(40, 20, null, 40), new(60, 20, null, 60),
            new(80, 40, 40, 80), new(120, 80, 80, 120) };
        if (reverse) segments = segments.Select(s => s with { AStart = s.BStart, BStart = s.AStart }).ToArray();
        var aa = reverse ? b : a; var bb = reverse ? a : b;
        var surface = RowComparisonSurface.Create(aa, bb, new(aa.Size(), bb.Size(), b.Size(), segments),
            [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Structural, RowBandKind.Paired, RowBandKind.Paired], new()).Surface!;
        using var raw = new RawDifference(RowProjectionTests.Mask(surface.ContentMap.CanvasSize), 0, 0);
        using var content = PageComparer.Cluster(raw, new(), retainProjection: true);
        using var display = RowProjection.Create(content, surface, aa, bb, new());
        Assert.Equal(2, display.DifferenceCount);
        Assert.Equal([reverse ? "deleted" : "inserted", "block_moved"], display.StructuralChanges.Select(c => c.Kind));
        Assert.Equal([2, 2], display.StructuralChanges.Select(c => c.BandCount));
        Assert.Equal([new Rect(0, 40, 80, 40), new Rect(0, 80, 80, 120)], display.StructuralChanges.Select(c => c.DisplayBounds));
        Assert.Equal(new MovementShift(0, reverse ? -40 : 40), display.StructuralChanges[1].DisplacementPx);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_full_content_exclusion_removes_a_structure_from_the_count(bool partial)
    {
        using var images = new RowProjectionTests.Images(200, 80, 80, 40, white: true);
        Cv2.Rectangle(images.A, new(10, 120, 10, 5), Scalar.All(0), -1); images.CopyContentB(images.A);
        // 黒い内容は除外内でも、1チャンネルだけ254の画素が外なら部分除外。
        Cv2.Rectangle(images.B, new(0, 80, 80, 40), Scalar.All(255), -1);
        Cv2.Rectangle(images.B, new(10, 85, 20, 10), Scalar.All(0), -1);
        images.B.Set(100, 79, new Vec3b(255, 254, 255));
        var p = new ComparisonParameters { Exclude = [PageMap.CanvasMillimeters(new(0, 79, partial ? 79 : 80, 121), 300)] };
        using var raw = new RawDifference(RowProjectionTests.Mask(images.A.Size()), 0, 0);
        using var content = PageComparer.Cluster(raw, p, retainProjection: true);
        using var display = RowProjection.Create(content, images.Surface, images.A, images.B, p);
        Assert.Equal(partial ? "different" : "same", display.Comparison.Status);
        Assert.Equal(partial ? 1 : 0, display.DifferenceCount);
        var insertion = display.StructuralChanges[0]; Assert.Equal(!partial, insertion.Excluded);
        Assert.Equal(partial ? null : "all_content_excluded", insertion.ExclusionReason);
        Assert.True(display.StructuralChanges[1].Excluded);
        Assert.Equal(3200, display.OmissionAudit.OmittedBandPixels); Assert.Equal(201, display.OmissionAudit.OmittedContentPixels);
        Assert.Equal(partial ? 200 : 201, display.OmissionAudit.UserExcludedOmittedContentPixels);
    }

    [Fact]
    public void Movement_exclusion_requires_the_content_on_both_sides()
    {
        using var images = new RowProjectionTests.Images(200, 80, 80, 40, white: true);
        Cv2.Rectangle(images.A, new(10, 120, 5, 5), Scalar.All(0), -1); images.CopyContentB(images.A);
        images.B.Set(160, 40, new Vec3b(254, 255, 255));
        var p = new ComparisonParameters { Exclude = [PageMap.CanvasMillimeters(new(0, 79, 20, 121), 300)] };
        using var raw = new RawDifference(RowProjectionTests.Mask(images.A.Size()), 0, 0);
        using var content = PageComparer.Cluster(raw, p, retainProjection: true);
        using var display = RowProjection.Create(content, images.Surface, images.A, images.B, p);
        var moved = Assert.Single(display.StructuralChanges, c => c.Kind == "block_moved");
        Assert.Equal(25, moved.ContentPixelsA); Assert.Equal(26, moved.ContentPixelsB);
        Assert.Equal(25, moved.ExcludedContentPixelsA); Assert.Equal(25, moved.ExcludedContentPixelsB);
        Assert.False(moved.Excluded);
    }

    [Fact]
    public void White_paired_rows_do_not_create_a_block_move()
    {
        using var images = new RowProjectionTests.Images(200, 80, 80, 40, white: true);
        using var raw = new RawDifference(RowProjectionTests.Mask(images.A.Size()), 0, 0);
        using var content = PageComparer.Cluster(raw, new(), retainProjection: true);
        using var display = RowProjection.Create(content, images.Surface, images.A, images.B, new());
        Assert.Equal("inserted", Assert.Single(display.StructuralChanges).Kind); Assert.Equal(1, display.DifferenceCount);
    }

    [Fact]
    public void A_region_wholly_in_a_deleted_band_keeps_display_bounds_and_empty_content_audit()
    {
        using var images = new RowProjectionTests.Images(200, 80, 80, 40, white: true);
        var bands = images.Surface.DisplayMap.Segments.Select(s => s with { AStart = s.BStart, BStart = s.AStart });
        var surface = RowComparisonSurface.Create(images.B, images.A, new(images.B.Size(), images.A.Size(), images.B.Size(), bands),
            [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired], new()).Surface!;
        var bounds = PageMap.CanvasMillimeters(new(0, 80, 80, 40), 300);
        var p = new ComparisonParameters { Regions = [new(7, "削除行", bounds, "exclude", new())] };
        using var a = surface.ContentMap.Render(images.B, PageSpace.A); using var b = surface.ContentMap.Render(images.A, PageSpace.B);
        using var content = RegionalComparer.Compare(a, b, p, RegionMap.ForRows(surface, p), retainProjection: true);
        using var display = RowProjection.Create(content, surface, images.B, images.A, p);
        Assert.Equal("same", display.Comparison.Status); Assert.Equal(0, display.DifferenceCount);
        var region = Assert.Single(display.Comparison.Regional!.Regions);
        Assert.Equal(7, region.Index); Assert.Equal("omitted_from_content", region.Status); Assert.Equal(0, region.RawPixels);
        var geometry = Assert.Single(display.Regions);
        Assert.Equal(bounds, geometry.SourceBounds); Assert.Equal(new Rect(0, 80, 80, 40), geometry.DisplayBounds);
        Assert.Equal(new Rect(), geometry.ContentBounds); Assert.Equal("outside_page", geometry.ContentStatus);
        Assert.True(Assert.Single(display.StructuralChanges).Excluded);
    }

    [Fact]
    public void Touching_exclusions_do_not_fill_the_inserted_gap()
    {
        using var images = new RowProjectionTests.Images(200, 80, 80, 40, white: true);
        var p = new ComparisonParameters { Exclude = [PageMap.CanvasMillimeters(new(0, 0, 80, 80), 300),
            PageMap.CanvasMillimeters(new(0, 80, 80, 120), 300)] };
        var settings = RegionMap.ForRowDisplay(images.Surface.DisplayMap, p);
        Assert.All(settings.Excluded.Take(80 * 80), Assert.True);
        Assert.All(settings.Excluded.Skip(80 * 80).Take(40 * 80), Assert.False);
        Assert.All(settings.Excluded.Skip(120 * 80), Assert.True);
    }

    [Fact]
    public void Source_coordinates_compose_global_alignment_and_preserve_missing_sides()
    {
        using var a = new Mat(140, 20, MatType.CV_8UC3, Scalar.All(255)); using var b = a.Clone();
        Cv2.Rectangle(b, new(0, 40, 20, 40), Scalar.All(128), -1);
        var surface = RowComparisonSurface.Create(a, b, new(a.Size(), b.Size(), new(20, 180),
            [new(0, 40, 0, 0), new(40, 40, null, 40), new(80, 60, 40, 80), new(140, 40, 100, null)]),
            [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired, RowBandKind.WhiteSpace], new()).Surface!;
        using var mask = RowProjectionTests.Mask(surface.ContentMap.CanvasSize);
        Cv2.Rectangle(mask, new(5, 39, 2, 3), Scalar.All(255), -1);
        Cv2.Rectangle(mask, new(6, 110, 2, 3), Scalar.All(255), -1);
        Cv2.Rectangle(mask, new(0, 10, 2, 3), Scalar.All(255), -1);
        var p = new ComparisonParameters { Cluster = new() { MergeXMm = 0, MergeYMm = 0 } };
        using var raw = new RawDifference(mask.Clone(), 0, 0);
        using var content = PageComparer.Cluster(raw, p, retainProjection: true);
        using var display = RowProjection.Create(content, surface, a, b, p, globalMap: PageMap.Global(a.Size(), b.Size(), a.Size(), new(2, -3)));
        var split = Assert.Single(display.Comparison.Clusters, c => c.Row!.ContentBounds.Y == 39).Row!;
        Assert.Equal([new PageBounds(3, 42, 5, 43), new PageBounds(3, 83, 5, 85)], split.SourceB!.Parts);
        Assert.Equal(new PageBounds(3, 42, 5, 85), split.SourceB.Bounds);
        Assert.Equal(new PageBounds(5, 39, 7, 42), split.SourceA!.Bounds);
        Assert.Null(Assert.Single(display.Comparison.Clusters, c => c.Row!.ContentBounds.Y == 110).Row!.SourceB);
        Assert.Null(Assert.Single(display.Comparison.Clusters, c => c.Row!.ContentBounds.Y == 10).Row!.SourceB);
        var insertion = Assert.Single(display.StructuralChanges);
        Assert.Null(insertion.SourceA); Assert.Equal(new PageBounds(0, 43, 18, 83), insertion.SourceB!.Bounds);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Excluded_inserted_text_still_needs_external_support_and_unambiguous_cuts(bool reverse, bool ruled, bool partial)
    {
        using var a = new Mat(1200, 160, MatType.CV_8UC3, Scalar.All(255)); using var b = a.Clone();
        if (ruled)
        {
            // 罫線なしの疎な配置では、同じdyで純白帯を外す別の切断も成立する。
            // 連続罫線のあるケースを別に用意し、非白の除外帯だけが成立する条件を固定する。
            Cv2.Line(a, new(100, 0), new(100, 1099), Scalar.All(0));
            Cv2.Line(b, new(100, 0), new(100, 1199), Scalar.All(0));
        }
        var wordsA = new List<RowWord>(); var wordsB = new List<RowWord>();
        foreach (var y in new[] { 80, 140, 800, 900 })
        { Draw(a, wordsA, y, "ROW" + y); Draw(b, wordsB, y < 400 ? y : y + 100, "ROW" + y); }
        Draw(b, wordsB, 450, "INSERT");
        if (partial) b.Set(455, 159, new Vec3b(255, 254, 255));
        var exclusion = PageMap.CanvasMillimeters(new(0, 430, partial ? 159 : 160, reverse ? 120 : 20), 300);
        var p = new ComparisonParameters { Exclude = [exclusion] };
        var textA = new RowTextResult("available", null, wordsA); var textB = new RowTextResult("available", null, wordsB);
        using var result = RowComparer.Compare(reverse ? b : a, reverse ? a : b, p, new() { Enabled = true },
            () => reverse ? (textB, textA) : (textA, textB));
        if (!ruled)
        {
            Assert.Null(result.Display); Assert.Equal("ambiguous", result.Decision.Reason);
            Assert.Equal("non_equivalent_safe_cuts", result.Decision.Detail);
            return;
        }
        if (partial)
        {
            Assert.Null(result.Display); Assert.Equal("skipped", result.Decision.Status);
            Assert.True(result.Comparison.RawPixels > 0);
            return;
        }
        Assert.True(result.Decision.Status == "applied", result.Decision.ToString());
        var structure = Assert.Single(result.Display!.StructuralChanges, c => c.Kind == (reverse ? "deleted" : "inserted"));
        Assert.True(structure.Excluded); Assert.Equal("all_content_excluded", structure.ExclusionReason);
        Assert.Equal(1, result.DifferenceCount); Assert.Equal("different", result.Status); // 行送りは未除外
        Assert.All(result.Decision.Selection!.SupportBands!, count => Assert.True(count >= 2));
        var allExcluded = p with { Exclude = [new(0, 0, 100, 200)] };
        var unsupported = RowSelector.Select(a, b, textA, textB, allExcluded, new());
        Assert.Null(unsupported.Candidate); Assert.Equal("insufficient_support", unsupported.Reason);

        static void Draw(Mat target, List<RowWord> words, int y, string text)
        {
            Cv2.Rectangle(target, new(10, y, 60, 20), Scalar.All(0), -1);
            words.Add(new(text, new(10, y, 70, y + 20), [y + 19]));
        }
    }
}
