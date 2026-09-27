using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;

if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
    throw new PlatformNotSupportedException();
if (args is ["--unpaired-local-support", var localSupportOutput])
{
    UnpairedSharedDiagnosis.Run(Path.GetFullPath(localSupportOutput), inspectSingleSupport: true);
    return;
}
if (args is ["--unpaired-shared-diagnosis", var unpairedSharedOutput])
{
    UnpairedSharedDiagnosis.Run(Path.GetFullPath(unpairedSharedOutput));
    return;
}
if (args is ["--unpaired-shared-inference-replay", var inferencePrevious, var inferenceReplayOutput])
{
    UnpairedSharedDiagnosis.ReplayInference(Path.GetFullPath(inferencePrevious), Path.GetFullPath(inferenceReplayOutput));
    return;
}
if (args is ["--unpaired-shared-product-audit", var sharedTerminalFixtures, var sharedTerminalOutput])
{
    TerminalProductAudit.Run(Path.GetFullPath(sharedTerminalFixtures), Path.GetFullPath(sharedTerminalOutput), true);
    return;
}
if (args is ["--unpaired-product-audit", var terminalFixtures, var terminalOutput])
{
    TerminalProductAudit.Run(Path.GetFullPath(terminalFixtures), Path.GetFullPath(terminalOutput));
    return;
}
if (args is ["--unpaired-components", var unpairedOutput])
{
    UnpairedComponentProbe.Run(Path.GetFullPath(unpairedOutput));
    return;
}
if (args is ["--unpaired-audit", var unpairedInput, var unpairedAuditOutput])
{
    using var data = JsonDocument.Parse(File.ReadAllBytes(unpairedInput));
    var audit = data.RootElement.EnumerateArray().Select(r => new {
        run = r.GetProperty("run").GetString(),
        audit = UnpairedComponentProbe.Audit(r.GetProperty("input").Deserialize<PageFlowAggregation.Input>(AggregationProbe.Json)!,
            r.GetProperty("candidate").Deserialize<PageFlowAggregation.Decision>(AggregationProbe.Json)!)
    }).ToArray();
    using var destination = new FileStream(unpairedAuditOutput, FileMode.CreateNew, FileAccess.Write);
    JsonSerializer.Serialize(destination, audit, AggregationProbe.Json);
    return;
}
if (args is ["--anchored-acceptance-fixtures", var acceptanceRoot, var acceptanceOutput])
{
    AnchoredAcceptanceFixtures.Create(Path.GetFullPath(acceptanceRoot), Path.GetFullPath(acceptanceOutput));
    return;
}
if (args is ["--anchored-projection", var projectionRoot, var projectionInput, var projectionOutput])
{
    AnchoredProjectionProbe.Run(Path.GetFullPath(projectionRoot), Path.GetFullPath(projectionInput), Path.GetFullPath(projectionOutput));
    return;
}
if (args is ["--anchored-content", var anchoredInput, var anchoredOutput])
{
    AnchoredContentProbe.Run(Path.GetFullPath(anchoredInput), Path.GetFullPath(anchoredOutput));
    return;
}
if (args is ["--cross-page-support", var crossRoot, var crossOutput])
{
    CrossPageSupportProbe.Run(Path.GetFullPath(crossRoot), Path.GetFullPath(crossOutput));
    return;
}
if (args is ["--same-page-support", var supportOutput])
{
    SamePageSupportProbe.Run(Path.GetFullPath(supportOutput));
    return;
}
if (args is ["--shared-inference-diagnosis", var diagnosisRoot, var diagnosisOutput])
{
    SharedInferenceDiagnosis.Run(Path.GetFullPath(diagnosisRoot), Path.GetFullPath(diagnosisOutput));
    return;
}
if (args is ["--shared-combination-fixtures", var sharedCombinations])
{
    SharedCombinationFixtures.Create(Path.GetFullPath(sharedCombinations));
    return;
}
if (args is ["--shared-product-audit", var sharedInput, var sharedAuditOutput])
{
    using var data = JsonDocument.Parse(File.ReadAllBytes(sharedInput));
    var sharedAuditRecords = data.RootElement.EnumerateArray().Select(r => {
        var input = r.GetProperty("input").Deserialize<PageFlowAggregation.Input>(AggregationProbe.Json)!;
        var decision = PageFlowAggregation.Evaluate(input);
        return new { run = r.GetProperty("run").GetString(), decision,
            audit_scope = input.Pages.All(p => p.Paired) ? "paired_components" : "replay_only_unpaired",
            audit = input.Pages.All(p => p.Paired) ? MultipleCauseAudit.Run(input, x => PageFlowAggregation.Evaluate(x))
                : new MultipleCauseAudit.Result(0, 0, 0, 0) };
    }).ToArray();
    File.WriteAllText(sharedAuditOutput, JsonSerializer.Serialize(sharedAuditRecords, AggregationProbe.Json));
    return;
}
if (args is ["--shared-causes", var sharedOutput])
{
    SharedCauseProbe.Run(Path.GetFullPath(sharedOutput));
    return;
}
if (args is ["--numeric-correspondence", var numericOutput])
{
    NumericProbe.Run(Path.GetFullPath(numericOutput));
    return;
}
if (args is ["--multiple-product-audit", var productInputs, var productOutput])
{
    using var data = JsonDocument.Parse(File.ReadAllBytes(productInputs));
    var audit = data.RootElement.EnumerateArray().Select(r => new {
        run = r.GetProperty("run").GetString(),
        audit = MultipleCauseAudit.Run(r.GetProperty("input").Deserialize<PageFlowAggregation.Input>(AggregationProbe.Json)!,
            input => PageFlowAggregation.Evaluate(input))
    }).ToArray();
    File.WriteAllText(productOutput, JsonSerializer.Serialize(audit, AggregationProbe.Json));
    return;
}
if (args is ["--multiple-causes-replay", var multipleReplayInputs, var multipleReplayOutput])
{
    MultipleCauseReplay.Run(Path.GetFullPath(multipleReplayInputs), Path.GetFullPath(multipleReplayOutput));
    return;
}
if (args is ["--multiple-causes", var multipleCauseOutput])
{
    MultipleCauseProbe.Run(Path.GetFullPath(multipleCauseOutput));
    return;
}
if (args is ["--global-flow-preflight", var globalFlowInputs, var globalFlowOutput])
{
    GlobalFlowProbe.Run(Path.GetFullPath(globalFlowInputs), Path.GetFullPath(globalFlowOutput));
    return;
}
if (args is ["--raster-diagnostics", var rasterInputs, var rasterOutput])
{
    RasterDiagnostics.Run(Path.GetFullPath(rasterInputs), Path.GetFullPath(rasterOutput));
    return;
}
if (args is ["--scale-fixtures", var scaleOutput])
{
    ScaleFixtures.Create(Path.GetFullPath(scaleOutput));
    return;
}
if (args is ["--scale-control-fixtures", var scaleControlOutput])
{
    ScaleFixtures.Create(Path.GetFullPath(scaleControlOutput), true);
    return;
}
if (args is ["--scale-grid-fixtures", var scaleGridOutput])
{
    ScaleFixtures.CreateGrid(Path.GetFullPath(scaleGridOutput));
    return;
}
if (args is ["--scale-integer-fixtures", var scaleIntegerOutput])
{
    ScaleFixtures.CreateGrid(Path.GetFullPath(scaleIntegerOutput), true);
    return;
}
if (args is ["--scale-sparse-fixtures", var scaleSparseOutput])
{
    ScaleFixtures.CreateGrid(Path.GetFullPath(scaleSparseOutput), true, true);
    return;
}
if (args is ["--scale-verify-legacy", var scaleLegacyInput])
{
    ScaleFixtures.VerifyLegacy(Path.GetFullPath(scaleLegacyInput));
    return;
}
if (args is ["--core-replay", var coreInputs, var coreCandidates, var coreAggregation, var coreOutput])
{
    CoreReplay.Run(Path.GetFullPath(coreInputs), Path.GetFullPath(coreCandidates), Path.GetFullPath(coreAggregation), Path.GetFullPath(coreOutput));
    return;
}
if (args is ["--core-aggregation-audit", var coreAudit, var coreAuditOutput])
{
    CoreAggregationAudit.Run(Path.GetFullPath(coreAudit), Path.GetFullPath(coreAuditOutput));
    return;
}
if (args is ["--foundation", var foundationInputs, var foundationCandidates, var foundationOutput])
{
    FoundationProbe.Run(Path.GetFullPath(foundationInputs), Path.GetFullPath(foundationCandidates), Path.GetFullPath(foundationOutput));
    return;
}
if (args is ["--foundation-measure", var foundationScenario, var measureOutput])
{
    FoundationProbe.Measure(foundationScenario, Path.GetFullPath(measureOutput));
    return;
}
if (args is ["--aggregation-audit", var aggregationAudit])
{
    AggregationAudit.Run(Path.GetFullPath(aggregationAudit));
    return;
}
if (args is ["--aggregate", var aggregationCandidates, var aggregationOutput])
{
    AggregationProbe.Run(Path.GetFullPath(aggregationCandidates), Path.GetFullPath(aggregationOutput));
    return;
}
if (args is ["--causal-fixtures", var causalOutput])
{
    CausalFixtures.Create(Path.GetFullPath(causalOutput));
    return;
}
if (args is ["--gate-audit", var gateFolder])
{
    GateAudit.Run(Path.GetFullPath(gateFolder));
    return;
}
if (args is ["--skia-local-fixtures", var localSkiaOutput])
{
    SkiaFlowFixture.Create(Path.GetFullPath(localSkiaOutput), true);
    return;
}
if (args is ["--skia-diagnostics", var skiaCandidates, var skiaDiagnostics])
{
    SkiaBandProbe.Run(Path.GetFullPath(skiaCandidates), Path.GetFullPath(skiaDiagnostics));
    return;
}
if (args is ["--skia-fixtures", var skiaOutput])
{
    SkiaFlowFixture.Create(Path.GetFullPath(skiaOutput));
    return;
}
if (args is ["--candidate-fixtures", var fixedInputs, var newFixtures])
{
    CandidateFixtures.Create(Path.GetFullPath(fixedInputs), Path.GetFullPath(newFixtures));
    return;
}
if (args is ["--candidates", var candidateInputs, var candidateOutput])
{
    CandidateProbe.Run(Path.GetFullPath(candidateInputs), Path.GetFullPath(candidateOutput));
    return;
}
if (args is ["--context", var fixtureFolder, var outputFolder])
{
    ContextProbe.Run(Path.GetFullPath(fixtureFolder), Path.GetFullPath(outputFolder));
    return;
}
if (args.Length != 1) throw new ArgumentException("空の出力フォルダーを指定してください。");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new ArgumentException("出力先が空ではありません。");
Directory.CreateDirectory(root);
var records = new List<object>();
foreach (var test in FlowFixture.Cases)
{
    var directory = Path.Combine(root, test.Id); Directory.CreateDirectory(directory);
    var aPath = Path.Combine(directory, "a.pdf"); var bPath = Path.Combine(directory, "b.pdf");
    File.WriteAllBytes(aPath, FlowFixture.Create(test, false)); File.WriteAllBytes(bPath, FlowFixture.Create(test, true));
    using var aPdf = PdfReader.Open(aPath); using var bPdf = PdfReader.Open(bPath);
    using var aText = new PdfTextReader(aPath); using var bText = new PdfTextReader(bPath);
    var pages = new List<object>();
    for (var page = 1; page <= Math.Min(aPdf.PageCount, bPdf.PageCount); page++)
    {
        using var a = aPdf.ReadPage(page); using var b = bPdf.ReadPage(page);
        var wa = aText.ReadRowWords(page, a.Pixels.Size(), 300); var wb = bText.ReadRowWords(page, b.Pixels.Size(), 300);
        Save($"p{page}-a.png", a.Pixels); Save($"p{page}-b.png", b.Pixels);
        using var result = RowComparer.Compare(a.Pixels, b.Pixels, new(), new() { Enabled = true }, () => (wa, wb));
        pages.Add(new
        {
            page,
            result.Alignment,
            result.Status,
            result.DifferenceCount,
            raw = result.Comparison.RawPixels,
            clusters = result.Comparison.Clusters.Count,
            words_a = wa,
            words_b = wb,
            structure_count = result.Display?.StructuralChanges.Count,
            content_size = result.ContentA is null ? null : new int[] { result.ContentA.Width, result.ContentA.Height },
            oracle = SurfaceProbe.Run(test, page, a.Pixels, b.Pixels, directory)
        });
    }
    var links = new List<object>();
    foreach (var link in FlowFixture.Links(test))
    {
        using var a = aPdf.ReadPage(link.APage); using var b = bPdf.ReadPage(link.BPage);
        var ay = Units.RoundPixels((FlowFixture.Top + link.ASlot * FlowFixture.Step) * 25.4 / 72, 300);
        var by = Units.RoundPixels((FlowFixture.Top + link.BSlot * FlowFixture.Step) * 25.4 / 72, 300);
        var length = Units.RoundPixels(FlowFixture.Step * 25.4 / 72, 300);
        var wordsA = aText.ReadRowWords(link.APage, a.Pixels.Size(), 300);
        var wordsB = bText.ReadRowWords(link.BPage, b.Pixels.Size(), 300);
        var textA = BandText(wordsA, ay, length);
        var textB = BandText(wordsB, by, length);
        var profiles = new List<object>();
        foreach (var (name, diff) in new (string, DiffOptions)[] { ("normal", new()), ("strict", new() { MaxShiftMm = 0, EdgeTolerance = 0 }) })
            foreach (var margin in new[] { 0, 20, 40 })
                foreach (var reverse in new[] { false, true })
                {
                    using var aa = new Mat(a.Pixels, new Rect(0, ay - margin, a.Pixels.Width, length + 2 * margin));
                    using var bb = new Mat(b.Pixels, new Rect(0, by - margin, b.Pixels.Width, length + 2 * margin));
                    using var comparison = PageComparer.Compare(reverse ? bb : aa, reverse ? aa : bb, new() { Diff = diff });
                    using var inBand = new Mat(comparison.RawMask, new Rect(0, margin, aa.Width, length));
                    var prefix = $"carry-{link.APage}-{name}-{margin}" + (reverse ? "-reverse" : "");
                    Save(prefix + "-a.png", reverse ? bb : aa); Save(prefix + "-b.png", reverse ? aa : bb); Save(prefix + "-raw.png", comparison.RawMask);
                    profiles.Add(new
                    {
                        profile = name,
                        margin,
                        reverse,
                        prefix,
                        raw = comparison.RawPixels,
                        band_raw = Cv2.CountNonZero(inBand),
                        comparison.Status,
                        clusters = comparison.Clusters.Count,
                        identical = Cv2.Norm(aa, bb, NormTypes.INF) == 0
                    });
                    if (name == "normal" && margin == 20 && !reverse)
                    { Save($"carry-{link.APage}-source.png", aa); Save($"carry-{link.APage}-target.png", bb); Save($"carry-{link.APage}-raw.png", comparison.RawMask); }
                }
        links.Add(new
        {
            link,
            oracle_only = true,
            coordinate_system = "original_top_left",
            band_a = new { x = 0, y = ay, w = a.Pixels.Width, h = length },
            band_b = new { x = 0, y = by, w = b.Pixels.Width, h = length },
            text_a = textA,
            text_b = textB,
            text_equal = textA == textB,
            profiles
        });
    }
    records.Add(new
    {
        test,
        a_pages = aPdf.PageCount,
        b_pages = bPdf.PageCount,
        a_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(aPath))),
        b_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(bPath))),
        pages,
        links
    });
    Console.WriteLine($"{test.Id}: {aPdf.PageCount}→{bPdf.PageCount}ページ / 既知の送り {links.Count}組");
    void Save(string name, Mat image) => File.WriteAllBytes(Path.Combine(directory, name), image.ImEncode(".png"));
}
File.WriteAllText(Path.Combine(root, "observations.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));

static string BandText(RowTextResult extraction, int top, int length) =>
    string.Join(" ", extraction.Words.Where(w => w.Bounds.Top >= top && w.Bounds.Bottom <= top + length)
        .OrderBy(w => w.Bounds.Left).Select(w => w.Text));
