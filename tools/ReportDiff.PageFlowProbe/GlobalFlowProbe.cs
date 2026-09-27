using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;

// 全体補正と送りの独立検証。固定PDFの描画を整数移動した合成ラスタを使い、製品CLIには接続しない。
internal static class GlobalFlowProbe
{
    private sealed record Scenario(string Id, string Fixture = "R10", bool Anchor = true, bool Reverse = false,
        double X = 0, double Y = 4, string Mutation = "none", string Settings = "normal", string Pattern = "uniform",
        bool Expected = true);
    private static readonly Scenario[] Scenarios = [
        new("original-r10", Anchor: false, Expected: false),
        new("original-chain", "chain3", Anchor: false, Expected: false),
        new("anchor-zero", Y: 0), new("anchor-down"), new("anchor-up", Y: -6),
        new("anchor-reverse", Reverse: true), new("anchor-variable", Pattern: "variable"),
        new("anchor-mixed", Pattern: "mixed"), new("anchor-chain", "chain3", Pattern: "variable"),
        new("anchor-horizontal", X: 5, Y: 0, Expected: false), new("anchor-both", X: -5, Expected: false),
        new("body-tone", Mutation: "body"), new("body-excluded", Mutation: "body", Settings: "exclude"),
        new("body-region", Mutation: "body", Settings: "region"),
        new("band-tone", Mutation: "band", Expected: false),
        new("band-excluded", Mutation: "band", Settings: "band-exclude", Expected: false),
        new("edge-content", Mutation: "edge", Expected: false),
        new("subpixel", Y: 4.5, Expected: false)];

    internal static void Run(string inputs, string output)
    {
        if (Directory.Exists(output)) throw new ArgumentException("未使用の出力先を指定してください。");
        Directory.CreateDirectory(output);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(inputs, "observations.json")));
        var runs = new List<object>();
        foreach (var scenario in Scenarios)
        {
            var directory = Path.Combine(output, scenario.Id); Directory.CreateDirectory(directory);
            var paths = new[] { "a", "b" }.Select(side => Path.Combine(inputs, scenario.Fixture, side + ".pdf")).ToArray();
            var fixedRecord = manifest.RootElement.EnumerateArray().Single(r => r.GetProperty("test").GetProperty("id").GetString() == scenario.Fixture);
            for (var i = 0; i < 2; i++) Require(Hash(paths[i]) == fixedRecord.GetProperty(i == 0 ? "a_sha256" : "b_sha256").GetString(), "固定PDFハッシュ");
            using var pa = PdfReader.Open(paths[0]); using var pb = PdfReader.Open(paths[1]);
            using var ta = new PdfTextReader(paths[0]); using var tb = new PdfTextReader(paths[1]);
            Require(pa.PageCount == pb.PageCount, "対応ページ数");
            var count = pa.PageCount;
            var keys = Enumerable.Range(1, count).SelectMany(p => new[] { new PageFlowPageKey(PageSpace.A, p), new(PageSpace.B, p) }).ToArray();
            var rawCollector = new PageFlowCollector(keys); var alignedCollector = new PageFlowCollector(keys);
            var maps = new Dictionary<int, PageMap>(); var alignments = new Dictionary<int, AlignmentResult>();
            var words = new Dictionary<PageFlowPageKey, RowTextResult>(); var pageRecords = new List<object>();
            var mismatchedFrameRejected = 0; var changedRereadRejected = 0;
            foreach (var number in Enumerable.Range(1, count))
            {
                using var a = Raw(new(PageSpace.A, number)); using var b = Raw(new(PageSpace.B, number));
                var parameters = Parameters(number);
                var alignment = GlobalAligner.Estimate(a, b, parameters, new() { Enabled = true });
                var map = PageMap.Global(a.Size(), b.Size(), a.Size(), alignment.Status == "applied" ? alignment.EstimatedShiftPx : null);
                maps.Add(number, map); alignments.Add(number, alignment);
                foreach (var side in new[] { PageSpace.A, PageSpace.B })
                {
                    var key = new PageFlowPageKey(side, number); var original = side == PageSpace.A ? a : b;
                    using var aligned = map.Render(original, side);
                    var text = OriginalWords(key, original.Size());
                    words.Add(key, RowTextAnnotations.ToAligned(text, map, side));
                    Require(rawCollector.Add(key, original, text, .5), "元記述収集");
                    Require(alignedCollector.Add(key, aligned, words[key], .5), "補正記述収集");
                    Save($"p{number}-{side}-O.png", original); Save($"p{number}-{side}-G.png", aligned);
                    File.WriteAllText(Path.Combine(directory, $"p{number}-{side}-words.json"), JsonSerializer.Serialize(new { original = text, aligned = words[key] }, Json));
                }
            }
            var rawDocument = rawCollector.Complete() ?? throw new InvalidOperationException(rawCollector.FailureReason);
            var alignedDocument = alignedCollector.Complete() ?? throw new InvalidOperationException(alignedCollector.FailureReason);
            var plan = PageFlowPlan.PrepareForPages(alignedDocument, Corrected, Parameters, new() { Enabled = true });
            var originalLinks = new List<PageFlowInference.Proposal>(); var linkRecords = new List<object>();
            foreach (var link in plan.Links)
            {
                var source = OriginalBand(link.Source); var target = OriginalBand(link.Target);
                PageFlowBandVerifier.Verification? proof = null;
                if (source is not null && target is not null)
                {
                    using var a = Raw(source.Page); using var b = Raw(target.Page);
                    proof = PageFlowBandVerifier.Verify(link with { Source = source, Target = target, Status = "candidate", Reason = null },
                        rawDocument.Pages.Single(p => p.Key == source.Page), rawDocument.Pages.Single(p => p.Key == target.Page),
                        a, b, Parameters(source.Page.Page), Parameters(target.Page.Page));
                    originalLinks.Add(proof.Proposal);
                    using var sa = new Mat(a, new Rect(0, source.Top, a.Width, source.Height));
                    using var sb = new Mat(b, new Rect(0, target.Top, b.Width, target.Height));
                    Save($"link-{linkRecords.Count + 1}-source-O.png", sa); Save($"link-{linkRecords.Count + 1}-target-O.png", sb);
                }
                linkRecords.Add(new { aligned = link, original_source = source, original_target = target, proof,
                    reason = source is null || target is null ? "original_band_not_fullwidth_or_clipped" : proof?.Proposal.Reason });
            }
            var descriptions = new List<PageFlowAggregation.Page>(); var adoptions = new List<PageFlowAdoptionResult>();
            foreach (var page in plan.Pages)
            {
                var number = page.Number; var map = maps[number];
                using var oa = Raw(new(PageSpace.A, number)); using var ob = Raw(new(PageSpace.B, number));
                using var a = Corrected(new(PageSpace.A, number)); using var b = Corrected(new(PageSpace.B, number));
                // 補正後を元画像に偽装することも、元画像の再読込変更も検出する。
                var descriptor = rawDocument.Pages.Single(p => p.Key == new PageFlowPageKey(PageSpace.B, number));
                if (Cv2.Norm(ob, b, NormTypes.INF) != 0) { Reject(() => PageFlowBandVerifier.VerifyOriginal(descriptor, b)); mismatchedFrameRejected++; }
                using (var changed = ob.Clone()) { changed.Set(0, 0, new Vec3b(254, 255, 255)); Reject(() => PageFlowBandVerifier.VerifyOriginal(descriptor, changed)); changedRereadRejected++; }
                object? candidateRecord = null;
                if (plan.Decision.Ready)
                {
                    using var candidate = plan.Compare(number, a, b);
                    var adoption = PageFlowAdoption.Evaluate(plan, number, a, b, Parameters(number), new() { Enabled = true },
                        words[new(PageSpace.A, number)], words[new(PageSpace.B, number)]);
                    adoptions.Add(adoption); descriptions.Add(candidate.Describe());
                    var surface = page.Built!.Surface!;
                    var cm = Compose(surface.ContentMap, map); var dm = Compose(surface.DisplayMap, map);
                    using var ca = cm.Render(oa, PageSpace.A); using var cb = cm.Render(ob, PageSpace.B);
                    using var da = dm.Render(oa, PageSpace.A); using var db = dm.Render(ob, PageSpace.B);
                    using var expectedDa = surface.DisplayMap.Render(a, PageSpace.A); using var expectedDb = surface.DisplayMap.Render(b, PageSpace.B);
                    Require(Cv2.Norm(ca, candidate.ContentA, NormTypes.INF) == 0 && Cv2.Norm(cb, candidate.ContentB, NormTypes.INF) == 0
                        && Cv2.Norm(da, expectedDa, NormTypes.INF) == 0 && Cv2.Norm(db, expectedDb, NormTypes.INF) == 0, "合成写像の元画素描画");
                    Save($"p{number}-C-A.png", ca); Save($"p{number}-C-B.png", cb);
                    Save($"p{number}-D-A.png", da); Save($"p{number}-D-B.png", db);
                    Save($"p{number}-C-raw.png", candidate.Content.RawMask); Save($"p{number}-D-raw.png", candidate.Display.Comparison.RawMask);
                    var structures = candidate.Display.StructuralChanges.Select(s => new { aligned = s,
                        original_a = OriginalParts(s.SourceA, PageSpace.A), original_b = OriginalParts(s.SourceB, PageSpace.B) }).ToArray();
                    var clusters = candidate.Display.Comparison.Clusters.Select(c => new { aligned = c,
                        original_a = OriginalParts(c.Row?.SourceA, PageSpace.A), original_b = OriginalParts(c.Row?.SourceB, PageSpace.B) }).ToArray();
                    candidateRecord = new { adoption, surface.Pieces, display_segments = surface.DisplayMap.Segments,
                        composed_content_segments = cm.Segments, composed_display_segments = dm.Segments,
                        composed_dx = cm.Dx, candidate.Content.RawPixels, content_clusters = candidate.Content.Clusters.Count,
                        candidate.Display.DifferenceCount, structures, clusters, composed_images_equal = true };
                    IReadOnlyList<PageBounds> OriginalParts(RowSourceBounds? bounds, PageSpace side) => bounds is null ? []
                        : bounds.Parts.SelectMany(p => map.MapBoundsParts(p, PageSpace.Canvas, side)).ToArray();
                }
                PageFlowBandVerifier.VerifyOriginal(rawDocument.Pages.Single(p => p.Key == new PageFlowPageKey(PageSpace.A, number)), oa);
                PageFlowBandVerifier.VerifyOriginal(descriptor, ob);
                pageRecords.Add(new { number, alignment = alignments[number], global_segments = map.Segments, map.Dx,
                    parameters = Parameters(number), page.Built?.Status, page.Built?.Reason, candidate = candidateRecord });
            }
            var rawProofs = linkRecords.Count > 0 && originalLinks.Count == linkRecords.Count && originalLinks.All(l => l.Status == "band_verified");
            var adoptable = plan.Decision.Ready && rawProofs && adoptions.Count == count && adoptions.All(a => a.Accepted);
            var aggregation = descriptions.Count == count ? plan.Aggregate(descriptions, false) : null;
            // 全体補正後の論理集約に、元座標の帯を混ぜてはいけないことを確かめる。
            PageFlowAggregation.Decision? mixedFrame = null;
            if (aggregation?.Status == "grouped" && originalLinks.Count > 0 && !originalLinks.SequenceEqual(plan.Links))
            {
                var rows = plan.Inference.Layouts.A.Concat(plan.Inference.Layouts.B).SelectMany(l => l.Body.Select((line, i) =>
                    new PageFlowAggregation.Row(l.Page.Key.Side, l.Page.Key.Page, l.BodyStart + i * l.Pitch, l.Pitch, line.Text))).ToArray();
                mixedFrame = PageFlowAggregation.Evaluate(new(true, false, rows, originalLinks, descriptions));
                Require(mixedFrame.Status == "skipped", "元帯と補正後構造の混同を見送る");
            }
            runs.Add(new { scenario, pdf_sha256 = paths.Select(Hash), diagnostic_only = true, plan.Decision,
                original_proofs_passed = rawProofs, diagnostic_adoptable = adoptable, expectation_met = adoptable == scenario.Expected,
                links = linkRecords, pages = pageRecords, candidate_aggregation = aggregation, mixed_frame_aggregation = mixedFrame,
                mismatched_frame_rejected = mismatchedFrameRejected, changed_reread_rejected = changedRereadRejected,
                raw_sha256 = rawDocument.Pages.Select(p => new { p.Key, p.PixelSha256 }),
                aligned_sha256 = alignedDocument.Pages.Select(p => new { p.Key, p.PixelSha256 }) });
            File.WriteAllText(Path.Combine(output, "global-flow.json"), JsonSerializer.Serialize(runs, Json));
            Console.WriteLine($"{scenario.Id}: global={alignments.Values.Count(a => a.Status == "applied")}/{count}, range={plan.Decision.Ready}, original_proof={rawProofs}, adoptable={adoptable}, expected={scenario.Expected}");

            (double X, double Y) InputShift(int page) => (scenario.X, scenario.Pattern == "variable" ? new[] { 4, -6, 3 }[page - 1]
                : scenario.Pattern == "mixed" && page == 1 ? 0 : scenario.Y);
            bool IsBaseB(PageSpace side) => (side == PageSpace.B) != scenario.Reverse;
            Mat Raw(PageFlowPageKey key)
            {
                var isB = IsBaseB(key.Side); using var pdf = (isB ? pb : pa).ReadPage(key.Page);
                using var baseImage = scenario.Anchor ? Anchored(pdf.Pixels) : pdf.Pixels.Clone();
                if (isB && key.Page == 2 && scenario.Mutation is "body" or "band")
                    Cv2.Rectangle(baseImage, new Rect(650, (scenario.Anchor ? 256 : 0) + (scenario.Mutation == "body" ? 550 : 350), 10, 10), Scalar.All(160), -1);
                var shift = isB ? InputShift(key.Page) : (X: 0.0, Y: 0.0);
                var result = Shift(baseImage, shift.X, shift.Y);
                if (isB && key.Page == 2 && scenario.Mutation == "edge") result.Set(0, 500, new Vec3b(254, 255, 255));
                return result;
            }
            Mat Corrected(PageFlowPageKey key) { using var raw = Raw(key); return maps[key.Page].Render(raw, key.Side); }
            RowTextResult OriginalWords(PageFlowPageKey key, Size size)
            {
                var isB = IsBaseB(key.Side); var pad = scenario.Anchor ? 256 : 0;
                var text = (isB ? tb : ta).ReadRowWords(key.Page, new(size.Width, size.Height - 2 * pad), 300);
                var shift = isB ? InputShift(key.Page) : (X: 0.0, Y: 0.0);
                return text with { Words = text.Words.Select(w => w with {
                    Bounds = new(w.Bounds.Left + shift.X, w.Bounds.Top + shift.Y + pad, w.Bounds.Right + shift.X, w.Bounds.Bottom + shift.Y + pad),
                    Baselines = w.Baselines.Select(y => y + shift.Y + pad).ToArray() }).ToArray() };
            }
            ComparisonParameters Parameters(int page)
            {
                // 本文変更はBの3行目で、Aでは2行目に対応する。設定はA基準。
                var y = scenario.Settings == "band-exclude" ? 350 : 450;
                var bounds = PageMap.CanvasMillimeters(new Rect(645, (scenario.Anchor ? 256 : 0) + y - 5, 20, 20), 300);
                return page != 2 ? new() : scenario.Settings switch {
                    "exclude" or "band-exclude" => new() { Exclude = [bounds] },
                    "region" => new() { Regions = [new(1, "検証用の厳密領域", bounds, "compare", new() { MaxShiftMm = 0, EdgeTolerance = 0 })] },
                    _ => new() };
            }
            PageFlowBand? OriginalBand(PageFlowBand? band)
            {
                if (band is null) return null;
                var map = maps[band.Page.Page];
                var parts = map.MapBoundsParts(new(0, band.Top, map.CanvasSize.Width, band.Bottom), PageSpace.Canvas, band.Page.Side);
                if (parts.Count != 1) return null;
                var p = parts[0];
                if (p.Left != 0 || p.Right != map.SizeOf(band.Page.Side).Width
                    || p.Bottom - p.Top != band.Height || p.Top != (int)p.Top) return null;
                return new(band.Page, (int)p.Top, band.Height);
            }
            void Save(string file, Mat image) => File.WriteAllBytes(Path.Combine(directory, file), image.ImEncode(".png"));
        }
    }

    private static PageMap Compose(PageMap local, PageMap global)
    {
        Require(global.Segments.Count == 1 && global.Segments[0].AStart == 0 && global.Segments[0].BStart is not null
            && local.Dx == 0 && local.SizeA == global.CanvasSize && local.SizeB == global.CanvasSize, "合成の前提");
        var bStart = global.Segments[0].BStart!.Value;
        return new(global.SizeA, global.SizeB, local.CanvasSize, local.Segments.Select(s =>
            s with { BStart = s.BStart is int b ? checked(b + bStart) : null }), global.Dx);
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("flow_original_changed:", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("元画像変更を拒否しませんでした。");
    }

    // 製品のPageMap.Renderとは独立した入力移動。整数以外は検出必須の対照にだけ使う。
    private static Mat Shift(Mat source, double x, double y)
    {
        using var transform = Mat.FromArray(new double[,] { { 1, 0, x }, { 0, 1, y } });
        var result = new Mat();
        try { Cv2.WarpAffine(source, result, transform, source.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255)); return result; }
        catch { result.Dispose(); throw; }
    }
    private static Mat Anchored(Mat original)
    {
        var image = new Mat(original.Height + 512, original.Width, MatType.CV_8UC3, Scalar.All(255));
        try
        {
            var width = image.Width;
            using (var roi = new Mat(image, new Rect(0, 256, original.Width, original.Height))) original.CopyTo(roi);
            foreach (var start in new[] { 64, image.Height - 248 })
            for (var y = 0; y < 184; y += 9)
            for (var x = 64; x < width - 72; x += 9)
                if ((x * 37 + y * 17 + x * y * 13) % 11 < 9)
                    Cv2.Rectangle(image, new Rect(x, start + y, 8, 8), Scalar.All(0), -1);
            return image;
        }
        catch { image.Dispose(); throw; }
    }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
