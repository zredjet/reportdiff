using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using static ReportDiff.Report.ReportRows;

namespace ReportDiff.Report;

public sealed partial class ReportWriter
{
    public PageImages AddAnchoredContent(int page, AnchoredImagePair images)
    {
        CheckWritable(); ValidateImage(images.A); ValidateImage(images.B);
        var stem = "pages/" + PageStem(page) + "_content";
        ExecuteWrite(() => { WritePng(stem + "_a.png", images.A); WritePng(stem + "_b.png", images.B); });
        return new(null, null, null) { ContentA = stem + "_a.png", ContentB = stem + "_b.png" };
    }

    public RawEvidence? AddAnchoredRaw(int page, Mat a, Mat b, int dpi)
    {
        CheckWritable(); ValidateImage(a); ValidateImage(b);
        RawEvidence? raw = null;
        if (config.Report.RawOverlay) ExecuteWrite(() => raw = WriteRawEvidence(page, a, b, a.Size(), b.Size(), dpi, null, null));
        return raw;
    }

    public ReportPage AddAnchoredPage(AnchoredContentPlan plan, AnchoredComparisonResult result, AnchoredDisplayResult display,
        AnchoredImagePair images, PageImages contentImages, RawEvidence? raw, PageTextAnnotations textA, PageTextAnnotations textB,
        PageTextAnnotations structureTextA, PageTextAnnotations structureTextB)
    {
        var page = display.Page; CheckPage(page);
        var d = plan.Displays.Single(d => d.Page == page); var content = result.Contents.Single(c => c.Id.OwnerPage == page);
        var dpi = plan.Settings.Comparison.Dpi;
        ValidateImage(images.A); ValidateImage(images.B);
        if (images.A.Size() != d.Size || images.B.Size() != d.Size || display.RawMask.Size() != d.Size)
            throw new ArgumentException("内容面の表示画像とマスクが一致しません。");
        var stem = "pages/" + PageStem(page);
        var paths = contentImages with { A = stem + "_a.png", B = stem + "_b.png", Overlay = stem + "_overlay.png" };
        var clusters = new List<ReportCluster>(); var references = new List<ReportContentReference>();
        var structures = display.Structures.Select(s =>
        {
            var a = s.Structure.A; var b = s.Structure.B; var bounds = s.DisplayBounds;
            return new ReportStructuralChange(s.Structure.Reference.StructuralChangeId, s.Structure.Kind, Box(bounds), Mm(bounds, dpi),
                BandSource(a), BandSource(b), d.Segments.Count(p => p.Band.Top >= bounds.Top && p.Band.Top < bounds.Bottom),
                s.Structure.Dy is { } dy ? new(0, dy) : null, false, null, Nonwhite(images.A, bounds), Nonwhite(images.B, bounds), 0, 0, [],
                structureTextA.TextByCluster.GetValueOrDefault(s.Structure.Reference.StructuralChangeId),
                structureTextB.TextByCluster.GetValueOrDefault(s.Structure.Reference.StructuralChangeId))
                { Role = s.Role, ComparedInContent = s.ComparedInContent };
        }).ToArray();
        ExecuteWrite(() =>
        {
            WritePng(paths.A, images.A); WritePng(paths.B, images.B);
            using (var overlay = ReportImages.AnchoredOverlay(images.B, display, dpi)) WritePng(paths.Overlay, overlay);
            foreach (var cluster in content.Clusters)
            {
                var parts = display.Parts.Where(p => p.Content.Surface == content.Id && p.Content.ClusterId == cluster.Id).ToArray();
                if (parts.Length == 0) throw new ReportWriteException("内容差分の所有ページに表示断片がありません。");
                var bounds = Union(parts.Select(p => p.DisplayBounds));
                var annotation = display.Annotations.Single(a => a.Content.Surface == content.Id && a.Content.ClusterId == cluster.Id);
                var crops = Crops($"{PageStem(page)}_c{cluster.Id:D3}", bounds);
                clusters.Add(new(cluster.Id, Box(bounds), Mm(bounds, dpi), cluster.Pixels, cluster.Pixels / (double)bounds.Width / bounds.Height,
                    annotation.Kind, annotation.Shift is { } shift ? new(shift.Dx, shift.Dy) : null,
                    textA.TextByCluster.GetValueOrDefault(cluster.Id), textB.TextByCluster.GetValueOrDefault(cluster.Id), crops)
                {
                    ContentRef = new(page, cluster.Id), SourceParts = parts.Select(ReportAnchored.Part).ToArray(),
                    DisplayPartsPx = parts.Select(p => Box(p.DisplayBounds)).ToArray(), ContentBboxPx = Box(cluster.Bounds),
                    ContentBboxMm = Mm(cluster.Bounds, dpi), ContentFillRatio = cluster.Pixels / (double)cluster.Bounds.Width / cluster.Bounds.Height,
                    RelatedClusterIds = annotation.RelatedClusterIds,
                    SourceA = LegacySource(cluster.Id, true), SourceB = LegacySource(cluster.Id, false),
                    RowParts = parts.Select(p => new ReportRowFragment(Box(p.ContentBounds), Box(p.DisplayBounds),
                        p.SourceA?.Page.Page == page ? Continuous(p.SourceA.Bounds) : null,
                        p.SourceB?.Page.Page == page ? Continuous(p.SourceB.Bounds) : null, p.Pixels)).ToArray()
                });
            }
            foreach (var group in display.Parts.Where(p => p.Content.Surface.OwnerPage != page).GroupBy(p => p.Content))
            {
                var bounds = Union(group.Select(p => p.DisplayBounds)); var key = group.Key;
                references.Add(new(ReportAnchored.Key(key), key.Surface.OwnerPage, key.ClusterId, Box(bounds),
                    group.Select(ReportAnchored.Part).ToArray(), group.Sum(p => p.Pixels),
                    Crops($"{PageStem(page)}_ref_p{key.Surface.OwnerPage:D3}_c{key.ClusterId:D3}", bounds)));
            }
        });
        var adoption = result.Adoption[page - 1]; var counts = StructureCounts.From(structures);
        var causes = structures.Where(s => s.Role == "cause").ToArray();
        var row = new ReportRowAlignment("applied", "anchored_content", null, "pdf_text", adoption.Score, adoption.ScoreGap, 1,
            adoption.SupportBands, adoption.BaselineRawPixels, adoption.CandidateRawPixels, adoption.Improvement,
            Size(d.OriginalSize), Size(d.OriginalSize), Size(d.OriginalSize),
            d.Segments.Select(s => new ReportRowSegment(s.Band.Top, s.Band.Height, s.Band.A?.Top, s.Band.B?.Top,
                s.Band.A is { } a && s.Band.B is { } b ? a.Top - b.Top : null, s.Role switch
                { AnchoredBandRole.Cause => "structural", AnchoredBandRole.Carry => "carry", AnchoredBandRole.WhiteSpace => "white_space", _ => "paired" })).ToArray(),
            null, structures, new(causes.Sum(s => (long)s.BboxPx.W * s.BboxPx.H),
                d.Segments.Where(s => s.Role == AnchoredBandRole.WhiteSpace).Sum(s => (long)s.Band.Height * d.Size.Width),
                causes.Sum(s => s.ContentPixelsA + s.ContentPixelsB), 0, null, "not_compared_in_content_canvas"),
            display.Annotations.Where(a => a.Content.Surface.OwnerPage == page && a.Reason is not null)
                .Select(a => new RowAnnotationOmission([a.Content.ClusterId], a.Reason!)).ToArray(), [], []) { ContentSurfaceId = page };
        var status = content.Status == "same" && (references.Count != 0 || counts.Total != 0) ? "different" : content.Status;
        var report = new ReportPage(page, status, Size(d.Size), false, content.RawPixels, content.NoiseDropped,
            content.AbsorbedGroups, content.MaxShiftPx, paths, clusters.AsReadOnly())
        {
            RowAlignment = row, StructuralChangeCount = counts.Total, StructuralChangeCounts = counts,
            DifferenceCountComplete = content.Status != "too_different" && !content.Warnings.Contains("CLUSTER_LIMIT"),
            ContentReferences = references.AsReadOnly(), ContentDisplay = new("row_display_top_left", display.DisplayRawPixels, references.Count),
            RawEvidence = raw
        };
        pages.Add(report);
        warnings.Add(new("ROW_ALIGNMENT_APPLIED", $"{page} ページ: 元ページを保持する内容面で行整列しました。構造変化 {counts.Total} 件、参照表示 {references.Count} 件です。"));
        foreach (var code in content.Warnings) warnings.Add(new(code, $"{page} ページ: 内容比較の警告: {code}"));
        foreach (var (side, text) in new[] { ("A", textA), ("B", textB), ("A・構造変化", structureTextA), ("B・構造変化", structureTextB) })
            foreach (var warning in text.Warnings) warnings.Add(new(warning.Code, $"{side}・{page} ページ: {warning.Message}"));
        return report;

        ClusterCrops Crops(string name, Rect bounds)
        {
            var crop = ReportImages.CropBounds(bounds, d.Size, config.Report.CropMarginMm, dpi); var prefix = "crops/" + name;
            using (var a = new Mat(images.A, crop)) WritePng(prefix + "_a.png", a);
            using (var b = new Mat(images.B, crop)) WritePng(prefix + "_b.png", b);
            using (var diff = ReportImages.DifferenceCrop(images.B, display.RawMask, crop, display.RemovalMask)) WritePng(prefix + "_diff.png", diff);
            return new(prefix + "_a.png", prefix + "_b.png", prefix + "_diff.png");
        }
        ReportSourceBounds? LegacySource(int id, bool a)
        {
            var sources = content.Parts.Where(p => p.Content.ClusterId == id).Select(p => a ? p.SourceA : p.SourceB).OfType<OriginalPixelBounds>().Distinct().ToArray();
            return sources.Length == 0 || sources.Any(p => p.Page.Page != page) ? null
                : new("original_top_left", Continuous(Union(sources.Select(s => s.Bounds))), sources.Select(s => Continuous(s.Bounds)).ToArray());
        }
        ReportSourceBounds? BandSource(PageFlowBand? band) => band is null ? null
            : new("original_top_left", new(0, band.Top, d.Size.Width, band.Height), [new(0, band.Top, d.Size.Width, band.Height)]);
    }
    private static ContinuousBox Continuous(Rect r) => new(r.X, r.Y, r.Width, r.Height);
    private static Rect Union(IEnumerable<Rect> source) => source.Aggregate((a, b) => Rect.Union(a, b));
    private static long Nonwhite(Mat image, Rect bounds)
    {
        using var roi = new Mat(image, bounds); using var white = new Mat(); Cv2.InRange(roi, Scalar.White, Scalar.White, white);
        return (long)bounds.Width * bounds.Height - Cv2.CountNonZero(white);
    }
}
