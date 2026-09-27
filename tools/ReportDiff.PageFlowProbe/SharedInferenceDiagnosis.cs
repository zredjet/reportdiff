using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;
using static ReportDiff.Core.PageFlowInference;

/// <summary>製品未接続。固定PDFの境界別変位と全文一致行の跨ぎから、前段の失敗を独立診断する。</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal static class SharedInferenceDiagnosis
{
    internal sealed record Line(PageFlowPageKey Page, int Index, int Top, int Height, string Text, double Baseline, double Left);
    internal sealed record Shift(int Dy, IReadOnlyList<string> Text, bool Eligible);
    internal sealed record Boundary(int Page, PageSpace Side, IReadOnlyList<Shift> Shifts, IReadOnlyList<Line> Crossing,
        IReadOnlyList<Line> Counterparts, bool NoCommonCrossing, Proposal? Candidate, string? Reason);
    internal sealed record CandidateResult(string Status, string? Reason, IReadOnlyList<Boundary> Boundaries,
        IReadOnlyList<Proposal> Proposals, bool DisplacementSupport);

    internal static CandidateResult Infer(IReadOnlyList<Line> rows, IReadOnlyList<Layout> layouts, RowOptions options, int dpi, bool terminalUnpaired = false)
    {
        var a = Ordered(PageSpace.A); var b = Ordered(PageSpace.B);
        if (a.Length == 0 || b.Length == 0 || layouts.Any(l => !l.Regular)) return Fail("irregular_layout");
        if (a.Select(r => r.Text).Distinct().Count() != a.Length || b.Select(r => r.Text).Distinct().Count() != b.Length)
            return Fail("nonunique_body");
        var byA = a.ToDictionary(r => r.Text); var byB = b.ToDictionary(r => r.Text);
        if (!a.Where(r => byB.ContainsKey(r.Text)).Select(r => r.Text).SequenceEqual(b.Where(r => byA.ContainsKey(r.Text)).Select(r => r.Text)))
            return Fail("reordered_common_rows");
        var aa = layouts.Where(l => l.Page.Key.Side == PageSpace.A).Select(l => l.Page.Key.Page).Order().ToArray();
        var bb = layouts.Where(l => l.Page.Key.Side == PageSpace.B).Select(l => l.Page.Key.Page).Order().ToArray();
        if (!aa.SequenceEqual(Enumerable.Range(1, aa.Length)) || !bb.SequenceEqual(Enumerable.Range(1, bb.Length))
            || (terminalUnpaired ? Math.Min(aa.Length, bb.Length) < 2 || Math.Abs(aa.Length - bb.Length) != 1 : !aa.SequenceEqual(bb)))
            return Fail("incomplete_pages");
        var common = a.Where(r => byB.ContainsKey(r.Text)).Select(r => (A: r, B: byB[r.Text])).ToArray();
        if (common.Any(r => Math.Abs(r.A.Page.Page - r.B.Page.Page) > 1)) return Fail("nonadjacent_crossing");
        var boundaries = new List<Boundary>(); var proposals = new List<Proposal>(); var support = true;
        var maximum = Units.RoundPixels(options.MaxShiftMm, dpi);
        for (var page = 1; page < Math.Max(aa.Length, bb.Length); page++)
        {
            var allCrossing = common.Where(r => Math.Min(r.A.Page.Page, r.B.Page.Page) <= page && Math.Max(r.A.Page.Page, r.B.Page.Page) > page).ToArray();
            if (allCrossing.Any(r => r.A.Page.Page < r.B.Page.Page) && allCrossing.Any(r => r.B.Page.Page < r.A.Page.Page)) return Fail("opposing_crossing");
            foreach (var side in new[] { PageSpace.A, PageSpace.B })
            {
                var source = layouts.SingleOrDefault(l => l.Page.Key == new PageFlowPageKey(side, page));
                var targetSide = side == PageSpace.A ? PageSpace.B : PageSpace.A;
                var target = layouts.SingleOrDefault(l => l.Page.Key == new PageFlowPageKey(targetSide, page + 1));
                if (source is null || target is null) continue;
                var lookup = side == PageSpace.A ? byB : byA;
                var from = (side == PageSpace.A ? a : b).Where(r => r.Page.Page == page).ToArray();
                var pairs = from.Where(r => lookup.TryGetValue(r.Text, out var other) && other.Page.Page == page && Math.Round(other.Left - r.Left) == 0)
                    .Select(r => (Row: r, Dy: (int)Math.Round(lookup[r.Text].Baseline - r.Baseline))).ToArray();
                var shifts = pairs.GroupBy(r => r.Dy).OrderBy(g => g.Key).Select(g => new Shift(g.Key, g.Select(r => r.Row.Text).ToArray(),
                    g.Key > 0 && g.Key <= maximum && g.Count() >= options.MinSupportBands)).ToArray();
                var crossed = from.Where(r => lookup.TryGetValue(r.Text, out var other) && other.Page.Page == page + 1).ToArray();
                var counterparts = crossed.Select(r => lookup[r.Text]).ToArray(); Proposal? candidate = null; string? reason = null;
                if (crossed.Length > 0)
                {
                    var height = crossed.Sum(r => r.Height); var start = source.BodyEnd - height;
                    if (crossed.Any(r => r.Height != source.Pitch) || counterparts.Any(r => r.Height != source.Pitch)
                        || crossed.Select(r => r.Top).Where((top, i) => top != start + i * source.Pitch).Any()
                        || counterparts.Select(r => r.Top).Where((top, i) => top != target.BodyStart + i * source.Pitch).Any()
                        || source.Pitch != target.Pitch || height >= source.BodyEnd - source.BodyStart || target.BodyStart + height > target.FooterStart
                        || crossed.Zip(counterparts, (x, y) => Math.Round(y.Left - x.Left) == 0).Any(v => !v)) reason = "crossing_not_suffix_prefix";
                    else if (height > maximum) reason = "crossing_exceeds_maximum";
                    else
                    {
                        var samePageSupport = shifts.SingleOrDefault(s => s.Dy == height)?.Text.Count ?? 0;
                        // 支持不足でも後段を観測するが、仮の候補であり最終採用にはしない。
                        if (samePageSupport < options.MinSupportBands) { support = false; reason = "insufficient_displacement_support"; }
                        candidate = new(new(source.Page.Key, start, height), new(target.Page.Key, target.BodyStart, height),
                            crossed.Select(r => r.Text).ToArray(), samePageSupport, "candidate", null);
                        proposals.Add(candidate);
                    }
                    if (candidate is null) support = false;
                }
                boundaries.Add(new(page, side, shifts, crossed, counterparts, allCrossing.Length == 0, candidate, reason));
            }
        }
        if (boundaries.Any(bd => bd.Crossing.Count > 0 && bd.Candidate is null)) return new("skipped", "unproven_crossing", boundaries, proposals, false);
        return new("inferred", null, boundaries, proposals, support);
        Line[] Ordered(PageSpace side) => rows.Where(r => r.Page.Side == side).OrderBy(r => r.Page.Page).ThenBy(r => r.Top).ToArray();
        CandidateResult Fail(string reason) => new("skipped", reason, [], [], false);
    }

    internal static void Run(string root, string output)
    {
        if (Directory.Exists(output)) throw new ArgumentException("出力先が既に存在します。");
        Directory.CreateDirectory(output); var fixtures = Path.Combine(root, "tests/ReportDiff.Tests/Fixtures/page-flow-shared");
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "expected.json")));
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(fixtures, "sha256.json")))!;
        var records = new List<object>();
        var config = Path.Combine(output, "carry.yaml"); File.WriteAllText(config, "rows: {enabled: true, carry_enabled: true}\nreport: {raw_overlay: true}\n");
        foreach (var saved in expected.RootElement.EnumerateArray())
        {
            var id = saved.GetProperty("id").GetString()!; var reverse = saved.GetProperty("reverse").GetBoolean(); var name = saved.GetProperty("run").GetString()!;
            var directory = Path.Combine(output, name); Directory.CreateDirectory(directory);
            var pathA = Path.Combine(fixtures, id, reverse ? "b.pdf" : "a.pdf"); var pathB = Path.Combine(fixtures, id, reverse ? "a.pdf" : "b.pdf");
            foreach (var path in new[] { pathA, pathB }) Require(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == hashes[Path.GetRelativePath(fixtures, path)], "固定PDFハッシュ");
            using var pdfA = PdfReader.Open(pathA); using var pdfB = PdfReader.Open(pathB); using var textA = new PdfTextReader(pathA); using var textB = new PdfTextReader(pathB);
            var keys = Enumerable.Range(1, pdfA.PageCount).SelectMany(p => new[] { new PageFlowPageKey(PageSpace.A, p), new PageFlowPageKey(PageSpace.B, p) }).ToArray();
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys) { using var image = Read(key); Require(collector.Add(key, image, Text(key, image), .5), "記述収集"); }
            var document = collector.Complete()!; var options = new RowOptions { Enabled = true }; var parameters = new ComparisonParameters();
            var product = PageFlowPlan.Prepare(document, Read, parameters, options);
            var layouts = product.Inference.Layouts.A.Concat(product.Inference.Layouts.B).ToArray();
            var rows = layouts.SelectMany(l => l.Body.Select((r, i) => new Line(l.Page.Key, i, l.BodyStart + i * l.Pitch, l.Pitch, r.Text, r.Baseline, r.Bounds.Left))).ToArray();
            var inferred = Infer(rows, layouts, options, 300);
            // 跨ぎなしだけで端点不明の候補を消さない。全ての適格変位の仮端点に既存の非送り証明を要求する。
            var alternatives = inferred.Boundaries.Where(bd => bd.NoCommonCrossing)
                .SelectMany(bd => bd.Shifts.Where(s => s.Eligible).Select(shift =>
                {
                    var source = layouts.Single(l => l.Page.Key == new PageFlowPageKey(bd.Side, bd.Page));
                    var target = layouts.Single(l => l.Page.Key == new PageFlowPageKey(bd.Side == PageSpace.A ? PageSpace.B : PageSpace.A, bd.Page + 1));
                    var sourceText = source.Body.Where(l => l.Bounds.Top >= source.BodyEnd - shift.Dy && l.Bounds.Bottom <= source.BodyEnd).Select(l => l.Text).ToArray();
                    var targetText = target.Body.Where(l => l.Bounds.Top >= target.BodyStart && l.Bounds.Bottom <= target.BodyStart + shift.Dy).Select(l => l.Text).ToArray();
                    return new Proposal(new(source.Page.Key, source.BodyEnd - shift.Dy, shift.Dy), new(target.Page.Key, target.BodyStart, shift.Dy),
                        sourceText, shift.Text.Count, "skipped", sourceText.SequenceEqual(targetText) ? "unexpected_equal_text" : "text_mismatch");
                })).ToArray();
            var nonflow = PageFlowNonflow.Find(document, product.Inference with { Proposals = alternatives }, false);
            var allNonflowProven = nonflow.FailureReason is null && nonflow.Proofs.Count == alternatives.Length;
            var verifications = inferred.Proposals.Select(p =>
            {
                using var a = Read(p.Source!.Page); using var b = Read(p.Target!.Page);
                return PageFlowBandVerifier.Verify(p, document.Pages.Single(x => x.Key == p.Source.Page), document.Pages.Single(x => x.Key == p.Target.Page), a, b, parameters);
            }).ToArray();
            var links = verifications.Select(v => v.Proposal).ToArray(); var verified = links.Where(p => p.Status == "band_verified").ToArray();
            var pages = new List<PageFlowPlan.Page>(); var mapProofs = new List<PageFlowPageProof>();
            var gaps = new List<object>();
            foreach (var number in keys.Select(k => k.Page).Distinct())
            {
                using var a = Read(new(PageSpace.A, number)); using var b = Read(new(PageSpace.B, number));
                var la = layouts.Single(l => l.Page.Key == new PageFlowPageKey(PageSpace.A, number)); var lb = layouts.Single(l => l.Page.Key == new PageFlowPageKey(PageSpace.B, number));
                var built = PageFlowSurface.Create(la, lb, a, b, verified, parameters);
                pages.Add(new(number, built, false)); mapProofs.Add(new(number, built.Status == "built", built.Removed.Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray()));
                var common = la.Body.Select(l => l.Text).Intersect(lb.Body.Select(l => l.Text)).ToHashSet();
                foreach (var layout in new[] { la, lb })
                {
                    var index = 0;
                    while (index < layout.Body.Count)
                    {
                        if (common.Contains(layout.Body[index].Text)) { index++; continue; }
                        var first = index; while (index < layout.Body.Count && !common.Contains(layout.Body[index].Text)) index++;
                        var band = new PageFlowBand(layout.Page.Key, layout.BodyStart + first * layout.Pitch, (index - first) * layout.Pitch);
                        var before = layout.Body.Take(first).Count(l => common.Contains(l.Text)); var after = layout.Body.Skip(index).Count(l => common.Contains(l.Text));
                        gaps.Add(new { band, text = layout.Body.Skip(first).Take(index - first).Select(l => l.Text), before, after,
                            local = first > 0 && index < layout.Body.Count && before >= 2 && after >= 2,
                            verified_carry = verified.Any(p => p.Source == band || p.Target == band) });
                    }
                }
            }
            var gate = PageFlowRangeGate.Evaluate(document, inferred.Status == "inferred" && allNonflowProven, inferred.Reason ?? (allNonflowProven ? null : "unproven_nonflow_alternative"),
                links.Select(p => new PageFlowCandidate(p.Source, p.Target, p.Status == "band_verified", p.Reason)).ToArray(), mapProofs);
            // 反射は独立診断内だけ。製品API・製品出力へ変更を入れず既存採用評価をそのまま呼ぶ。
            var ctor = typeof(PageFlowPlan).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var plan = (PageFlowPlan)ctor.Invoke([document, pages.ToDictionary(p => p.Number, _ => parameters), product.Inference with { Proposals = inferred.Proposals }, verifications, links, pages.ToArray(), gate]);
            var adoptions = new List<PageFlowAdoptionResult>(); var described = new List<PageFlowAggregation.Page>(); var projections = new List<object>();
            foreach (var p in pages)
            {
                using var a = Read(new(PageSpace.A, p.Number)); using var b = Read(new(PageSpace.B, p.Number));
                Save(a, $"p{p.Number}-O-A.png"); Save(b, $"p{p.Number}-O-B.png");
                if (!gate.Ready)
                {
                    using var baseline = RowComparer.Compare(a, b, parameters, options, () => (Text(new(PageSpace.A, p.Number), a), Text(new(PageSpace.B, p.Number), b)));
                    described.Add(new(p.Number, true, baseline.Comparison.Clusters.Count, baseline.DifferenceCount, true, false, [])); continue;
                }
                adoptions.Add(PageFlowAdoption.Evaluate(plan, p.Number, a, b, parameters, options, Text(new(PageSpace.A, p.Number), a), Text(new(PageSpace.B, p.Number), b)));
                using var comparison = plan.Compare(p.Number, a, b); described.Add(comparison.Describe());
                Save(comparison.ContentA, $"p{p.Number}-C-A.png"); Save(comparison.ContentB, $"p{p.Number}-C-B.png"); Save(comparison.Content.RawMask, $"p{p.Number}-C-raw.png");
                Save(comparison.Display.Comparison.RawMask, $"p{p.Number}-D-raw.png");
                using var da = p.Built!.Surface!.DisplayMap.Render(a, PageSpace.A); using var db = p.Built.Surface.DisplayMap.Render(b, PageSpace.B);
                Save(da, $"p{p.Number}-D-A.png"); Save(db, $"p{p.Number}-D-B.png");
                projections.Add(new { page = p.Number, comparison.Content.RawPixels, content_clusters = comparison.Content.Clusters,
                    structures = comparison.Display.StructuralChanges, display_map = p.Built.Surface.DisplayMap.Segments,
                    content_map = p.Built.Surface.ContentMap.Segments, p.Built.Surface.Pieces });
            }
            var input = new PageFlowAggregation.Input(gate.Ready && inferred.DisplacementSupport && adoptions.All(a => a.Accepted), false,
                rows.Select(r => new PageFlowAggregation.Row(r.Page.Side, r.Page.Page, r.Top, r.Height, r.Text)).ToArray(), verified, described);
            var aggregate = PageFlowAggregation.Evaluate(input);
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var code = CliApplication.Run(["compare", pathA, pathB, "--config", config, "--out", Path.Combine(directory, "cli"), "--quiet"], stdout, stderr);
            Require(code == 1, "CLI終了コード" + stderr);
            var report = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllBytes(Path.Combine(directory, "cli/result.json")), ReportJson.Options)!;
            Require(report.Summary.DifferenceCount == saved.GetProperty("expected").GetProperty("difference_count").GetInt32()
                && report.Summary.AggregatedDifferenceCount == saved.GetProperty("expected").GetProperty("aggregated_difference_count").GetInt32(), "製品の既存件数");
            var audit = Audit(rows, layouts, inferred, options);
            records.Add(new { run = name, id, reverse, rows, layouts = layouts.Select(l => new { l.Page.Key, l.Pitch, l.BodyStart, l.BodyEnd, l.Regular }),
                product = new { product.Inference.Proposals, product.Links, product.Decision, report.Summary, report.PageFlow!.Status },
                experiment = new { inferred, links, gate, adoptions, gaps, input, aggregate, projections,
                    nonflow_alternatives = alternatives, nonflow, all_nonflow_proven = allNonflowProven,
                    maps = pages.Select(p => new { p.Number, p.Built!.Status, p.Built.Reason }), original_comparisons = verifications.Sum(v => v.OriginalComparisons) }, audit });
            File.WriteAllText(Path.Combine(output, "diagnosis.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
            Console.WriteLine($"{name}: 製品{report.Summary.DifferenceCount}→{report.Summary.AggregatedDifferenceCount}, 診断{aggregate.DifferenceCount}→{aggregate.AggregatedDifferenceCount}, 支持={inferred.DisplacementSupport}, gate={gate.Ready}, {aggregate.Reason}");
            Mat Read(PageFlowPageKey key) { using var image = (key.Side == PageSpace.A ? pdfA : pdfB).ReadPage(key.Page, 300); return image.Pixels.Clone(); }
            RowTextResult Text(PageFlowPageKey key, Mat image) => (key.Side == PageSpace.A ? textA : textB).ReadRowWords(key.Page, image.Size(), 300);
            void Save(Mat image, string path) => File.WriteAllBytes(Path.Combine(directory, path), image.ImEncode(".png"));
            void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(name + ": " + label); }
        }
    }

    private static object Audit(Line[] rows, Layout[] layouts, CandidateResult expected, RowOptions options)
    {
        var permutations = 0; var duplicateRejections = 0; var crossingRejections = 0; var orderRejections = 0;
        if (expected.Status != "inferred") return new { permutations, duplicateRejections, crossingRejections, orderRejections };
        for (var seed = 0; seed < 5; seed++)
        {
            var random = new Random(seed); var actual = Infer(rows.OrderBy(_ => random.Next()).ToArray(), layouts.Reverse().ToArray(), options, 300);
            if (JsonSerializer.Serialize(actual, AggregationProbe.Json) != JsonSerializer.Serialize(expected, AggregationProbe.Json)) throw new InvalidOperationException("列挙順で診断候補が変化");
            permutations++;
        }
        foreach (var row in rows)
        {
            if (Infer(rows.Append(row).ToArray(), layouts, options, 300).Status != "skipped") throw new InvalidOperationException("重複本文を診断候補へ採用");
            duplicateRejections++;
        }
        foreach (var boundary in expected.Boundaries.Where(b => b.Candidate is not null))
        foreach (var row in boundary.Counterparts)
        foreach (var changed in new[] { row with { Top = row.Top + 1 }, row with { Height = row.Height + 1 },
            row with { Left = row.Left + 1 }, row with { Page = new(row.Page.Side, row.Page.Page + 1) } })
        {
            var actual = Infer(rows.Select(r => r == row ? changed : r).ToArray(), layouts, options, 300);
            if (actual.Status != "skipped") throw new InvalidOperationException("跨ぎの位置・高さ・向きの破損を診断候補へ採用");
            crossingRejections++;
        }
        var common = rows.Where(r => r.Page.Side == PageSpace.A && rows.Any(b => b.Page.Side == PageSpace.B && b.Text == r.Text)).ToArray();
        for (var i = 1; i < common.Length; i++)
        {
            var x = common[i - 1]; var y = common[i];
            var actual = Infer(rows.Select(r => r == x ? r with { Text = y.Text } : r == y ? r with { Text = x.Text } : r).ToArray(), layouts, options, 300);
            if (actual.Status != "skipped") throw new InvalidOperationException("共通行の逆順を診断候補へ採用");
            orderRejections++;
        }
        return new { permutations, duplicateRejections, crossingRejections, orderRejections };
    }
}
