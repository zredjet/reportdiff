using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using static AnchoredContentSurface;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal static class AnchoredProjectionProbe
{
    internal sealed record Input(string Name, string SourceRun, bool Reverse, AnchoredContentProbe.Input Content,
        Scope Settings, double MinimumImprovement, bool Expected);
    internal sealed record Scope(ComparisonParameters[] Pages, bool SelectionLimited, bool GlobalAlignment, bool NumericRows, bool CompletePages);
    internal static string? ScopeReason(Scope settings)
    {
        if (!settings.CompletePages || settings.Pages.Length != 2) return "unsupported_page_scope";
        if (settings.SelectionLimited) return "unsupported_selection";
        if (settings.GlobalAlignment) return "unsupported_alignment";
        if (settings.NumericRows) return "unsupported_numeric";
        if (settings.Pages.Any(p => p.Exclude.Count != 0)) return "unsupported_exclude";
        if (settings.Pages.Any(p => p.Regions.Count != 0)) return "unsupported_regions";
        return JsonSerializer.Serialize(settings.Pages[0], AggregationProbe.Json) != JsonSerializer.Serialize(settings.Pages[1], AggregationProbe.Json)
            ? "unsupported_page_settings" : null;
    }
    internal static void Run(string root, string input, string output)
    {
        if (Directory.Exists(output)) throw new ArgumentException("出力先が既に存在します。");
        Directory.CreateDirectory(output);
        var inputs = JsonSerializer.Deserialize<Input[]>(File.ReadAllText(input), AggregationProbe.Json)!;
        var records = new List<object>();
        foreach (var c in inputs)
        {
            var folder = Path.Combine(output, c.Name); Directory.CreateDirectory(folder);
            var originals = new Dictionary<string, Mat>(); var comparisons = new List<PageComparison>();
            try
            {
                var policyReason = ScopeReason(c.Settings);
                if (policyReason is not null) { records.Add(new { c.Name, accepted = false, reason = policyReason, published_pages = Array.Empty<int>() }); continue; }
                var hashes = c.Content.Originals.ToDictionary(kv => kv.Key, kv => Hash(File.ReadAllBytes(kv.Value)));
                foreach (var (key, path) in c.Content.Originals) originals.Add(key, Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color));
                var keys = originals.Keys.Select(Key).OrderBy(k => k.Page).ThenBy(k => k.Side).ToArray();
                var fixture = Path.Combine(root, "tests/ReportDiff.Tests/Fixtures/page-flow-same-page-support/same-page-two");
                using var textA = new PdfTextReader(Path.Combine(fixture, c.Reverse ? "b.pdf" : "a.pdf"));
                using var textB = new PdfTextReader(Path.Combine(fixture, c.Reverse ? "a.pdf" : "b.pdf"));
                var collector = new PageFlowCollector(keys);
                foreach (var key in keys)
                {
                    var im = originals[Name(key)]; var text = (key.Side == PageSpace.A ? textA : textB).ReadRowWords(key.Page, im.Size(), 300);
                    if (!collector.Add(key, im, text, .5)) throw new InvalidOperationException("記述収集に失敗しました。");
                }
                var document = collector.Complete()!; var parameters = c.Settings.Pages[0];
                var options = new RowOptions { Enabled = true, CarryEnabled = true, MinImprovement = c.MinimumImprovement };
                var product = PageFlowPlan.Prepare(document, k => originals[Name(k)].Clone(), parameters, options);
                var layouts = product.Inference.Layouts.A.Concat(product.Inference.Layouts.B).ToArray();
                var evidence = CrossPageSupportEvidence.Find(layouts, options, 300);
                var proof = evidence.Evidence ?? throw new InvalidOperationException(c.Name + ": " + evidence.Reason);
                if (!CrossPageSupportEvidence.Matches(layouts, options, proof)) throw new InvalidOperationException("証拠の再計算が一致しません。");
                var proposal = proof.Candidate;
                var verification = PageFlowBandVerifier.Verify(proposal, document.Pages.Single(p => p.Key == proposal.Source!.Page),
                    document.Pages.Single(p => p.Key == proposal.Target!.Page), originals[Name(proposal.Source!.Page)], originals[Name(proposal.Target!.Page)], parameters);
                if (verification.Proposal.Status != "band_verified")
                { records.Add(new { c.Name, accepted = false, reason = "carry_pixels", published_pages = Array.Empty<int>(), verification }); continue; }
                if (!AnchoredEvidenceBinding.Matches(c.Content, layouts, proof))
                { records.Add(new { c.Name, accepted = false, reason = "content_evidence_mismatch", published_pages = Array.Empty<int>() }); continue; }
                if (Validate(originals, c.Content.Canvases, c.Content.Omitted) is { } invalid) throw new InvalidOperationException(invalid);
                var displays = new List<AnchoredProjection.Display>(); var priorSurface = new List<object>();
                foreach (var page in new[] { 1, 2 })
                {
                    var old = CrossPageSupportSurface.Create(layouts.Single(l => l.Page.Key == new PageFlowPageKey(PageSpace.A, page)),
                        layouts.Single(l => l.Page.Key == new PageFlowPageKey(PageSpace.B, page)), originals["A"+page], originals["B"+page],
                        [verification.Proposal], parameters, [proof.Cause],
                        (map, kinds) => displays.Add(new(page, map, kinds.ToArray())));
                    priorSurface.Add(new { page, old.Status, old.Reason });
                }
                if (displays.Count != 2) throw new InvalidOperationException("全表示ページの写像がありません。");
                var pages = new List<object>(); var descriptions = new List<PageFlowAggregation.Page>(); var audits = new List<object>();
                var allAccepted = true;
                foreach (var canvas in c.Content.Canvases)
                {
                    var page = canvas.Page; var display = displays.Single(d => d.Page == page);
                    using var ca = Render(originals, canvas, true); using var cb = Render(originals, canvas, false);
                    var comparison = AnchoredProjection.Compare(ca, cb, parameters); comparisons.Add(comparison);
                    var adoption = AnchoredAdoption.Evaluate(originals["A"+page], originals["B"+page], display.Map,
                        document.Pages.Single(p => p.Key == new PageFlowPageKey(PageSpace.A, page)),
                        document.Pages.Single(p => p.Key == new PageFlowPageKey(PageSpace.B, page)), comparison, parameters, options);
                    allAccepted &= adoption.Accepted;
                    var projection = AnchoredProjection.Project(comparison, canvas, displays, ca.Width, (target, im) => Save(im, $"C{page}-D{target}-raw.png"));
                    var (structures, audit) = AnchoredProjection.Structures(display, originals, c.Content.Omitted); audits.AddRange(audit);
                    descriptions.Add(new(page, true, comparison.Clusters.Count, comparison.Clusters.Count + structures.Length,
                        comparison.Status != "too_different" && !comparison.Warnings.Contains("CLUSTER_LIMIT"), false, structures));
                    Save(ca, $"C{page}-A.png"); Save(cb, $"C{page}-B.png"); Save(comparison.RawMask, $"C{page}-raw.png");
                    using var da = display.Map.Render(originals["A"+page], PageSpace.A); using var db = display.Map.Render(originals["B"+page], PageSpace.B);
                    Save(da, $"D{page}-A.png"); Save(db, $"D{page}-B.png");
                    pages.Add(new { page, adoption, projection, comparison.RawPixels, comparison.Clusters,
                        display_segments = display.Map.Segments, display.Kinds, structures, content = canvas });
                }
                var aggregateInput = new PageFlowAggregation.Input(allAccepted, false,
                    CrossPageSupportEvidence.Rows(layouts).Select(r => new PageFlowAggregation.Row(r.Page.Side, r.Page.Page, r.Top, r.Height, r.Text)).ToArray(),
                    [verification.Proposal], descriptions);
                var aggregate = allAccepted ? PageFlowAggregation.Evaluate(aggregateInput) : null;
                var smallFragmentAudit = c.Name is "same-page-two-ab" or "same-page-two-ba"
                    ? AnchoredProjection.SmallFragmentAudit(c.Content.Canvases[0], displays, originals["A1"].Width,
                        (page, im) => Save(im, $"audit-D{page}-raw.png")) : null;
                foreach (var (key, path) in c.Content.Originals)
                    if (Hash(File.ReadAllBytes(path)) != hashes[key]) throw new InvalidOperationException("実行中に入力が変わりました。");
                var fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(new { hashes, proof, options, parameters,
                    maps = c.Content.Canvases, c.Content.Omitted, displays = displays.Select(d => new { d.Page, d.Map.Segments }) }, AggregationProbe.Json));
                records.Add(new { c.Name, accepted = allAccepted, reason = allAccepted ? "candidate_accepted" : "page_adoption_failed",
                    published_pages = allAccepted ? new[] { 1, 2 } : [], fingerprint, hashes, evidence, verification,
                    prior_surface = priorSurface, pages, structure_audit = audits, aggregate_input = aggregateInput, aggregate, small_fragment_audit = smallFragmentAudit,
                    cause_omitted_pixels = c.Content.Omitted.Sum(o => o.Length * originals[o.Key].Width), production_integrated = false });
                Console.WriteLine(c.Name + ": " + allAccepted + " / " + aggregate?.AggregatedDifferenceCount);
                void Save(Mat im, string name) => File.WriteAllBytes(Path.Combine(folder, name), im.ImEncode(".png"));
            }
            finally { foreach (var result in comparisons) result.Dispose(); foreach (var im in originals.Values) im.Dispose(); }
        }
        File.WriteAllText(Path.Combine(output, "projection.json"), JsonSerializer.Serialize(records, AggregationProbe.Json));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static PageFlowPageKey Key(string key) => new(key[0] == 'A' ? PageSpace.A : PageSpace.B, int.Parse(key[1..]));
    private static string Name(PageFlowPageKey key) => key.Side + key.Page.ToString();
}
