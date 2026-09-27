using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;

internal static class CoreReplay
{
    internal static void Run(string inputs, string candidates, string aggregation, string output)
    {
        if (File.Exists(output)) throw new ArgumentException("出力先が存在します。");
        using var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(candidates, "candidates.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(inputs, "observations.json")));
        using var aggregates = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(aggregation, "aggregation.json")));
        var hashes = manifest.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("test").GetProperty("id").GetString()!);
        var expectedAggregates = aggregates.RootElement.GetProperty("records").EnumerateArray().ToDictionary(r => r.GetProperty("run").GetString()!);
        var records = new List<object>();
        foreach (var run in saved.RootElement.EnumerateArray())
        {
            var id = run.GetProperty("id").GetString()!; var name = run.GetProperty("folder").GetString()!;
            var reverse = run.GetProperty("reverse").GetBoolean(); var expected = run.GetProperty("result");
            foreach (var side in new[] { "a", "b" })
                Require(System.Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(inputs, id, side + ".pdf"))))
                    == hashes[id].GetProperty(side + "_sha256").GetString(), "固定PDFハッシュ");
            using var textA = new PdfTextReader(Path.Combine(inputs, id, reverse ? "b.pdf" : "a.pdf"));
            using var textB = new PdfTextReader(Path.Combine(inputs, id, reverse ? "a.pdf" : "b.pdf"));
            var keys = new[] { "a", "b" }.SelectMany(side => expected.GetProperty("pages_read_" + side).EnumerateArray()
                .Select(p => new PageFlowPageKey(side == "a" ? PageSpace.A : PageSpace.B, p.GetInt32()))).ToArray();
            var timer = Stopwatch.StartNew(); var collector = new PageFlowCollector(keys);
            foreach (var key in keys)
            {
                using var image = LoadOriginal(key);
                Require(collector.Add(key, image, ReadText(key, image), .5), "記述収集");
            }
            var document = collector.Complete() ?? throw new InvalidOperationException(collector.FailureReason);
            // ここまでの入力は元画像・PDFのテキスト・選択ページだけ。正解帯・写像・IDはCoreへ渡さない。
            var plan = PageFlowPlan.Prepare(document, LoadOriginal, new(), new() { Enabled = true });
            Require(plan.Inference.Layouts.Status == expected.GetProperty("status").GetString(), "推定状態");
            Require(plan.Inference.Layouts.Reason == expected.GetProperty("reason").GetString(), "推定理由");
            var expectedLinks = expected.GetProperty("links").Deserialize<CandidateInference.Proposal[]>(AggregationProbe.Json)!;
            Require(plan.Links.Count == expectedLinks.Length, "候補数");
            foreach (var (actual, previous) in plan.Links.Zip(expectedLinks))
                Require(actual.Source == Band(previous.Source) && actual.Target == Band(previous.Target)
                    && actual.Text.SequenceEqual(previous.Text) && actual.Support == previous.Support
                    && actual.Status == previous.Status && actual.Reason == previous.Reason, "候補帯・本文・支持・画像検証");
            var gate = expected.GetProperty("gate").Deserialize<DocumentGate.Decision>(AggregationProbe.Json)!;
            Require(plan.Decision.Ready == gate.Ready && plan.Decision.Selections.Select(s => (s.Page, s.Choice))
                .SequenceEqual(gate.Selections.OrderBy(s => s.Page).Select(s => (s.Page, s.Choice))), "全体採否");
            var pages = new List<PageFlowAggregation.Page>(); var comparisons = 0; var projections = 0;
            foreach (var item in expected.GetProperty("pages").EnumerateArray())
            {
                var number = item.GetProperty("page").GetInt32(); var actual = plan.Pages.Single(p => p.Number == number);
                if (item.GetProperty("status").GetString()!.StartsWith("only_in_", StringComparison.Ordinal))
                {
                    Require(actual.UnpairedCovered == item.GetProperty("only_verified_bands_and_fixed_parts").GetBoolean(), "片側ページ残余");
                    pages.Add(new(number, false, 0, 0, false, actual.UnpairedCovered, [])); continue;
                }
                using var a = LoadOriginal(new(PageSpace.A, number)); using var b = LoadOriginal(new(PageSpace.B, number));
                Require((actual.Built?.Status ?? "skipped") == item.GetProperty("status").GetString(), "C/D構築採否");
                if (actual.Built?.Surface is { } surface)
                {
                    Require(Serialize(actual.Built.Map!.Segments) == item.GetProperty("segments").GetRawTextNormalized(), "D写像");
                    Require(Serialize(surface.ContentMap.Segments) == item.GetProperty("content_segments").GetRawTextNormalized(), "C写像");
                    using var ca = surface.ContentMap.Render(a, PageSpace.A); using var cb = surface.ContentMap.Render(b, PageSpace.B);
                    Match(ca, $"p{number}-c-a.png"); Match(cb, $"p{number}-c-b.png");
                    foreach (var (profile, diff) in new[] { ("normal", new DiffOptions()), ("strict", new DiffOptions { MaxShiftMm = 0, EdgeTolerance = 0 }), ("loose", new DiffOptions { MaxShiftMm = .30 }) })
                    {
                        using var content = PageComparer.Compare(ca, cb, new() { Diff = diff });
                        Match(content.RawMask, $"p{number}-{profile}-raw.png", ImreadModes.Grayscale); comparisons++;
                    }
                }
                if (plan.Decision.Ready)
                {
                    using var comparison = plan.Compare(number, a, b);
                    Match(comparison.Content.RawMask, $"p{number}-selected-raw.png", ImreadModes.Grayscale);
                    using var oldDisplay = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(aggregation, name, $"p{number}-display-raw.png")), ImreadModes.Grayscale);
                    Require(Cv2.Norm(comparison.Display.Comparison.RawMask, oldDisplay, NormTypes.INF) == 0, "D生差分");
                    var projection = expectedAggregates[name].GetProperty("projections").EnumerateArray().Single(p => p.GetProperty("page").GetInt32() == number);
                    Require(Serialize(comparison.Display.StructuralChanges) == projection.GetProperty("structures").GetRawTextNormalized(), "実構造IDと座標");
                    Require(Serialize(comparison.Display.Comparison.Clusters) == projection.GetProperty("clusters").GetRawTextNormalized(), "表示クラスタ");
                    Require(Serialize(comparison.Content.Clusters) == projection.GetProperty("content_clusters").GetRawTextNormalized(), "内容クラスタ");
                    pages.Add(comparison.Describe()); projections++;
                }
                else
                {
                    using var baseline = RowComparer.Compare(a, b, new(), new() { Enabled = true },
                        () => (ReadText(new(PageSpace.A, number), a), ReadText(new(PageSpace.B, number), b)));
                    Match(baseline.Comparison.RawMask, $"p{number}-selected-raw.png", ImreadModes.Grayscale);
                    var oldBaseline = item.GetProperty("baseline");
                    Require(baseline.DifferenceCount == oldBaseline.GetProperty("difference_count").GetInt32(), "基準比較件数");
                    pages.Add(new(number, true, baseline.Comparison.Clusters.Count, baseline.DifferenceCount,
                        baseline.Display?.DifferenceCountComplete ?? (baseline.Status != "too_different" && !baseline.Comparison.Warnings.Contains("CLUSTER_LIMIT")), false, []));
                }
            }
            var decision = plan.Aggregate(pages, run.TryGetProperty("selected", out _));
            Require(Serialize(Legacy(decision)) == expectedAggregates[name].GetProperty("decision").GetRawTextNormalized(), "因果集約の全フィールド");
            Require(Serialize(Legacy(plan.Aggregate(pages, run.TryGetProperty("selected", out _), false)))
                == expectedAggregates[name].GetProperty("disabled").GetRawTextNormalized(), "集約無効時");
            timer.Stop();
            records.Add(new { run = name, plan.Decision.Ready, inference_links = plan.Links.Count, original_comparisons = plan.Verifications.Sum(v => v.OriginalComparisons),
                content_comparisons = comparisons, projections, decision, elapsed_ms = timer.Elapsed.TotalMilliseconds, matched = true });
            Console.WriteLine($"{name}: Core一致 / {decision.Status} / {decision.DifferenceCount}→{decision.AggregatedDifferenceCount}");

            Mat LoadOriginal(PageFlowPageKey key)
            {
                Require(keys.Contains(key), "未選択ページの読込禁止");
                return Load($"{(key.Side == PageSpace.A ? "a" : "b")}-p{key.Page}.png", ImreadModes.Color);
            }
            RowTextResult ReadText(PageFlowPageKey key, Mat image) => (key.Side == PageSpace.A ? textA : textB).ReadRowWords(key.Page, image.Size(), 300);
            Mat Load(string file, ImreadModes mode) => Cv2.ImDecode(File.ReadAllBytes(Path.Combine(candidates, name, file)), mode);
            void Match(Mat image, string file, ImreadModes mode = ImreadModes.Color)
            { using var previous = Load(file, mode); Require(image.Size() == previous.Size() && Cv2.Norm(image, previous, NormTypes.INF) == 0, file); }
            void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException($"{name}: {label}が一致しません。"); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, Serialize(records));
    }

    internal static PageFlowBand? Band(CandidateInference.Band? b) => b is null || b.Length <= 0 ? null : new(new(Side(b.Side), b.Page), b.Start, b.Length);
    private static PageSpace Side(string side) => side == "a" ? PageSpace.A : PageSpace.B;
    internal static PageFlowAggregation.Input Convert(CausalAggregation.Input input) => new(input.GateReady, input.SelectionLimited,
        input.Rows.Select(r => new PageFlowAggregation.Row(Side(r.Side), r.Page, r.Start, r.Length, r.Text)).ToArray(),
        input.Links.Select(l => new PageFlowInference.Proposal(Band(l.Source), Band(l.Target), l.Text, l.Support, l.Status, l.Reason)).ToArray(),
        input.Pages.Select(p => new PageFlowAggregation.Page(p.Number, p.Paired, p.Clusters, p.DifferenceCount, p.Complete, p.UnpairedCovered,
            p.Structures.Select(s => new PageFlowAggregation.Structure(new(s.Reference.Page, s.Reference.StructuralChangeId), s.Kind, Band(s.A), Band(s.B), s.Dy, s.Excluded)).ToArray())).ToArray());
    internal static CausalAggregation.Decision Legacy(PageFlowAggregation.Decision d) => new(d.Status, d.Reason, d.DifferenceCount,
        d.AggregatedDifferenceCount, d.DifferenceCountComplete, d.AggregatedDifferenceCountComplete, d.Groups.Select(g => new CausalAggregation.Group(
            Ref(g.Cause), g.Structures.Select(Ref).ToArray(), g.AuxiliaryBands.Select(Band).ToArray(),
            g.Links.Select(l => new CandidateInference.Proposal(Band(l.Source!), Band(l.Target!), l.Text.ToArray(), l.Support, l.Status, l.Reason)).ToArray(),
            g.Balance.Select(b => new CausalAggregation.Balance(b.Page, b.RowsA, b.RowsB, b.CauseDelta, b.Incoming, b.Outgoing)).ToArray())).ToArray());
    private static CausalAggregation.Reference Ref(PageFlowAggregation.Reference r) => new(r.Page, r.StructuralChangeId);
    private static CandidateInference.Band Band(PageFlowBand b) => new(b.Page.Side == PageSpace.A ? "a" : "b", b.Page.Page, b.Top, b.Height);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, AggregationProbe.Json);
    private static string GetRawTextNormalized(this JsonElement value) => Serialize(value);
}
