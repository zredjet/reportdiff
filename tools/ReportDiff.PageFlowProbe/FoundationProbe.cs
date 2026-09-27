using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;

internal static class FoundationProbe
{
    internal static void Run(string inputs, string candidates, string output)
    {
        if (File.Exists(output)) throw new ArgumentException("出力先が存在します。");
        using var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(candidates, "candidates.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(inputs, "observations.json")));
        var inputHashes = manifest.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("test").GetProperty("id").GetString()!);
        var records = new List<object>();
        foreach (var run in saved.RootElement.EnumerateArray())
        {
            var id = run.GetProperty("id").GetString()!; var name = run.GetProperty("folder").GetString()!;
            var reverse = run.GetProperty("reverse").GetBoolean(); var result = run.GetProperty("result");
            foreach (var side in new[] { "a", "b" })
                if (System.Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(inputs, id, side + ".pdf"))))
                    != inputHashes[id].GetProperty(side + "_sha256").GetString()) throw new InvalidOperationException("固定PDFが変わっています。");
            using var textA = new PdfTextReader(Path.Combine(inputs, id, reverse ? "b.pdf" : "a.pdf"));
            using var textB = new PdfTextReader(Path.Combine(inputs, id, reverse ? "a.pdf" : "b.pdf"));
            var keys = new[] { "a", "b" }.SelectMany(side => result.GetProperty("pages_read_" + side).EnumerateArray()
                .Select(p => new PageFlowPageKey(side == "a" ? PageSpace.A : PageSpace.B, p.GetInt32()))).ToArray();
            var collector = new PageFlowCollector(keys); var old = new List<CandidateInference.Page>();
            var timer = Stopwatch.StartNew();
            foreach (var key in keys)
            {
                var side = key.Side == PageSpace.A ? "a" : "b";
                using var image = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(candidates, name, $"{side}-p{key.Page}.png")), ImreadModes.Color);
                var text = (key.Side == PageSpace.A ? textA : textB).ReadRowWords(key.Page, image.Size(), 300);
                if (!collector.Add(key, image, text, .5)) throw new InvalidOperationException(collector.FailureReason);
                old.Add(CandidateInference.Describe(key.Page, image, text));
            }
            var document = collector.Complete() ?? throw new InvalidOperationException(collector.FailureReason);
            for (var i = 0; i < keys.Length; i++)
            {
                var page = document.Pages.Single(p => p.Key == keys[i]); var previous = old[i];
                if (!page.Lines.Select(l => l.Text).SequenceEqual(previous.Lines.Select(CandidateInference.Text))
                    || !page.Lines.Select(l => (l.Bounds, l.Baseline)).SequenceEqual(previous.Lines.Select(l => (l.Bounds, l.Baseline)))
                    || Enumerable.Range(0, page.Size.Height).Any(y => page.RowHasNonwhite(y) != previous.Nonwhite[y]))
                    throw new InvalidOperationException("保持した本文・座標・非白行が独立ツールと異なります。");
                // すべての行を個別照合。別記述データ間でもハッシュ比較が同じ結果になること。
                var partner = document.Pages[0]; var originalPartner = old[Array.IndexOf(keys, partner.Key)];
                if (page.Size != partner.Size) continue;
                for (var y = 0; y < page.Size.Height; y++)
                    if (page.SameRowHashes(partner, y, y, 1) != (previous.RowHashes[y] == originalPartner.RowHashes[y]))
                        throw new InvalidOperationException("行ハッシュの一致判定が異なります。");
            }
            var links = result.GetProperty("links").Deserialize<CandidateInference.Proposal[]>(AggregationProbe.Json)!
                .Select(l => new PageFlowCandidate(Convert(l.Source), Convert(l.Target), l.Status == "band_verified", l.Reason)).ToArray();
            var proofs = result.GetProperty("pages").EnumerateArray().Select(p =>
            {
                var paired = !p.GetProperty("status").GetString()!.StartsWith("only_in_", StringComparison.Ordinal);
                var built = p.GetProperty("status").GetString() == "built";
                var bands = !paired ? p.GetProperty("verified_bands").Deserialize<CandidateInference.Band[]>(AggregationProbe.Json)! : built
                    ? p.GetProperty("removed").EnumerateArray().Where(r => r.GetProperty("proof").GetString() == "verified_carry_range")
                        .Select(r => r.GetProperty("band").Deserialize<CandidateInference.Band>(AggregationProbe.Json)!).ToArray() : [];
                return new PageFlowPageProof(p.GetProperty("page").GetInt32(), built, bands.Select(b => Convert(b)!).ToArray());
            }).ToArray();
            var actual = PageFlowRangeGate.Evaluate(document, result.GetProperty("status").GetString() == "prepared", result.GetProperty("reason").GetString(), links, proofs);
            var expected = result.GetProperty("gate").Deserialize<DocumentGate.Decision>(AggregationProbe.Json)!;
            if (actual.Ready != expected.Ready || !actual.Selections.Select(s => (s.Page, s.Choice)).SequenceEqual(expected.Selections.OrderBy(s => s.Page).Select(s => (s.Page, s.Choice)))
                || actual.SelectedLinks.Count != expected.SelectedLinks.Count
                || expected.SelectedLinks.Any(l => !actual.SelectedLinks.Any(a => a.Source == Convert(l.Source) && a.Target == Convert(l.Target))))
                throw new InvalidOperationException("文書の採否・選択リンク・基準比較への復帰が異なります。");
            timer.Stop();
            records.Add(new { run = name, actual.Ready, actual.Reasons, selected_links = actual.SelectedLinks.Count, document.Usage,
                descriptors_match = true, range_decision_matches = true, elapsed_ms = timer.Elapsed.TotalMilliseconds });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(records, AggregationProbe.Json));
        Console.WriteLine($"{records.Count}実行の記述データ・文書採否・選択リンクが一致しました。");
    }

    internal static void Measure(string scenario, string output)
    {
        if (File.Exists(output)) throw new ArgumentException("出力先が存在します。");
        if (scenario is "candidates-128" or "candidates-129") { MeasureCandidates(scenario, output); return; }
        var (count, width, height, lineCount) = scenario switch
        {
            "a4-2" => (4, 2480, 3508, 50), "a4-16" => (32, 2480, 3508, 50),
            "a4-30" => (60, 2480, 3508, 50), "a4-31" => (62, 2480, 3508, 50),
            "dense-16" => (16, 200, 4000, 2000), "dense-17" => (17, 200, 4000, 2000),
            "page-128" => (128, 20, 100, 0), "page-129" => (129, 20, 100, 0),
            "metadata-127" => (127, 1, 16000, 0), "metadata-128" => (128, 1, 16000, 0),
            _ => throw new ArgumentException("未知の計測条件です。")
        };
        var paired = scenario.StartsWith("a4-", StringComparison.Ordinal);
        var keys = Enumerable.Range(0, count).Select(i => new PageFlowPageKey(paired && i % 2 != 0 ? PageSpace.B : PageSpace.A,
            paired ? i / 2 + 1 : i + 1)).ToArray();
        var collector = new PageFlowCollector(keys);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true); var allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
        if (collector.FailureReason is null)
        {
            // 同じラスタを一枚だけ使う記述処理の測定。PDFの描画・比較・保存は含まない。
            using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
            var words = Enumerable.Range(0, lineCount).Select(i => new RowWord($"ROW {i:D4} VALUE 12345", new(1, i * 2, width - 1, i * 2 + 1), [i * 2 + 1])).ToArray();
            foreach (var key in keys) if (!collector.Add(key, image, new("available", null, words), .5)) break;
        }
        var document = collector.Complete(); clock.Stop();
        var live = GC.GetTotalMemory(true) - before;
        var record = new { scenario, descriptor_only = true, requested_images = count, accepted = document is not null,
            collector.FailureReason, collector.Usage, elapsed_ms = clock.Elapsed.TotalMilliseconds,
            managed_allocated_bytes = GC.GetTotalAllocatedBytes(true) - allocated, managed_retained_delta_bytes = live };
        GC.KeepAlive(document);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(record, AggregationProbe.Json));
        Console.WriteLine($"{scenario}: {record.accepted} / {collector.FailureReason} / {clock.Elapsed.TotalMilliseconds:F1}ms / 計上{collector.Usage.DescriptorBytes}B");
    }

    private static void MeasureCandidates(string scenario, string output)
    {
        var count = scenario == "candidates-128" ? 128 : 129;
        var keys = new[] { new PageFlowPageKey(PageSpace.A, 1), new(PageSpace.B, 1), new(PageSpace.A, 2), new(PageSpace.B, 2) };
        var collector = new PageFlowCollector(keys);
        using (var image = new Mat(300, 20, MatType.CV_8UC3, Scalar.White))
            foreach (var key in keys) collector.Add(key, image, new("available", null, []), .5);
        var document = collector.Complete()!;
        var sources = Enumerable.Range(0, count).Select(i => new PageFlowBand(keys[0], i, 1)).ToArray();
        var targets = Enumerable.Range(0, count).Select(i => new PageFlowBand(keys[3], i, 1)).ToArray();
        var links = sources.Zip(targets, (s, t) => new PageFlowCandidate(s, t, true, null)).ToArray();
        var clock = Stopwatch.StartNew();
        var result = PageFlowRangeGate.Evaluate(document, true, null, links, [new(1, true, sources), new(2, true, targets)]);
        clock.Stop();
        if (result.Ready != (count == 128) || result.SelectedLinks.Count != (count == 128 ? 128 : 0))
            throw new InvalidOperationException("候補上限の採否が異なります。");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { scenario, range_gate_only = true,
            candidates = count, accepted = result.Ready, result.Reasons, selected_links = result.SelectedLinks.Count,
            elapsed_ms = clock.Elapsed.TotalMilliseconds }, AggregationProbe.Json));
        Console.WriteLine($"{scenario}: {result.Ready} / {clock.Elapsed.TotalMilliseconds:F1}ms");
    }
    private static PageFlowBand? Convert(CandidateInference.Band band) => band.Length <= 0 ? null :
        new(new(band.Side == "a" ? PageSpace.A : PageSpace.B, band.Page), band.Start, band.Length);
}
