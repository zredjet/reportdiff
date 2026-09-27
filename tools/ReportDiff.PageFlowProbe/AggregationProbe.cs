using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using static CausalAggregation;

internal static class AggregationProbe
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static void Run(string candidates, string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(candidates, "candidates.json")));
        var records = new List<object>();
        foreach (var run in document.RootElement.EnumerateArray())
        {
            var name = run.GetProperty("folder").GetString()!;
            var result = run.GetProperty("result"); var directory = Path.Combine(candidates, name);
            var destination = Path.Combine(output, name); Directory.CreateDirectory(destination);
            var gate = result.GetProperty("gate").Deserialize<DocumentGate.Decision>(Json)!;
            var rows = new List<Row>(); var pages = new List<Page>(); var projections = new List<object>();
            foreach (var side in new[] { "a", "b" })
            foreach (var layout in result.GetProperty("layouts_" + side).EnumerateArray())
            {
                var page = layout.GetProperty("page").GetInt32(); var pitch = layout.GetProperty("pitch").GetInt32();
                var top = layout.GetProperty("body_start").GetInt32(); var index = 0;
                foreach (var line in layout.GetProperty("body").EnumerateArray())
                    rows.Add(new(side, page, top + index++ * pitch, pitch, line.GetProperty("text").GetString()!));
            }
            foreach (var page in result.GetProperty("pages").EnumerateArray())
            {
                var number = page.GetProperty("page").GetInt32(); var state = page.GetProperty("status").GetString()!;
                if (state.StartsWith("only_in_", StringComparison.Ordinal))
                {
                    pages.Add(new(number, false, 0, 0, false, page.GetProperty("only_verified_bands_and_fixed_parts").GetBoolean(), []));
                    continue;
                }
                if (!gate.Ready)
                {
                    var baseline = page.GetProperty("baseline");
                    var count = baseline.GetProperty("clusters").GetInt32();
                    // 旧保存形式にはwarningsがない。既定上限未満だけを網羅性の根拠とする。
                    var complete = baseline.GetProperty("status").GetString() != "too_different"
                        && count < new ClusterOptions().MaxClustersPerPage;
                    pages.Add(new(number, true, count, baseline.GetProperty("difference_count").GetInt32(), complete, false, []));
                    continue;
                }
                using var a = Load($"a-p{number}.png"); using var b = Load($"b-p{number}.png");
                var segments = page.GetProperty("segments").Deserialize<PageSegment[]>(Json)!;
                var kinds = page.GetProperty("kinds").Deserialize<RowBandKind[]>(Json)!;
                var map = new PageMap(a.Size(), b.Size(), new(a.Width, segments.Sum(s => s.Length)), segments);
                var surface = RowComparisonSurface.Create(a, b, map, kinds, new()).Surface
                    ?? throw new InvalidOperationException("保存写像を再構成できません。");
                using var ca = surface.ContentMap.Render(a, PageSpace.A); using var cb = surface.ContentMap.Render(b, PageSpace.B);
                using var content = CompareRetainingProjection(ca, cb);
                using var savedRaw = Load($"p{number}-normal-raw.png", ImreadModes.Grayscale);
                if (Cv2.Norm(content.RawMask, savedRaw, NormTypes.INF) != 0) throw new InvalidOperationException("保存生差分が変わっています。");
                // 製品の内部APIを公開せず、独立ツールだけで実際の構造IDとクラスタ投影を使用する。
                using var display = (RowDisplayProjection)Method("ReportDiff.Core.RowProjection", "Create", 7)
                    .Invoke(null, [content, surface, a, b, new ComparisonParameters(), null, null])!;
                if (display.Comparison.RawPixels != content.RawPixels || display.Comparison.Clusters.Count != content.Clusters.Count)
                    throw new InvalidOperationException("表示投影で内容差分が変わっています。");
                var structures = display.StructuralChanges.Select(s => new Structure(new(number, s.Id), s.Kind,
                    BandOf(s.SourceA, "a"), BandOf(s.SourceB, "b"), s.DisplacementPx?.Dy, s.Excluded)).ToArray();
                pages.Add(new(number, true, content.Clusters.Count, display.DifferenceCount, display.DifferenceCountComplete, false, structures));
                var rawFile = $"p{number}-display-raw.png";
                File.WriteAllBytes(Path.Combine(destination, rawFile), display.Comparison.RawMask.ImEncode(".png"));
                projections.Add(new { page = number, display.Comparison.Status, raw_pixels = display.Comparison.RawPixels,
                    structures = display.StructuralChanges, clusters = display.Comparison.Clusters,
                    content_clusters = content.Clusters, raw_mask = rawFile, raw_sha256 = Hash(File.ReadAllBytes(Path.Combine(destination, rawFile))) });

                CandidateInference.Band? BandOf(RowSourceBounds? source, string side)
                {
                    if (source is null) return null;
                    var bounds = source.Bounds;
                    if (bounds.Top != (int)bounds.Top || bounds.Bottom != (int)bounds.Bottom || bounds.Left != 0 || bounds.Right != a.Width
                        || source.Parts.Sum(p => p.Bottom - p.Top) != bounds.Bottom - bounds.Top)
                        throw new InvalidOperationException("構造帯が全幅の連続した整数区間ではありません。");
                    return new(side, number, (int)bounds.Top, (int)(bounds.Bottom - bounds.Top));
                }
            }
            var input = new Input(gate.Ready, run.TryGetProperty("selected", out _), rows, gate.SelectedLinks, pages);
            var decision = Evaluate(input);
            records.Add(new { run = name, input, decision, disabled = Evaluate(input, false), projections });
            Console.WriteLine($"{name}: {decision.Status} / {decision.Reason ?? "単一原因"} / {decision.DifferenceCount}→{decision.AggregatedDifferenceCount}");
            Mat Load(string file, ImreadModes mode = ImreadModes.Color) => Cv2.ImDecode(File.ReadAllBytes(Path.Combine(directory, file)), mode);
        }
        File.WriteAllText(Path.Combine(output, "aggregation.json"), JsonSerializer.Serialize(new
        { source = candidates, source_sha256 = Hash(File.ReadAllBytes(Path.Combine(candidates, "candidates.json"))), automatic_product_adoption = false, records }, Json));
    }

    private static PageComparison CompareRetainingProjection(Mat a, Mat b) =>
        (PageComparison)Method("ReportDiff.Core.PageComparer", "Compare", 8).Invoke(null, [a, b, new ComparisonParameters(), true, null, true, true, null])!;
    private static MethodInfo Method(string type, string method, int count) => typeof(PageComparer).Assembly.GetType(type)!
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == method && m.GetParameters().Length == count);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
