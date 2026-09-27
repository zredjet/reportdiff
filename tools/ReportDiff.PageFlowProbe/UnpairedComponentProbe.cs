using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using static ReportDiff.Core.PageFlowAggregation;

/// <summary>製品未接続の診断。末尾の片側ページを残した独立成分と、全画素の残余を検査する。</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal static class UnpairedComponentProbe
{
    private sealed record Scenario(string Id, string[][] A, string[][] B, bool Grouped, int? Aggregate, string Drawing = "", string Mutation = "none");
    internal static void Run(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output);
        var records = new List<object>(); var fixtures = new List<object>();
        foreach (var scenario in Cases())
        {
            var folder = Path.Combine(output, "inputs", scenario.Id); Directory.CreateDirectory(folder);
            foreach (var (side, rows) in new[] { ("a", scenario.A), ("b", scenario.B) })
            {
                var bytes = FlowFixture.Create(new(scenario.Id, rows.Sum(r => r.Length), scenario.Mutation), side == "b", explicitPages: rows,
                    pageDrawing: p => side == "b" && p == rows.Length - 1 ? scenario.Drawing : "");
                File.WriteAllBytes(Path.Combine(folder, side + ".pdf"), bytes);
                fixtures.Add(new { scenario.Id, side, expected_rows = rows, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
            }
            File.WriteAllText(Path.Combine(output, "fixtures.json"), JsonSerializer.Serialize(fixtures, AggregationProbe.Json));
            foreach (var reverse in new[] { false, true })
            {
                var name = scenario.Id + (reverse ? "-ba" : "-ab");
                var target = Path.Combine(output, name); Directory.CreateDirectory(target);
                var pathA = Path.Combine(folder, reverse ? "b.pdf" : "a.pdf"); var pathB = Path.Combine(folder, reverse ? "a.pdf" : "b.pdf");
                using var pdfA = PdfReader.Open(pathA); using var pdfB = PdfReader.Open(pathB);
                using var textA = new PdfTextReader(pathA); using var textB = new PdfTextReader(pathB);
                var keys = Enumerable.Range(1, pdfA.PageCount).Select(p => new PageFlowPageKey(PageSpace.A, p))
                    .Concat(Enumerable.Range(1, pdfB.PageCount).Select(p => new PageFlowPageKey(PageSpace.B, p))).ToArray();
                var collector = new PageFlowCollector(keys);
                foreach (var key in keys)
                {
                    using var image = Read(key); Save(image, $"p{key.Page}-O-{key.Side}.png");
                    Require(collector.Add(key, image, Text(key, image), .5), "記述収集");
                }
                var document = collector.Complete() ?? throw new InvalidOperationException(collector.FailureReason);
                var product = PageFlowPlan.Prepare(document, Read, new(), new() { Enabled = true, CarryEnabled = true });
                var layouts = product.Inference.Layouts.A.Concat(product.Inference.Layouts.B).ToArray();
                var rows = layouts.SelectMany(l => l.Body.Select((r, i) => new Row(l.Page.Key.Side, l.Page.Key.Page,
                    l.BodyStart + i * l.Pitch, l.Pitch, r.Text))).ToArray();
                var eligible = Math.Min(pdfA.PageCount, pdfB.PageCount) >= 2 && Math.Abs(pdfA.PageCount - pdfB.PageCount) == 1
                    && product.Inference.Layouts.Status == "prepared" && layouts.All(l => l.Regular)
                    && layouts.Select(l => l.Page.Key).ToHashSet().SetEquals(keys);
                var nonflow = eligible ? IndependentBoundary.Find(rows, product.Inference.Proposals) : [];
                var plan = Recheck(product, document, nonflow);
                var pages = new List<Page>(); var adoptions = new List<object>(); var projections = new List<object>();
                var coverage = new List<object>(); var adopted = plan.Decision.Ready;
                foreach (var page in plan.Pages)
                {
                    if (plan.Decision.Selections.Single(s => s.Page == page.Number).Choice == "unpaired")
                    {
                        var key = keys.Single(k => k.Page == page.Number); var layout = layouts.SingleOrDefault(l => l.Page.Key == key);
                        var bands = plan.Links.Where(l => l.Status == "band_verified").SelectMany(l => new[] { l.Source!, l.Target! }).Where(b => b.Page == key).ToArray();
                        using var image = Read(key);
                        long? residual = null;
                        if (layout is not null)
                        {
                            using var mask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black); long count = 0; var width = image.Width;
                            for (var y = layout.HeaderEnd; y < layout.FooterStart; y++)
                            for (var x = 0; x < width; x++)
                            {
                                if (bands.Any(b => y >= b.Top && y < b.Bottom)) continue;
                                var value = image.At<Vec3b>(y, x);
                                if (value.Item0 == 255 && value.Item1 == 255 && value.Item2 == 255) continue;
                                mask.Set(y, x, (byte)255); count++;
                            }
                            residual = count; Save(mask, $"p{key.Page}-unpaired-residual.png");
                            Require(page.UnpairedCovered == (bands.Length > 0 && count == 0), "元画素から独立した残余確認");
                        }
                        coverage.Add(new { key, header_end = layout?.HeaderEnd, footer_start = layout?.FooterStart,
                            bands, residual_pixels = residual, page.UnpairedCovered });
                        pages.Add(new(page.Number, false, 0, 0, false, page.UnpairedCovered, [])); continue;
                    }
                    using var a = Read(new(PageSpace.A, page.Number)); using var b = Read(new(PageSpace.B, page.Number));
                    if (!plan.Decision.Ready)
                    {
                        using var baseline = RowComparer.Compare(a, b, new(), new() { Enabled = true },
                            () => (Text(new(PageSpace.A, page.Number), a), Text(new(PageSpace.B, page.Number), b)));
                        pages.Add(new(page.Number, true, baseline.Comparison.Clusters.Count, baseline.DifferenceCount,
                            baseline.Display?.DifferenceCountComplete ?? baseline.Status != "too_different", false, [])); continue;
                    }
                    var adoption = PageFlowAdoption.Evaluate(plan, page.Number, a, b, new(), new() { Enabled = true },
                        Text(new(PageSpace.A, page.Number), a), Text(new(PageSpace.B, page.Number), b));
                    adoptions.Add(new { page = page.Number, adoption }); adopted &= adoption.Accepted;
                    using var compared = plan.Compare(page.Number, a, b); pages.Add(compared.Describe());
                    var surface = page.Built!.Surface!;
                    Save(compared.ContentA, $"p{page.Number}-C-A.png"); Save(compared.ContentB, $"p{page.Number}-C-B.png");
                    Save(compared.Content.RawMask, $"p{page.Number}-C-raw.png"); Save(compared.Display.Comparison.RawMask, $"p{page.Number}-D-raw.png");
                    using var da = surface.DisplayMap.Render(a, PageSpace.A); using var db = surface.DisplayMap.Render(b, PageSpace.B);
                    Save(da, $"p{page.Number}-D-A.png"); Save(db, $"p{page.Number}-D-B.png");
                    projections.Add(new { page = page.Number, compared.Content.RawPixels, clusters = compared.Content.Clusters,
                        content_map = surface.ContentMap.Segments, display_map = surface.DisplayMap.Segments, surface.Pieces,
                        structures = compared.Display.StructuralChanges });
                }
                var input = new Input(adopted, false, rows, plan.Links.Where(l => l.Status == "band_verified").ToArray(), pages);
                var legacy = PageFlowAggregation.Evaluate(input);
                var candidate = IndependentCauseAggregation.Evaluate(input);
                var matched = (candidate.Status == "grouped") == scenario.Grouped && (!scenario.Grouped || candidate.AggregatedDifferenceCount == scenario.Aggregate);
                records.Add(new { run = name, scenario.Id, reverse, expected = new { scenario.Grouped, scenario.Aggregate }, matched,
                    product_gate = product.Decision, product_links = product.Links,
                    product_aggregation = product.Decision.Ready && adopted ? product.Aggregate(pages, false) : null,
                    nonflow, gate = plan.Decision, input, legacy, candidate,
                    adoptions, projections, coverage, audit = Audit(input, candidate),
                    layouts = layouts.Select(l => new { key = l.Page.Key, l.HeaderEnd, l.FooterStart, l.BodyStart, l.BodyEnd, l.Pitch, l.Regular }) });
                File.WriteAllText(Path.Combine(output, "unpaired-components.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
                Console.WriteLine($"{name}: 元ゲート={product.Decision.Ready}, 診断ゲート={adopted}, {candidate.DifferenceCount}→{candidate.AggregatedDifferenceCount}, {candidate.Status}/{candidate.Reason}, 期待一致={matched}");
                Mat Read(PageFlowPageKey key) { using var image = (key.Side == PageSpace.A ? pdfA : pdfB).ReadPage(key.Page, 300); return image.TakePixels(); }
                RowTextResult Text(PageFlowPageKey key, Mat image) => (key.Side == PageSpace.A ? textA : textB).ReadRowWords(key.Page, image.Size(), 300);
                void Save(Mat image, string file) => File.WriteAllBytes(Path.Combine(target, file), image.ImEncode(".png"));
                void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(name + ": " + label); }
            }
        }
    }

    private static PageFlowPlan Recheck(PageFlowPlan original, PageFlowDocumentDescriptor document, IReadOnlyList<IndependentBoundary.Proof> proofs)
    {
        if (original.Decision.Ready || proofs.Count == 0) return original;
        var indices = proofs.Select(p => p.CandidateIndex).ToHashSet();
        var retained = original.Links.Where((_, i) => !indices.Contains(i)).ToArray();
        var verified = retained.Where(l => l.Status == "band_verified").SelectMany(l => new[] { l.Source!, l.Target! }).ToArray();
        var pageProofs = original.Pages.Select(p => new PageFlowPageProof(p.Number, p.Built?.Status == "built",
            p.Built is null ? verified.Where(b => b.Page.Page == p.Number).ToArray()
                : p.Built.Removed.Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray())).ToArray();
        var gate = PageFlowRangeGate.Evaluate(document, original.Inference.Layouts.Status == "prepared", original.Inference.Layouts.Reason,
            retained.Select(l => new PageFlowCandidate(l.Source, l.Target, l.Status == "band_verified", l.Reason)).ToArray(), pageProofs);
        // 診断だけで既存写像の採用検査を呼ぶ。製品の非送り証拠や予算を偽造せず、CLIへこの計画を渡さない。
        var constructor = typeof(PageFlowPlan).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return (PageFlowPlan)constructor.Invoke([document, original.Pages.ToDictionary(p => p.Number, _ => new ComparisonParameters()),
            original.Inference, original.Verifications.ToArray(), original.Links.ToArray(), original.Pages.ToArray(), gate]);
    }

    internal static object Audit(Input input, Decision candidate)
    {
        var rejected = 0; var permutations = 0; var contentPreserved = 0; var incomplete = 0;
        if (JsonSerializer.Serialize(IndependentCauseAggregation.Evaluate(input), AggregationProbe.Json) != JsonSerializer.Serialize(candidate, AggregationProbe.Json))
            throw new InvalidOperationException("再読込した診断入力の集約が一致しません。");
        if (candidate.Status != "grouped") return new { rejected, permutations, contentPreserved, incomplete };
        Reject(input with { GateReady = false }); Reject(input with { SelectionLimited = true });
        foreach (var page in input.Pages.Where(p => !p.Paired))
        {
            Reject(input with { Pages = input.Pages.Select(p => p == page ? p with { UnpairedCovered = false } : p).ToArray() });
            Reject(input with { Pages = input.Pages.Select(p => p == page ? p with { Clusters = 1, DifferenceCount = 1 } : p).ToArray() });
        }
        foreach (var link in input.Links)
        {
            Reject(input with { Links = input.Links.Where(l => l != link).ToArray() });
            Reject(input with { Links = input.Links.Append(link).ToArray() });
            Reject(input with { Links = input.Links.Select(l => l == link ? l with { Status = "skipped", Reason = "nonidentical_band_not_proven" } : l).ToArray() });
        }
        foreach (var page in input.Pages)
        {
            Reject(input with { Pages = input.Pages.Where(p => p != page).ToArray() });
            Reject(input with { Pages = input.Pages.Append(page).ToArray() });
            Reject(input with { Pages = input.Pages.Select(p => p == page ? p with { DifferenceCount = p.DifferenceCount + 1 } : p).ToArray() });
            if (page.Paired)
            {
                var added = IndependentCauseAggregation.Evaluate(input with { Pages = input.Pages.Select(p => p == page
                    ? p with { Clusters = p.Clusters + 1, DifferenceCount = p.DifferenceCount + 1 } : p).ToArray() });
                if (added.Status != "grouped" || added.AggregatedDifferenceCount != candidate.AggregatedDifferenceCount + 1)
                    throw new InvalidOperationException("内容クラスタが集約で失われました。");
                contentPreserved++;
            }
            foreach (var structure in page.Structures)
            {
                Reject(input with { Pages = input.Pages.Select(p => p == page ? p with { Structures = p.Structures.Where(s => s != structure).ToArray(), DifferenceCount = p.DifferenceCount - 1 } : p).ToArray() });
                Reject(input with { Pages = input.Pages.Select(p => p == page ? p with { Structures = p.Structures.Append(structure).ToArray(), DifferenceCount = p.DifferenceCount + 1 } : p).ToArray() });
                Reject(input with { Pages = input.Pages.Select(p => p == page ? p with { Structures = p.Structures.Select(s => s == structure ? s with { Excluded = true } : s).ToArray(), DifferenceCount = p.DifferenceCount - 1 } : p).ToArray() });
            }
        }
        foreach (var row in input.Rows)
        {
            Reject(input with { Rows = input.Rows.Append(row).ToArray() });
            Reject(input with { Rows = input.Rows.Where(r => r != row).ToArray() });
            Reject(input with { Rows = input.Rows.Select(r => r == row ? r with { Start = r.Start + 1 } : r).ToArray() });
        }
        var claimedComplete = IndependentCauseAggregation.Evaluate(input with { Pages = input.Pages.Select(p => p with { Complete = true }).ToArray() });
        if (claimedComplete.Status != "grouped" || claimedComplete.DifferenceCountComplete || claimedComplete.AggregatedDifferenceCountComplete != false)
            throw new InvalidOperationException("片側ページの網羅性が引き上げられました。");
        incomplete++;
        for (var seed = 0; seed < 3; seed++)
        {
            var random = new Random(seed);
            var result = IndependentCauseAggregation.Evaluate(input with { Rows = input.Rows.OrderBy(_ => random.Next()).ToArray(),
                Pages = input.Pages.OrderBy(_ => random.Next()).ToArray(), Links = input.Links.OrderBy(_ => random.Next()).ToArray() });
            if (JsonSerializer.Serialize(result, AggregationProbe.Json) != JsonSerializer.Serialize(candidate, AggregationProbe.Json))
                throw new InvalidOperationException("列挙順で集約が変化しました。");
            permutations++;
        }
        return new { rejected, permutations, contentPreserved, incomplete };
        void Reject(Input altered)
        {
            var result = IndependentCauseAggregation.Evaluate(altered);
            if (result.Status == "grouped" || result.Groups.Count != 0 || result.AggregatedDifferenceCount != result.DifferenceCount)
                throw new InvalidOperationException("破損した集約入力を採用しました。");
            rejected++;
        }
    }

    private static Scenario[] Cases()
    {
        var cases = new List<Scenario>();
        Single("single-r11", 12, true, 1); Single("single-chain", 18, true, 1); Single("insufficient-fixed", 6, false, null);
        Single("residual-tone", 12, false, null, drawing: "0.85 g 130 155 25 8 re f\n");
        Single("residual-faint", 12, false, null, drawing: "0.9960784314 g 130 155 2.4 0.72 re f\n");
        Single("fixed-header-change", 12, false, null, drawing: "0.5 g 40 264 4.8 0.48 re f\n");
        Single("crossing-number-change", 12, false, null, number: true);
        Single("shared-unpaired", 12, false, null, shared: true);
        Independent("independent-terminal", false, false, true, 2);
        Independent("independent-opposite", true, false, true, 2);
        Independent("neutral-between", false, true, true, 2);
        Independent("independent-tone", false, false, true, 3, mutation: "paired");
        Independent("independent-residual", false, false, false, null, drawing: "0.85 g 130 155 25 8 re f\n");
        Independent("independent-faint", false, false, false, null, drawing: "0.9960784314 g 130 155 2.4 0.72 re f\n");
        Independent("repeated-components", false, false, false, null, repeated: true);
        var baseCase = cases.Single(c => c.Id == "single-r11");
        cases.Add(baseCase with { Id = "unlinked-last-page", B = baseCase.B.Append(Array.Empty<string>()).ToArray(), Grouped = false, Aggregate = null });
        return cases.ToArray();
        void Single(string id, int count, bool grouped, int? aggregate, string drawing = "", bool number = false, bool shared = false)
        {
            var f = new FlowCase(id, count); var a = FlowFixture.Rows(f, false); var b = FlowFixture.Rows(f, true).ToList();
            if (number) b[^1] = "SUM TOTAL 556";
            if (shared) b.Insert(8, "SECOND ADDED");
            cases.Add(new(id, a.Chunk(6).ToArray(), b.Chunk(6).ToArray(), grouped, aggregate, drawing));
        }
        void Independent(string id, bool opposite, bool neutral, bool grouped, int? aggregate, string drawing = "", string mutation = "none", bool repeated = false)
        {
            var a = new List<string[]>(); var b = new List<string[]>();
            for (var i = 0; i < 2; i++)
            {
                var prefix = repeated ? "SAME" : $"SECTION {(char)('A' + i)}";
                var original = Enumerable.Range(0, i == 0 ? 10 : 12).Select(n => $"{prefix} ITEM {(char)('A' + n)}{(char)('A' + n)}").ToList();
                original[^1] = $"{prefix} TOTAL 555"; var revised = original.ToList(); revised.Insert(2, $"{prefix} NEW ADDED");
                a.AddRange((opposite && i == 0 ? revised : original).Chunk(6)); b.AddRange((opposite && i == 0 ? original : revised).Chunk(6));
                if (neutral && i == 0) { string[] idle = ["IDLE FIRST", "IDLE NEXT", "IDLE LAST"]; a.Add(idle); b.Add(idle); }
            }
            cases.Add(new(id, a.ToArray(), b.ToArray(), grouped, aggregate, drawing, mutation));
        }
    }
}
