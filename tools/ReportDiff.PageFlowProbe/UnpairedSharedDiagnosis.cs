using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using static ReportDiff.Core.PageFlowInference;
using static ReportDiff.Core.PageFlowAggregation;

/// <summary>診断専用。片側末尾へ続く共有原因の候補・局所支持・写像・採否を分ける。</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal static class UnpairedSharedDiagnosis
{
    internal static void ReplayInference(string previous, string output)
    {
        using var data = JsonDocument.Parse(File.ReadAllBytes(previous)); var records = new List<object>();
        foreach (var record in data.RootElement.EnumerateArray())
        {
            var id = record.GetProperty("id").GetString()!; var reverse = record.GetProperty("reverse").GetBoolean();
            var folder = Path.Combine("tests/ReportDiff.Tests/Fixtures/page-flow-shared", id);
            var ap = Path.Combine(folder, reverse ? "b.pdf" : "a.pdf"); var bp = Path.Combine(folder, reverse ? "a.pdf" : "b.pdf");
            using var a = PdfReader.Open(ap); using var b = PdfReader.Open(bp); using var ta = new PdfTextReader(ap); using var tb = new PdfTextReader(bp);
            var keys = Enumerable.Range(1, a.PageCount).SelectMany(n => new[] { new PageFlowPageKey(PageSpace.A, n), new PageFlowPageKey(PageSpace.B, n) }).ToArray();
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys)
            {
                using var image = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page, 300);
                if (!collector.Add(key, image.Pixels, (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, image.Pixels.Size(), 300), .5))
                    throw new InvalidOperationException(collector.FailureReason);
            }
            var inference = PageFlowInference.Find(collector.Complete()!, new() { Enabled = true }, 300);
            var layouts = inference.Layouts.A.Concat(inference.Layouts.B).ToArray();
            var rows = layouts.SelectMany(l => l.Body.Select((r, i) => new SharedInferenceDiagnosis.Line(l.Page.Key, i,
                l.BodyStart + i * l.Pitch, l.Pitch, r.Text, r.Baseline, r.Bounds.Left))).ToArray();
            var actual = SharedInferenceDiagnosis.Infer(rows, layouts, new() { Enabled = true }, 300);
            var expected = record.GetProperty("experiment").GetProperty("inferred").Deserialize<SharedInferenceDiagnosis.CandidateResult>(AggregationProbe.Json)!;
            if (JsonSerializer.Serialize(actual, AggregationProbe.Json) != JsonSerializer.Serialize(expected, AggregationProbe.Json))
                throw new InvalidOperationException("既存診断が変化しました: " + id);
            records.Add(new { run = record.GetProperty("run").GetString(), equal = true });
        }
        using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(destination, records, AggregationProbe.Json);
    }
    private sealed record Case(string Id, int Count, int? Second, bool Expected, string Mutation = "none", string Drawing = "");
    internal static void Run(string output, bool inspectSingleSupport = false)
    {
        if (Directory.Exists(output)) throw new ArgumentException("出力先が既に存在します。");
        Directory.CreateDirectory(output);
        Case[] cases = [new("original", 12, 7, true), new("single", 12, null, true),
            new("before6", 12, 6, false), new("before8", 12, 8, true), new("before9", 12, 9, false), new("before10", 12, 10, false),
            new("chain", 18, 8, true), new("tone", 12, 8, true, "paired"), new("carry-change", 12, 8, false, "pixels"),
            new("residual-tone", 12, 8, false, Drawing: "0.85 g 130 155 25 8 re f\n"),
            new("residual-faint", 12, 8, false, Drawing: "0.9960784314 g 130 155 2.4 0.72 re f\n"),
            new("fixed-change", 12, 8, false, Drawing: "0.5 g 40 264 4.8 0.48 re f\n"),
            new("repeated", 12, 8, false), new("number", 12, 8, false)];
        var fixtures = new List<object>(); var records = new List<object>(); var supportRecords = new List<object>();
        foreach (var test in cases)
        {
            var folder = Path.Combine(output, "inputs", test.Id); Directory.CreateDirectory(folder);
            var f = new FlowCase(test.Id, test.Count, test.Mutation);
            var aa = FlowFixture.Rows(f, false); var bb = FlowFixture.Rows(f, true).ToList();
            if (test.Second is { } second) bb.Insert(second + 1, "SECOND ADDED");
            if (test.Id == "repeated") { aa[7] = aa[6]; bb[8] = bb[7]; }
            if (test.Id == "number") bb[^1] = "SUM TOTAL 556";
            foreach (var (side, rows) in new[] { ("a", aa.Chunk(6).ToArray()), ("b", bb.Chunk(6).ToArray()) })
            {
                var bytes = FlowFixture.Create(f, side == "b", explicitPages: rows,
                    pageDrawing: p => side == "b" && p == rows.Length - 1 ? test.Drawing : "");
                if (test.Id is "original" or "single")
                {
                    var original = Path.Combine("tests/ReportDiff.Tests/Fixtures/page-flow-unpaired",
                        test.Id == "original" ? "shared-unpaired" : "single-r11", side + ".pdf");
                    if (!bytes.SequenceEqual(File.ReadAllBytes(original))) throw new InvalidOperationException("旧PDFが再現しません。");
                }
                File.WriteAllBytes(Path.Combine(folder, side + ".pdf"), bytes);
                fixtures.Add(new { test.Id, side, expected_rows = rows, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
            }
            File.WriteAllText(Path.Combine(output, "fixtures.json"), JsonSerializer.Serialize(fixtures, AggregationProbe.Json));
            foreach (var reverse in new[] { false, true })
            {
                var name = test.Id + (reverse ? "-ba" : "-ab"); var target = Path.Combine(output, name); Directory.CreateDirectory(target);
                var pathA = Path.Combine(folder, reverse ? "b.pdf" : "a.pdf"); var pathB = Path.Combine(folder, reverse ? "a.pdf" : "b.pdf");
                using var pa = PdfReader.Open(pathA); using var pb = PdfReader.Open(pathB);
                using var ta = new PdfTextReader(pathA); using var tb = new PdfTextReader(pathB);
                var keys = Enumerable.Range(1, pa.PageCount).Select(n => new PageFlowPageKey(PageSpace.A, n))
                    .Concat(Enumerable.Range(1, pb.PageCount).Select(n => new PageFlowPageKey(PageSpace.B, n))).ToArray();
                var collector = new PageFlowCollector(keys);
                foreach (var key in keys)
                {
                    using var image = Read(key); Save(image, $"p{key.Page}-O-{key.Side}.png");
                    if (!collector.Add(key, image, Text(key, image), .5)) throw new InvalidOperationException(collector.FailureReason);
                }
                var document = collector.Complete()!; var options = new RowOptions { Enabled = true, CarryEnabled = true };
                var product = PageFlowPlan.Prepare(document, Read, new(), options);
                var layouts = product.Inference.Layouts.A.Concat(product.Inference.Layouts.B).ToArray();
                var lines = layouts.SelectMany(l => l.Body.Select((r, i) => new SharedInferenceDiagnosis.Line(l.Page.Key, i,
                    l.BodyStart + i * l.Pitch, l.Pitch, r.Text, r.Baseline, r.Bounds.Left))).ToArray();
                var inferred = SharedInferenceDiagnosis.Infer(lines, layouts, options, 300, terminalUnpaired: true);
                var verifications = inferred.Proposals.Select(p =>
                {
                    using var a = Read(p.Source!.Page); using var b = Read(p.Target!.Page);
                    return PageFlowBandVerifier.Verify(p, document.Pages.Single(d => d.Key == p.Source.Page),
                        document.Pages.Single(d => d.Key == p.Target.Page), a, b, new());
                }).ToArray();
                var links = verifications.Select(v => v.Proposal).ToArray(); var verified = links.Where(l => l.Status == "band_verified").ToArray();
                var planPages = new List<PageFlowPlan.Page>(); var proofs = new List<PageFlowPageProof>(); var coverage = new List<object>();
                foreach (var n in keys.Select(k => k.Page).Distinct().Order())
                {
                    if (n > Math.Min(pa.PageCount, pb.PageCount))
                    {
                        var key = keys.Single(k => k.Page == n); var layout = layouts.SingleOrDefault(l => l.Page.Key == key);
                        using var image = Read(key); var bands = verified.SelectMany(l => new[] { l.Source!, l.Target! }).Where(b => b.Page == key).ToArray();
                        long? residual = null;
                        if (layout is not null)
                        {
                            using var mask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black); long count = 0; var width = image.Width;
                            for (var y = layout.HeaderEnd; y < layout.FooterStart; y++)
                            for (var x = 0; x < width; x++)
                            {
                                if (bands.Any(b => y >= b.Top && y < b.Bottom)) continue;
                                var c = image.At<Vec3b>(y, x); if (c.Item0 == 255 && c.Item1 == 255 && c.Item2 == 255) continue;
                                count++; mask.Set(y, x, (byte)255);
                            }
                            residual = count; Save(mask, $"p{n}-unpaired-residual.png");
                        }
                        var covered = bands.Length > 0 && residual == 0;
                        planPages.Add(new(n, null, covered)); proofs.Add(new(n, false, bands));
                        coverage.Add(new { key, header_end = layout?.HeaderEnd, footer_start = layout?.FooterStart, bands, residual_pixels = residual, unpaired_covered = covered });
                    }
                    else
                    {
                        var la = layouts.SingleOrDefault(l => l.Page.Key == new PageFlowPageKey(PageSpace.A, n));
                        var lb = layouts.SingleOrDefault(l => l.Page.Key == new PageFlowPageKey(PageSpace.B, n));
                        using var a = Read(new(PageSpace.A, n)); using var b = Read(new(PageSpace.B, n));
                        var built = la is null || lb is null ? null : inspectSingleSupport
                            ? UnpairedLocalSupportSurface.Create(la, lb, a, b, verified, new())
                            : PageFlowSurface.Create(la, lb, a, b, verified, new());
                        planPages.Add(new(n, built, false)); proofs.Add(new(n, built?.Status == "built",
                            built?.Removed.Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray() ?? []));
                    }
                }
                var oneChain = inferred.Proposals.Select(l => l.Source!.Page.Page).Order().SequenceEqual(Enumerable.Range(1, Math.Max(pa.PageCount, pb.PageCount) - 1));
                var gate = PageFlowRangeGate.Evaluate(document, inferred.Status == "inferred" && oneChain,
                    inferred.Reason ?? (oneChain ? null : "not_one_chain"), links.Select(l => new PageFlowCandidate(l.Source, l.Target, l.Status == "band_verified", l.Reason)).ToArray(), proofs);
                // 診断専用の再構成。製品の候補・範囲証拠は書き換えず、CLIへ渡さない。
                var ctor = typeof(PageFlowPlan).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                var plan = (PageFlowPlan)ctor.Invoke([document, planPages.ToDictionary(p => p.Number, _ => new ComparisonParameters()),
                    product.Inference, verifications, links, planPages.ToArray(), gate]);
                var pages = new List<Page>(); var projections = new List<object>(); var adoptions = new List<object>();
                var adopted = gate.Ready && inferred.DisplacementSupport;
                foreach (var page in planPages)
                {
                    if (page.Number > Math.Min(pa.PageCount, pb.PageCount))
                    { pages.Add(new(page.Number, false, 0, 0, false, page.UnpairedCovered, [])); continue; }
                    using var a = Read(new(PageSpace.A, page.Number)); using var b = Read(new(PageSpace.B, page.Number));
                    if (!gate.Ready)
                    {
                        using var baseline = RowComparer.Compare(a, b, new(), options,
                            () => (Text(new(PageSpace.A, page.Number), a), Text(new(PageSpace.B, page.Number), b)));
                        pages.Add(new(page.Number, true, baseline.Comparison.Clusters.Count, baseline.DifferenceCount,
                            baseline.Display?.DifferenceCountComplete ?? baseline.Status != "too_different", false, [])); continue;
                    }
                    var adoption = PageFlowAdoption.Evaluate(plan, page.Number, a, b, new(), options,
                        Text(new(PageSpace.A, page.Number), a), Text(new(PageSpace.B, page.Number), b));
                    adoptions.Add(new { page = page.Number, adoption }); adopted &= adoption.Accepted;
                    using var comparison = plan.Compare(page.Number, a, b); pages.Add(comparison.Describe());
                    Save(comparison.ContentA, $"p{page.Number}-C-A.png"); Save(comparison.ContentB, $"p{page.Number}-C-B.png");
                    Save(comparison.Content.RawMask, $"p{page.Number}-C-raw.png"); Save(comparison.Display.Comparison.RawMask, $"p{page.Number}-D-raw.png");
                    var surface = page.Built!.Surface!;
                    using var da = surface.DisplayMap.Render(a, PageSpace.A); using var db = surface.DisplayMap.Render(b, PageSpace.B);
                    Save(da, $"p{page.Number}-D-A.png"); Save(db, $"p{page.Number}-D-B.png");
                    projections.Add(new { page = page.Number, comparison.Content.RawPixels, clusters = comparison.Content.Clusters,
                        content_map = surface.ContentMap.Segments, display_map = surface.DisplayMap.Segments, surface.Pieces, structures = comparison.Display.StructuralChanges });
                }
                var input = new Input(adopted, false, lines.Select(r => new Row(r.Page.Side, r.Page.Page, r.Top, r.Height, r.Text)).ToArray(), verified, pages);
                var gaps = layouts.Where(l => l.Page.Key.Page <= Math.Min(pa.PageCount, pb.PageCount)).SelectMany(l => Gaps(l, layouts, verified)).ToArray();
                records.Add(new { run = name, test.Id, reverse, expected = new { grouped = test.Expected, causes = test.Second is null ? 1 : 2 },
                    product = new { product.Inference.Proposals, product.Links, product.Decision, maps = product.Pages.Select(p => new { p.Number, p.Built?.Status, p.Built?.Reason }) },
                    inferred, links, gate, input, adoptions, projections, coverage, gaps,
                    maps = planPages.Select(p => new { p.Number, p.Built?.Status, p.Built?.Reason }),
                    layouts = layouts.Select(l => new { key = l.Page.Key, l.HeaderEnd, l.FooterStart, l.BodyStart, l.BodyEnd, l.Pitch, l.Regular }),
                    original_comparisons = verifications.Sum(v => v.OriginalComparisons) });
                File.WriteAllText(Path.Combine(output, "diagnosis.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
                if (inspectSingleSupport)
                {
                    supportRecords.Add(new { run = name, minimum_support_bands = options.MinSupportBands,
                        maps = planPages.Select(p => new { p.Number, p.Built?.Status, p.Built?.Reason, p.Built?.Removed }),
                        same_page_body_matches = layouts.Where(l => l.Page.Key.Side == PageSpace.A).SelectMany(la =>
                            la.Body.SelectMany(a => layouts.Where(lb => lb.Page.Key == new PageFlowPageKey(PageSpace.B, la.Page.Key.Page))
                                .SelectMany(lb => lb.Body.Where(b => b.Text == a.Text).Select(b => new {
                                    page = la.Page.Key.Page, a.Text, baseline_a = a.Baseline, baseline_b = b.Baseline,
                                    dy = a.Baseline - b.Baseline })))) });
                    File.WriteAllText(Path.Combine(output, "local-support.json"), JsonSerializer.Serialize(supportRecords, AggregationProbe.Json));
                }
                Console.WriteLine($"{name}: 製品={product.Decision.Ready}, 診断={gate.Ready}, 採用={adopted}, " + string.Join(",", planPages.Select(p => p.Built?.Reason)));
                Mat Read(PageFlowPageKey key) { using var image = (key.Side == PageSpace.A ? pa : pb).ReadPage(key.Page, 300); return image.TakePixels(); }
                RowTextResult Text(PageFlowPageKey key, Mat image) => (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, image.Size(), 300);
                void Save(Mat image, string file) => File.WriteAllBytes(Path.Combine(target, file), image.ImEncode(".png"));
            }
        }
    }
    private static IEnumerable<object> Gaps(Layout layout, Layout[] layouts, Proposal[] links)
    {
        var other = layouts.SingleOrDefault(l => l.Page.Key.Side != layout.Page.Key.Side && l.Page.Key.Page == layout.Page.Key.Page);
        if (other is null) yield break;
        var common = layout.Body.Select(r => r.Text).Intersect(other.Body.Select(r => r.Text)).ToHashSet();
        for (var i = 0; i < layout.Body.Count;)
        {
            if (common.Contains(layout.Body[i].Text)) { i++; continue; }
            var start = i; while (i < layout.Body.Count && !common.Contains(layout.Body[i].Text)) i++;
            var band = new PageFlowBand(layout.Page.Key, layout.BodyStart + start * layout.Pitch, (i - start) * layout.Pitch);
            var before = layout.Body.Take(start).Count(r => common.Contains(r.Text)); var after = layout.Body.Skip(i).Count(r => common.Contains(r.Text));
            yield return new { band, text = layout.Body.Skip(start).Take(i - start).Select(r => r.Text), before, after,
                local = start > 0 && i < layout.Body.Count && before >= 2 && after >= 2,
                carry = links.Any(l => l.Source == band || l.Target == band) };
        }
    }
}
