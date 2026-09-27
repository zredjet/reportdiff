using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class RowComparisonSurfaceTests
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "row-surfaces");
    private static JsonElement[] Cases()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "cases.json")));
        return json.RootElement.EnumerateArray().Select(j => j.Clone()).ToArray();
    }
    public static IEnumerable<object[]> CaseIds => Cases().Select(j => new object[] { j.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Fixed_reference_inputs_masks_counts_and_bidirectional_maps_are_preserved(string id)
    {
        var j = Cases().Single(j => j.GetProperty("id").GetString() == id);
        using var a = Read("a"); using var b = Read("b");
        using var expectedA = Read("baseline-a"); using var expectedB = Read("baseline-b");
        var hashA = Hash(a); var hashB = Hash(b);
        var bands = j.GetProperty("bands").EnumerateArray().Select(s => new PageSegment(s[0].GetInt32(), s[1].GetInt32(),
            s[2].ValueKind == JsonValueKind.Null ? null : s[2].GetInt32(),
            s[3].ValueKind == JsonValueKind.Null ? null : s[3].GetInt32())).ToArray();
        var kinds = j.GetProperty("roles").EnumerateArray().Select(s => Enum.Parse<RowBandKind>(s.GetString()!)).ToArray();
        var map = new PageMap(a.Size(), b.Size(), new(a.Width, bands.Sum(s => s.Length)), bands);
        var parameters = GoldenData.Parameters(j);
        var build = RowComparisonSurface.Create(a, b, map, kinds, parameters);
        Assert.True(build.Success, id + ": " + build.Detail);
        var surface = build.Surface!;
        using var ca = surface.ContentMap.Render(a, PageSpace.A); using var cb = surface.ContentMap.Render(b, PageSpace.B);
        AssertImage(expectedA, ca); AssertImage(expectedB, cb);
        var reverse = j.GetProperty("reverse").GetBoolean();
        using var result = PageComparer.Compare(reverse ? cb : ca, reverse ? ca : cb, parameters);
        Assert.Equal(j.GetProperty("raw_sha256").GetString(), Hash(result.RawMask));
        var expected = j.GetProperty("expected");
        Assert.Equal(expected.GetProperty("status").GetString(), result.Status);
        Assert.Equal(expected.GetProperty("raw_pixels").GetInt32(), result.RawPixels);
        Assert.Equal(expected.GetProperty("noise_dropped").GetInt32(), result.NoiseDropped);
        Assert.Equal(expected.GetProperty("absorbed_groups").GetInt32(), result.AbsorbedGroups);
        Assert.Equal(expected.GetProperty("max_shift_px").GetInt32(), result.MaxShiftPx);
        var clusters = expected.GetProperty("clusters").EnumerateArray().ToArray();
        Assert.Equal(clusters.Length, result.Clusters.Count);
        for (var i = 0; i < clusters.Length; i++)
        {
            var e = clusters[i]; var c = result.Clusters[i];
            Assert.Equal(new Rect(e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32(),
                e.GetProperty("w").GetInt32(), e.GetProperty("h").GetInt32()), c.Bounds);
            Assert.Equal(e.GetProperty("pixels").GetInt32(), c.Pixels);
        }
        using var displayRaw = surface.ToDisplay(result.RawMask);
        Assert.Equal(result.RawPixels, Cv2.CountNonZero(displayRaw));
        using var roundTrip = surface.ToContent(displayRaw);
        AssertImage(result.RawMask, roundTrip);
        foreach (var band in surface.OmittedBands)
        {
            using var roi = new Mat(displayRaw, new Rect(0, band.CanvasStart, a.Width, band.Length));
            Assert.Equal(0, Cv2.CountNonZero(roi));
        }
        var swappedMap = new PageMap(b.Size(), a.Size(), map.CanvasSize,
            bands.Select(s => s with { AStart = s.BStart, BStart = s.AStart }));
        var swapped = RowComparisonSurface.Create(b, a, swappedMap, kinds, parameters);
        Assert.True(swapped.Success, swapped.Detail);
        Assert.Equal(surface.Pieces.Select(p => p with { AStart = p.BStart, BStart = p.AStart }), swapped.Surface!.Pieces);
        using var sa = swapped.Surface.ContentMap.Render(b, PageSpace.A); using var sb = swapped.Surface.ContentMap.Render(a, PageSpace.B);
        AssertImage(ca, sb); AssertImage(cb, sa);
        Assert.Equal(hashA, Hash(a)); Assert.Equal(hashB, Hash(b));

        Mat Read(string side)
        {
            var name = j.GetProperty("images").GetProperty(side).GetString()!;
            var path = Path.Combine(name.StartsWith("golden/", StringComparison.Ordinal) ? AppContext.BaseDirectory : Folder, name);
            var bytes = File.ReadAllBytes(path);
            if (!name.StartsWith("golden/", StringComparison.Ordinal)) Assert.Equal(name[..^4], Convert.ToHexStringLower(SHA256.HashData(bytes)));
            return Cv2.ImDecode(bytes, ImreadModes.Color);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opposing_structural_gaps_cannot_hide_a_replacement(bool reverse)
    {
        using var a = new Mat(120, 80, MatType.CV_8UC3, Scalar.All(200)); using var b = a.Clone();
        var map = new PageMap(a.Size(), b.Size(), new(80, 160),
            [new(0, 40, 0, 0), new(40, 40, 40, null), new(80, 40, null, 40), new(120, 40, 80, 80)]);
        if (reverse) map = new(b.Size(), a.Size(), map.CanvasSize, map.Segments.Select(s => s with { AStart = s.BStart, BStart = s.AStart }));
        Assert.Equal("unanchored_join", RowComparisonSurface.Create(a, b, map,
            [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Structural, RowBandKind.Paired], new()).Detail);
    }

    [Theory]
    [InlineData(4, false, false)]
    [InlineData(4, true, false)]
    [InlineData(40, false, true)]
    [InlineData(40, true, true)]
    public void Every_neighborhood_requires_one_continuous_real_source(int separation, bool reverse, bool accepted)
    {
        var height = 176 + separation;
        using var a = new Mat(height, 20, MatType.CV_8UC3, Scalar.All(160)); using var b = a.Clone();
        var map = new PageMap(a.Size(), b.Size(), new(20, height + 40),
            [new(0, 60, 0, 0), new(60, 40, 60, null), new(100, separation, 100, 60),
                new(100 + separation, 40, null, 60 + separation), new(140 + separation, 76, 100 + separation, 100 + separation)]);
        if (reverse) map = new(b.Size(), a.Size(), map.CanvasSize, map.Segments.Select(s => s with { AStart = s.BStart, BStart = s.AStart }));
        var roles = new[] { RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired };
        var result = RowComparisonSurface.Create(a, b, map, roles, new());
        Assert.Equal(accepted, result.Success);
        Assert.Equal(accepted ? null : "unsupported_common_neighborhood", result.Detail);
        // 比較領域の探索距離が広い場合も、同じ最大半径を全体へ適用する。
        var regional = new ComparisonParameters { Regions = [new(0, "拡大探索", new(0, 0, 1, 1), "compare", new() { MaxShiftMm = 2 })] };
        Assert.Equal("unsupported_common_neighborhood", RowComparisonSurface.Create(a, b, map, roles, regional).Detail);
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(254, false)]
    [InlineData(200, false)]
    public void Only_exact_white_can_be_retained_as_generated_white(int value, bool accepted)
    {
        using var a = new Mat(100, 20, MatType.CV_8UC3, Scalar.All(255));
        using var b = new Mat(140, 20, MatType.CV_8UC3, Scalar.All(255));
        using (var gap = new Mat(b, new Rect(0, 40, 20, 40))) gap.SetTo(new Scalar(255, value, 255));
        var map = new PageMap(a.Size(), b.Size(), b.Size(), [new(0, 40, 0, 0), new(40, 40, null, 40), new(80, 60, 40, 80)]);
        var result = RowComparisonSurface.Create(a, b, map, [RowBandKind.Paired, RowBandKind.WhiteSpace, RowBandKind.Paired], new());
        Assert.Equal(accepted, result.Success);
        Assert.Equal(accepted ? null : "nonwhite_space", result.Detail);
        if (accepted) Assert.Equal(800, result.Surface!.PreservedWhitePixels);
        // 非白というだけで内容帯として省略する既定経路を用意しない。
        Assert.Equal("invalid_band_roles", RowComparisonSurface.Create(a, b, map,
            [RowBandKind.Paired, RowBandKind.Paired, RowBandKind.Paired], new()).Detail);
    }

    [Fact]
    public void White_on_both_sides_must_be_paired_first_and_cannot_be_counted_twice()
    {
        using var a = new Mat(120, 20, MatType.CV_8UC3, Scalar.All(255)); using var b = a.Clone();
        var map = new PageMap(a.Size(), b.Size(), new(20, 160),
            [new(0, 40, 0, 0), new(40, 40, 40, null), new(80, 40, null, 40), new(120, 40, 80, 80)]);
        Assert.Equal("unresolved_white_correspondence", RowComparisonSurface.Create(a, b, map,
            [RowBandKind.Paired, RowBandKind.WhiteSpace, RowBandKind.WhiteSpace, RowBandKind.Paired], new()).Detail);
    }

    [Fact]
    public void Invalid_source_coverage_and_display_geometry_are_rejected_before_rendering()
    {
        using var a = new Mat(100, 20, MatType.CV_8UC3, Scalar.All(255)); using var b = a.Clone();
        foreach (var map in new[]
        {
            new PageMap(a.Size(), b.Size(), new(20, 99), [new(0, 99, 1, 0)]),
            new PageMap(a.Size(), b.Size(), new(20, 101), [new(0, 101, 0, 0)]),
            new PageMap(a.Size(), b.Size(), new(20, 99), [new(0, 99, 0, 0)]),
            new PageMap(a.Size(), b.Size(), new(20, 100), [new(0, 100, -1, 0)])
        }) Assert.Equal("incomplete_source_coverage", RowComparisonSurface.Create(a, b, map, [RowBandKind.Paired], new()).Detail);
        Assert.Equal("invalid_display_geometry", RowComparisonSurface.Create(a, b, PageMap.Global(a.Size(), b.Size(), a.Size(), new(1, 0)),
            [RowBandKind.Paired], new()).Detail);
        Assert.Equal("resource_limit", RowComparisonSurface.Create(a, b,
            new(a.Size(), b.Size(), new(20, int.MaxValue), [new(0, int.MaxValue, 0, 0)]), [RowBandKind.Paired], new()).Detail);
        Assert.False(RowComparisonSurface.CanRepresent(new(int.MaxValue, int.MaxValue)));
        Assert.True(RowComparisonSurface.CanRepresent(new(1, int.MaxValue / 3)));
        Assert.False(RowComparisonSurface.CanRepresent(new(1, int.MaxValue / 3 + 1)));
    }

    [Theory]
    [InlineData(72, 0.0001, 0)]
    [InlineData(300, 1.5, 0.15)]
    [InlineData(1200, 20, 0)]
    [InlineData(1200, 0.0001, 100)]
    public void Neighborhood_uses_validated_effective_dpi_ink_and_shift(int dpi, double ink, double shift)
    {
        var p = new ComparisonParameters { Dpi = dpi, Ink = new() { BackgroundRadiusMm = ink }, Diff = new() { MaxShiftMm = shift } };
        Assert.Equal(Math.Max(Math.Max(1, Units.RoundPixels(ink, dpi)), 2 + Units.RoundPixels(shift, dpi)), RowComparisonSurface.RequiredRadius(p));
        Assert.Throws<ArgumentException>(() => RowComparisonSurface.RequiredRadius(p with { Diff = new() { MaxShiftMm = double.NaN } }));
        Assert.Throws<ArgumentException>(() => RowComparisonSurface.RequiredRadius(p with { Ink = new() { BackgroundRadiusMm = double.PositiveInfinity } }));
    }

    [Fact]
    public void Typed_ownership_roundtrip_preserves_fragment_values_and_zeroes_omitted_bands()
    {
        using var a = new Mat(100, 12, MatType.CV_8UC3, Scalar.All(128));
        using var b = new Mat(140, 12, MatType.CV_8UC3, Scalar.All(128));
        var map = new PageMap(a.Size(), b.Size(), b.Size(), [new(0, 40, 0, 0), new(40, 40, null, 40), new(80, 60, 40, 80)]);
        var s = RowComparisonSurface.Create(a, b, map, [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired], new()).Surface!;
        using var owners = new Mat(140, 12, MatType.CV_32SC1, Scalar.All(-1));
        using (var region = new Mat(owners, new Rect(4, 30, 4, 60))) region.SetTo(37);
        using var content = s.ToContent(owners); using var display = s.ToDisplay(content);
        Assert.Equal(37, content.At<int>(45, 5)); Assert.Equal(-1, content.At<int>(55, 5));
        Assert.Equal(37, display.At<int>(85, 5)); Assert.Equal(0, display.At<int>(50, 5));
        using var restored = s.ToContent(display); AssertImage(content, restored);
    }

    private static string Hash(Mat image) => Convert.ToHexStringLower(SHA256.HashData(MatBuffers.Bytes(image)));

    [Fact]
    public void One_content_cluster_keeps_its_pixels_and_source_fragments_across_an_inserted_band()
    {
        using var a = new Mat(200, 80, MatType.CV_8UC3, Scalar.All(128));
        using var b = new Mat(260, 80, MatType.CV_8UC3, Scalar.All(128));
        var map = new PageMap(a.Size(), b.Size(), b.Size(), [new(0, 80, 0, 0), new(80, 60, null, 80), new(140, 120, 80, 140)]);
        var s = RowComparisonSurface.Create(a, b, map, [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired], new()).Surface!;
        using var mask = new Mat(200, 80, MatType.CV_8UC1, Scalar.All(0));
        using (var changed = new Mat(mask, new Rect(42, 78, 2, 4))) changed.SetTo(255);
        using var raw = new RawDifference(mask.Clone(), 0, 0);
        var parameters = new ComparisonParameters { Cluster = new() { MinPixels = 5, MergeXMm = 0, MergeYMm = 0 } };
        using var compared = PageComparer.FromRawDifference(raw, parameters);
        var cluster = Assert.Single(compared.Clusters);
        Assert.Equal(8, cluster.Pixels);
        var fragments = s.Fragments(compared.RawMask);
        Assert.Equal([new Rect(42, 78, 2, 2), new Rect(42, 140, 2, 2)], fragments.Select(f => f.DisplayBounds));
        Assert.Equal([new PageBounds(42, 78, 44, 80), new PageBounds(42, 80, 44, 82)], fragments.Select(f => f.SourceA!.Value));
        Assert.Equal([new PageBounds(42, 78, 44, 80), new PageBounds(42, 140, 44, 142)], fragments.Select(f => f.SourceB!.Value));
        Assert.Equal(8, fragments.Sum(f => f.Pixels));
        using var display = s.ToDisplay(compared.RawMask);
        using var wrongRaw = new RawDifference(display.Clone(), 0, 0);
        using var wrong = PageComparer.FromRawDifference(wrongRaw, parameters);
        Assert.Empty(wrong.Clusters); // D で再判定すると断片ごとにノイズとなる反例を固定する。
        Assert.Single(compared.Clusters);
    }

    [Fact]
    public void Fragment_rectangles_follow_actual_cluster_pixels_and_missing_sources_remain_null()
    {
        using var a = new Mat(100, 20, MatType.CV_8UC3, Scalar.All(255));
        using var b = new Mat(140, 20, MatType.CV_8UC3, Scalar.All(255));
        var map = new PageMap(a.Size(), b.Size(), b.Size(), [new(0, 40, 0, 0), new(40, 40, null, 40), new(80, 60, 40, 80)]);
        var s = RowComparisonSurface.Create(a, b, map, [RowBandKind.Paired, RowBandKind.WhiteSpace, RowBandKind.Paired], new()).Surface!;
        using var mask = new Mat(s.ContentMap.CanvasSize, MatType.CV_8UC1, Scalar.All(0));
        mask.Set(39, 1, (byte)255); mask.Set(40, 18, (byte)255);
        var parts = s.Fragments(mask);
        Assert.Equal(new Rect(1, 39, 1, 1), parts[0].DisplayBounds);
        Assert.Equal(new Rect(18, 40, 1, 1), parts[1].DisplayBounds);
        Assert.Null(parts[1].SourceA);
        Assert.Equal(new PageBounds(18, 40, 19, 41), parts[1].SourceB);
    }

    [Fact]
    public void Too_different_ratio_is_decided_before_display_padding_can_dilute_it()
    {
        using var a = new Mat(100, 20, MatType.CV_8UC3, Scalar.All(128));
        using var b = new Mat(200, 20, MatType.CV_8UC3, Scalar.All(128));
        var map = new PageMap(a.Size(), b.Size(), b.Size(), [new(0, 40, 0, 0), new(40, 100, null, 40), new(140, 60, 40, 140)]);
        var s = RowComparisonSurface.Create(a, b, map, [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired], new()).Surface!;
        using var mask = new Mat(100, 20, MatType.CV_8UC1, Scalar.All(0));
        using (var changed = new Mat(mask, new Rect(0, 0, 8, 100))) changed.SetTo(255);
        using var raw = new RawDifference(mask.Clone(), 0, 0);
        using var content = PageComparer.FromRawDifference(raw, new());
        Assert.Equal("too_different", content.Status);
        using var display = s.ToDisplay(content.RawMask);
        Assert.Equal(content.RawPixels, Cv2.CountNonZero(display));
        Assert.True(content.RawPixels / (double)display.Total() < 0.3);
        Assert.Empty(content.Clusters);
    }

    [Fact]
    public void Piecewise_bounds_and_global_shift_compose_without_reapplying_the_shift()
    {
        using var a = new Mat(140, 20, MatType.CV_8UC3, Scalar.All(255)); using var b = a.Clone();
        using (var band = new Mat(b, new Rect(0, 40, 20, 40))) band.SetTo(0);
        var map = new PageMap(a.Size(), b.Size(), new(20, 180),
            [new(0, 40, 0, 0), new(40, 40, null, 40), new(80, 60, 40, 80), new(140, 40, 100, null)]);
        var s = RowComparisonSurface.Create(a, b, map, [RowBandKind.Paired, RowBandKind.Structural, RowBandKind.Paired, RowBandKind.WhiteSpace], new()).Surface!;
        using var mask = new Mat(s.ContentMap.CanvasSize, MatType.CV_8UC1, Scalar.All(0));
        using (var changed = new Mat(mask, new Rect(5, 39, 2, 3))) changed.SetTo(255);
        var global = PageMap.Global(a.Size(), b.Size(), a.Size(), new(2, -3));
        var parts = s.Fragments(mask, global);
        Assert.Equal(new PageBounds(3, 42, 5, 43), parts[0].SourceB);
        Assert.Equal(new PageBounds(3, 83, 5, 85), parts[1].SourceB);
        Assert.Equal([new PageBounds(5, 39, 7, 40), new PageBounds(5, 80, 7, 82)],
            map.MapBoundsParts(new(5, 39, 7, 42), PageSpace.A, PageSpace.Canvas));
        Assert.Equal(new PageBounds(5, 39, 7, 82), map.MapBounds(new(5, 39, 7, 42), PageSpace.A, PageSpace.Canvas));
    }
    private static void AssertImage(Mat expected, Mat actual)
    {
        Assert.Equal(expected.Size(), actual.Size()); Assert.Equal(expected.Type(), actual.Type());
        Assert.Equal(0, Cv2.Norm(expected, actual, NormTypes.INF));
    }
}
