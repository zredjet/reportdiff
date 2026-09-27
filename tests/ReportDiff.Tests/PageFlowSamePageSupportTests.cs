using System.Text.Json;
using ReportDiff.Report;
using Xunit;

namespace ReportDiff.Tests;

public sealed partial class PageFlowCliTests
{
    public static IEnumerable<object[]> SamePageSupportCases()
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-same-page-support/geometry.json")));
        return data.RootElement.EnumerateArray().SelectMany(r => new[] { false, true }.Select(reverse => new object[] {
            r.GetProperty("id").GetString()!, reverse, r.GetProperty("capacity").GetInt32(),
            r.GetProperty("first").GetInt32(), r.GetProperty("second").GetInt32(),
            r.GetProperty("grouped").GetBoolean() && !r.GetProperty("existing_unmet").GetBoolean() })).ToArray();
    }

    [Theory]
    [MemberData(nameof(SamePageSupportCases))]
    public void Same_page_two_causes_require_local_support_and_preserve_baseline_when_adoption_fails(
        string id, bool reverse, int capacity, int first, int second, bool applied)
    {
        using var files = new Files("support-" + id, reverse);
        Assert.Equal(1, files.Run("on", true).Code); Assert.Equal(1, files.Run("off", false).Code);
        var report = files.Report("on"); var off = files.Report("off"); var flow = report.PageFlow!;
        Assert.Equal(applied || id == "same-page-two" ? "applied" : "skipped", flow.Status);
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-same-page-support/expected.json")));
        var saved = expected.RootElement.EnumerateArray().Single(r => r.GetProperty("run").GetString() == id + (reverse ? "-ba" : "-ab"));
        if (id == "same-page-two")
        {
            // 旧診断の未成立記録は保持し、A3の別経路が同じ固定PDFで期待した二原因を検出することを要求する。
            Assert.True(saved.GetProperty("hypothesis").GetProperty("grouped").GetBoolean());
            Assert.False(saved.GetProperty("hypothesis_met").GetBoolean());
            Assert.Equal("applied", flow.AnchoredContent!.Status);
            Assert.Equal(6, report.Summary.DifferenceCount); Assert.Equal(2, report.Summary.AggregatedDifferenceCount);
            Assert.Equal(0, report.Summary.Clusters); Assert.Equal(2, report.Pages.SelectMany(p => p.RowAlignment.StructuralChanges).Count(s => s.Role == "cause"));
            foreach (var (side, path) in new[] { (reverse ? "b" : "a", files.A), (reverse ? "a" : "b", files.B) })
                Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/page-flow-shared/same-page-two", side + ".pdf")), File.ReadAllBytes(path));
        }
        else if (applied)
        {
            Assert.Equal(id == "content-tone" ? 8 : 7, report.Summary.DifferenceCount);
            Assert.Equal(id == "content-tone" ? 3 : 2, report.Summary.AggregatedDifferenceCount);
            Assert.True(report.Summary.AggregatedDifferenceCountComplete);
            Assert.Equal(id == "content-tone" ? 1614 : 0, report.Pages.Sum(p => p.RawPixels));
            Assert.Equal(id == "content-tone" ? 1 : 0, report.Summary.Clusters);
            var link = Assert.Single(flow.Links); Assert.Equal(1, link.Id); Assert.Equal("carried", link.Status);
            var proof = Assert.IsType<ReportFlowAmbiguity>(link.Ambiguity);
            Assert.Equal(reverse ? "b" : "a", proof.SourceSide); Assert.Equal(1, proof.BoundaryPage);
            Assert.Equal(200, proof.SelectedDy); Assert.False(proof.PixelEqualityProven);
            Assert.Equal(new[] { 100, 200 }, proof.Shifts.Select(s => s.Dy));
            Assert.Equal(new[] { second - first, capacity - second - 2 }, proof.Shifts.Select(s => s.SupportText.Count));
            Assert.Equal(2, proof.CrossingRows.Count); Assert.Empty(proof.Alternatives);
            Assert.All(proof.CrossingRows, r => { Assert.Equal(1, r.Row.Page); Assert.Equal(2, r.Counterpart.Page); });

            var component = Assert.Single(flow.Aggregation.SharedComponents!);
            var independent = Assert.Single(saved.GetProperty("decision").GetProperty("components").EnumerateArray());
            Assert.Equal(independent.GetProperty("structures").EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32())),
                component.Structures.Select(r => (r.Page, r.StructuralChangeId)));
            Assert.Equal(2, component.Causes.Count); Assert.Equal(3, component.Movements.Count);
            Assert.Equal(new[] { 1, 2, 2 }, component.Movements.Select(m => m.Causes.Count));
            Assert.Equal(new[] { 100, 200, 200 }.Select(d => reverse ? -d : d), component.Movements.Select(m => m.Dy));
            Assert.All(component.Causes, c => { Assert.Equal(1, c.Rows); Assert.Equal(reverse ? -100 : 100, c.DeltaPx); });
            Assert.All(component.Movements, m => Assert.Equal(m.Dy, component.Causes.Where(c => m.Causes.Contains(c.Reference)).Sum(c => c.DeltaPx)));
            Assert.All(component.Balance, b => Assert.Equal(b.RowsB - b.RowsA, b.CauseDelta + b.Incoming - b.Outgoing));
            var html = File.ReadAllText(files.PathOf("on", "report.html"));
            foreach (var r in component.Structures)
            {
                Assert.Contains(report.Pages.Single(p => p.Page == r.Page).RowAlignment.StructuralChanges, s => s.Id == r.StructuralChangeId);
                Assert.Contains($"id=\"page-{r.Page}-structure-{r.StructuralChangeId}\"", html);
                Assert.Contains($"href=\"#page-{r.Page}-structure-{r.StructuralChangeId}\"", html);
            }
        }
        else
        {
            Assert.Empty(flow.Aggregation.Groups); Assert.Null(flow.Aggregation.SharedComponents);
            Assert.Equal(JsonSerializer.Serialize(off.Pages, ReportJson.Options), JsonSerializer.Serialize(report.Pages, ReportJson.Options));
            Assert.Equal(id switch { "same-page-two" => 10, "capacity7" => 12, "before-short" => 15, "faint-support" => 11, _ => 14 }, report.Summary.DifferenceCount);
            Assert.Equal(report.Summary.DifferenceCount, report.Summary.AggregatedDifferenceCount);
            var rangeOnly = id is "between-short" or "faint-support";
            Assert.Equal(rangeOnly, flow.RangeReady);
            if (rangeOnly)
            {
                var rejected = Assert.Single(flow.Pages, p => p.Adoption is { Accepted: false });
                Assert.Equal("insufficient_support", rejected.Adoption!.Reason);
                Assert.Equal(id == "between-short" ? "support_bands" : "support_ink", rejected.Adoption.Detail);
                Assert.Single(flow.Pages, p => p.Adoption is { Accepted: true });
                Assert.All(flow.Pages, p => Assert.Equal("baseline", p.Choice));
            }

        }
        foreach (var (p, q) in report.Pages.Zip(off.Pages))
        foreach (var (x, y) in new[] { (p.RawEvidence!.A.Image, q.RawEvidence!.A.Image), (p.RawEvidence.B.Image, q.RawEvidence.B.Image), (p.RawEvidence.Overlay, q.RawEvidence.Overlay) })
            Assert.Equal(File.ReadAllBytes(files.PathOf("on", x)), File.ReadAllBytes(files.PathOf("off", y)));
    }
}
