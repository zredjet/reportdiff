using System.Runtime.Versioning;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal static class TerminalProductAudit
{
    internal static void Run(string fixtures, string output, bool shared = false)
    {
        if (File.Exists(output)) throw new ArgumentException("出力ファイルが既にあります。");
        using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "expectations.json")));
        var records = new List<object>();
        foreach (var record in data.RootElement.EnumerateArray().Where(r => shared ? r.GetProperty("id").GetString() is "before8" or "chain" or "tone" : r.GetProperty("nonflow_ids").GetArrayLength() > 0))
        {
            var name = record.GetProperty("id").GetString()!; var reverse = record.GetProperty("reverse").GetBoolean();
            var pathA = Path.Combine(fixtures, name, reverse ? "b.pdf" : "a.pdf"); var pathB = Path.Combine(fixtures, name, reverse ? "a.pdf" : "b.pdf");
            using var a = PdfReader.Open(pathA); using var b = PdfReader.Open(pathB);
            using var ta = new PdfTextReader(pathA); using var tb = new PdfTextReader(pathB);
            var keys = Enumerable.Range(1, a.PageCount).Select(p => new PageFlowPageKey(PageSpace.A, p))
                .Concat(Enumerable.Range(1, b.PageCount).Select(p => new PageFlowPageKey(PageSpace.B, p))).ToArray();
            var collector = new PageFlowCollector(keys);
            foreach (var key in keys)
            { using var image = Read(key); Require(collector.Add(key, image, Text(key, image), .5), "文書記述"); }
            var document = collector.Complete() ?? throw new InvalidOperationException("文書記述が不完全です。");
            var options = new RowOptions { Enabled = true, CarryEnabled = true };
            var plan = PageFlowPlan.Prepare(document, Read, new(), options);
            Require(plan.Decision.Ready, "文書範囲");
            var pages = new List<PageFlowAggregation.Page>(); var raw = new List<object>();
            foreach (var page in plan.Pages)
            {
                if (plan.Decision.Selections.Single(s => s.Page == page.Number).Choice == "unpaired")
                { pages.Add(new(page.Number, false, 0, 0, false, page.UnpairedCovered, [])); continue; }
                using var ia = Read(new(PageSpace.A, page.Number)); using var ib = Read(new(PageSpace.B, page.Number));
                var adoption = PageFlowAdoption.Evaluate(plan, page.Number, ia, ib, new(), options,
                    Text(new(PageSpace.A, page.Number), ia), Text(new(PageSpace.B, page.Number), ib));
                Require(adoption.Accepted, "既存の採用条件");
                using var compared = plan.Compare(page.Number, ia, ib);
                pages.Add(compared.Describe()); raw.Add(new { page = page.Number, compared.Content.RawPixels, adoption });
            }
            var result = plan.Aggregate(pages, false);
            var expected = record.GetProperty("candidate").Deserialize<PageFlowAggregation.Decision>(AggregationProbe.Json)!;
            Require(result.Status == "grouped" && result.DifferenceCount == expected.DifferenceCount
                && result.AggregatedDifferenceCount == expected.AggregatedDifferenceCount, "診断で固定した件数");
            if (shared)
            {
                var input = record.GetProperty("input").Deserialize<PageFlowAggregation.Input>(AggregationProbe.Json)!;
                Require(JsonSerializer.Serialize(pages, AggregationProbe.Json) == JsonSerializer.Serialize(input.Pages, AggregationProbe.Json), "診断の全実構造と内容件数");
                Require(result.SharedComponents is { Count: 1 } && result.SharedComponents[0].Causes.Count == 2
                    && result.SharedComponents[0].AuxiliaryBands is { Count: 1 }, "共有二原因と補助帯");
            }
            else Require(JsonSerializer.Serialize(result.Groups, AggregationProbe.Json) == JsonSerializer.Serialize(expected.Groups, AggregationProbe.Json), "実構造IDと原因所属");
            records.Add(new { run = record.GetProperty("run").GetString(), usage = plan.Usage, original_descriptor_bytes = document.Usage.DescriptorBytes,
                nonflow_descriptor_bytes = plan.Nonflow.DescriptorBytes, ambiguity_descriptor_bytes = plan.Ambiguity.DescriptorBytes,
                shared_descriptor_bytes = result.SharedDescriptorBytes, aggregation_descriptor_bytes = result.TerminalDescriptorBytes,
                reserved_total_bytes = plan.Usage.DescriptorBytes + result.TerminalDescriptorBytes + result.SharedDescriptorBytes,
                descriptor_limit_bytes = PageFlowLimits.MaximumDescriptorBytes, raw, result,
                original_comparisons = plan.Verifications.Sum(v => v.OriginalComparisons),
                nonflow_ids = plan.Nonflow.Proofs.Select(p => p.CandidateIndex + 1).ToArray() });
            Console.WriteLine(record.GetProperty("run").GetString() + ": 製品の実構造と予約を照合");
            Mat Read(PageFlowPageKey key) { using var image = (key.Side == PageSpace.A ? a : b).ReadPage(key.Page, 300); return image.TakePixels(); }
            RowTextResult Text(PageFlowPageKey key, Mat image) => (key.Side == PageSpace.A ? ta : tb).ReadRowWords(key.Page, image.Size(), 300);
            static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException("末尾ページの製品監査: " + label); }
        }
        if (records.Count != (shared ? 6 : 8)) throw new InvalidOperationException("追加方向の数が不足しています。");
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(file, records, AggregationProbe.Json);
    }
}
