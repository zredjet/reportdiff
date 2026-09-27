using System.Text.Json;
using ReportDiff.Core;

internal static class MultipleCauseReplay
{
    internal static void Run(string folder, string output)
    {
        var records = new List<object>();
        foreach (var part in new[] { "fixed", "additional", "skia", "skia-local", "causal" })
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "aggregate-" + part, "aggregation.json")));
            foreach (var saved in document.RootElement.GetProperty("records").EnumerateArray())
            {
                var legacyInput = saved.GetProperty("input").Deserialize<CausalAggregation.Input>(AggregationProbe.Json)!;
                var input = CoreReplay.Convert(legacyInput); var legacy = PageFlowAggregation.Evaluate(input);
                var actual = IndependentCauseAggregation.Evaluate(input);
                if (actual.Status != legacy.Status || actual.DifferenceCount != legacy.DifferenceCount || actual.AggregatedDifferenceCount != legacy.AggregatedDifferenceCount
                    || actual.AggregatedDifferenceCountComplete != legacy.AggregatedDifferenceCountComplete
                    || legacy.Status == "grouped" && JsonSerializer.Serialize(actual, AggregationProbe.Json) != JsonSerializer.Serialize(legacy, AggregationProbe.Json))
                    throw new InvalidOperationException(part + "/" + saved.GetProperty("run").GetString() + ": 既存採否・件数が変化");
                records.Add(new { dataset = part, run = saved.GetProperty("run").GetString(), actual.Status, actual.DifferenceCount,
                    actual.AggregatedDifferenceCount, actual.AggregatedDifferenceCountComplete, matched = true });
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(records, AggregationProbe.Json));
        Console.WriteLine($"既存{records.Count}入力の採否・件数が一致");
    }
}
