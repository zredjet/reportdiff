using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Pdf;
using static CandidateInference;

internal static class CandidateProbe
{
    internal static void Run(string inputs, string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(inputs, "observations.json")));
        var runs = new List<object>();
        foreach (var item in manifest.RootElement.EnumerateArray())
        {
            // 正解links・行ID・Rows・座標は読まない。ファイルの固定と列挙だけを行う。
            var id = item.GetProperty("test").GetProperty("id").GetString()!;
            var pathA = Path.Combine(inputs, id, "a.pdf"); var pathB = Path.Combine(inputs, id, "b.pdf");
            if (Hash(File.ReadAllBytes(pathA)) != item.GetProperty("a_sha256").GetString()
                || Hash(File.ReadAllBytes(pathB)) != item.GetProperty("b_sha256").GetString()) throw new InvalidOperationException("固定PDFが変わっています。");
            foreach (var reverse in new[] { false, true })
            {
                var name = id + (reverse ? "-ba" : "-ab");
                var folder = Path.Combine(output, name); Directory.CreateDirectory(folder);
                var result = Document(reverse ? pathB : pathA, reverse ? pathA : pathB, folder);
                runs.Add(new { id, reverse, folder = name, result });
                Console.WriteLine($"{name}: 推定と元座標の検証を保存");
                if (id is "chain3" or "skia-chain3")
                foreach (var selected in new[] { new[] { 1, 3 }, new[] { 1 } })
                {
                    var selectedName = name + "-pages-" + string.Join('-', selected);
                    var selectedFolder = Path.Combine(output, selectedName); Directory.CreateDirectory(selectedFolder);
                    var selectedResult = Document(reverse ? pathB : pathA, reverse ? pathA : pathB, selectedFolder, selected);
                    runs.Add(new { id, reverse, folder = selectedName, selected, result = selectedResult });
                    Console.WriteLine($"{selectedName}: 選択ページだけで検証を保存");
                }
            }
        }
        File.WriteAllText(Path.Combine(output, "candidates.json"), JsonSerializer.Serialize(runs,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }

    private static object Document(string pathA, string pathB, string folder, int[]? selected = null)
    {
        using var pdfA = PdfReader.Open(pathA); using var pdfB = PdfReader.Open(pathB);
        if (pdfA.PageCount > 16 || pdfB.PageCount > 16)
            throw new ArgumentException("独立ツールの上限は各16ページです。描画前に終了します。");
        using var textA = new PdfTextReader(pathA); using var textB = new PdfTextReader(pathB);
        var pagesA = Read(pdfA, textA, "a"); var pagesB = Read(pdfB, textB, "b");
        var inference = Find(pagesA, pagesB);
        var links = new List<Proposal>(); var evidence = new List<object>();
        foreach (var proposal in inference.Proposals)
        {
            if (proposal.Status != "candidate") { links.Add(proposal); continue; }
            using var source = (proposal.Source.Side == "a" ? pdfA : pdfB).ReadPage(proposal.Source.Page);
            using var target = (proposal.Target.Side == "a" ? pdfA : pdfB).ReadPage(proposal.Target.Page);
            using var from = new Mat(source.Pixels, new Rect(0, proposal.Source.Start, source.Pixels.Width, proposal.Source.Length));
            using var to = new Mat(target.Pixels, new Rect(0, proposal.Target.Start, target.Pixels.Width, proposal.Target.Length));
            var equal = Cv2.Norm(from, to, NormTypes.INF) == 0;
            var checkedLink = proposal with { Status = equal ? "band_verified" : "skipped", Reason = equal ? null : "nonidentical_band_not_proven" };
            links.Add(checkedLink);
            var comparisons = new List<object>();
            if (equal)
            {
                foreach (var (side, original, rect, donor) in new[]
                {
                    ("source", source.Pixels, new Rect(0, proposal.Source.Start, source.Pixels.Width, proposal.Source.Length), to),
                    ("target", target.Pixels, new Rect(0, proposal.Target.Start, target.Pixels.Width, proposal.Target.Length), from)
                })
                {
                    using var replaced = original.Clone(); using (var dest = new Mat(replaced, rect)) donor.CopyTo(dest);
                    if (Cv2.Norm(original, replaced, NormTypes.INF) != 0) throw new InvalidOperationException("完全一致帯の置換が元画像と異なります。");
                    foreach (var (profile, diff) in Profiles())
                    foreach (var swapped in new[] { false, true })
                    {
                        using var compared = PageComparer.Compare(swapped ? replaced : original, swapped ? original : replaced, new() { Diff = diff });
                        if (compared.RawPixels != 0) throw new InvalidOperationException("完全一致の帯で差分が発生しました。");
                        comparisons.Add(new { side, profile, reverse = swapped, compared.RawPixels });
                    }
                }
            }
            evidence.Add(new { source = proposal.Source, target = proposal.Target, exact_band_pixels = equal, comparisons });
        }
        // 一つの帯へ複数候補が重なる場合は、先着の候補を採用しない。
        var verified = links.Where(l => l.Status == "band_verified").ToArray();
        if (verified.SelectMany(l => new[] { l.Source, l.Target }).GroupBy(b => (b.Side, b.Page)).Any(g =>
            g.SelectMany((x, i) => g.Skip(i + 1).Select(y => x.Start < y.Start + y.Length && y.Start < x.Start + x.Length)).Any(v => v)))
        {
            links = links.Select(l => l.Status == "band_verified" ? l with { Status = "skipped", Reason = "overlapping_candidates" } : l).ToList();
            verified = [];
        }
        var pages = new List<object>();
        var gatePages = new List<DocumentGate.Page>();
        var availableMasks = new Dictionary<int, (string Baseline, string? Candidate)>();
        var mappedBands = new HashSet<Band>(); var unpairedBands = new HashSet<Band>(); var mapFailures = 0;
        foreach (var number in pagesA.Select(p => p.Number).Union(pagesB.Select(p => p.Number)).Order())
        {
            var pa = pagesA.SingleOrDefault(p => p.Number == number); var pb = pagesB.SingleOrDefault(p => p.Number == number);
            if (pa is null || pb is null)
            {
                var side = pa is null ? "b" : "a";
                var layout = (side == "a" ? inference.Layouts.A : inference.Layouts.B).SingleOrDefault(l => l.Page.Number == number);
                var bands = verified.SelectMany(l => new[] { l.Source, l.Target }).Where(b => b.Side == side && b.Page == number).ToArray();
                unpairedBands.UnionWith(bands);
                gatePages.Add(new(number, false, false, bands));
                using var loaded = (side == "a" ? pdfA : pdfB).ReadPage(number);
                var residual = layout is null ? (long?)null : Residual(loaded.Pixels, layout, bands);
                pages.Add(new { page = number, status = "only_in_" + side, difference_count_complete = false,
                    verified_bands = bands, fixed_parts_verified = layout is not null, residual_nonwhite_pixels = residual,
                    only_verified_bands_and_fixed_parts = layout is not null && bands.Length > 0 && residual == 0 });
                continue;
            }
            using var a = pdfA.ReadPage(number); using var b = pdfB.ReadPage(number);
            using var baseline = RowComparer.Compare(a.Pixels, b.Pixels, new(), new() { Enabled = true }, () => (pa.Extraction, pb.Extraction));
            var baselineMask = $"p{number}-baseline-raw.png";
            Save(baselineMask, baseline.Comparison.RawMask);
            Save($"p{number}-baseline-a.png", baseline.ContentA ?? a.Pixels);
            Save($"p{number}-baseline-b.png", baseline.ContentB ?? b.Pixels);
            var la = inference.Layouts.A.SingleOrDefault(l => l.Page.Number == number);
            var lb = inference.Layouts.B.SingleOrDefault(l => l.Page.Number == number);
            var built = la is null || lb is null ? null : CandidateSurface.Create(la, lb, a.Pixels, b.Pixels, verified);
            var comparisons = new List<object>();
            if (built?.Status == "built")
            {
                mappedBands.UnionWith(built.Removed.Where(r => r.Proof == "verified_carry_range").Select(r => r.Band));
                var surface = built.Surface!;
                using var ca = surface.ContentMap.Render(a.Pixels, PageSpace.A);
                using var cb = surface.ContentMap.Render(b.Pixels, PageSpace.B);
                Save($"p{number}-c-a.png", ca); Save($"p{number}-c-b.png", cb);
                foreach (var (profile, diff) in Profiles())
                {
                    using var result = PageComparer.Compare(ca, cb, new() { Diff = diff });
                    var mask = $"p{number}-{profile}-raw.png"; Save(mask, result.RawMask);
                    comparisons.Add(new { profile, result.RawPixels, result.Status, clusters = result.Clusters.Count, mask });
                }
            }
            else { mapFailures++; Save($"p{number}-fallback-raw.png", baseline.Comparison.RawMask); }
            gatePages.Add(new(number, true, built?.Status == "built", built?.Removed
                .Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray() ?? []));
            availableMasks[number] = (baselineMask, built?.Status == "built" ? $"p{number}-normal-raw.png" : null);
            pages.Add(new { page = number, status = built?.Status ?? "skipped", reason = built?.Reason ?? inference.Layouts.Reason,
                baseline = new { baseline.Status, baseline.DifferenceCount, raw_pixels = baseline.Comparison.RawPixels,
                    clusters = baseline.Comparison.Clusters.Count, baseline.Alignment },
                fallback_preserved = built?.Status != "built", segments = built?.Map?.Segments, kinds = built?.Kinds,
                removed = built?.Removed, content_segments = built?.Surface?.ContentMap.Segments, comparisons });
        }
        var gate = DocumentGate.Evaluate(inference.Layouts, links, gatePages);
        var selectedMasks = new List<object>();
        foreach (var selection in gate.Selections.Where(s => s.Choice != "unpaired"))
        {
            var masks = availableMasks[selection.Page];
            var source = selection.Choice == "candidate" ? masks.Candidate! : masks.Baseline;
            var destination = $"p{selection.Page}-selected-raw.png";
            File.Copy(Path.Combine(folder, source), Path.Combine(folder, destination));
            selectedMasks.Add(new { selection.Page, selection.Choice, source, mask = destination });
        }
        return new { status = inference.Layouts.Status, reason = inference.Layouts.Reason, automatic_product_adoption = false,
            gate, selected_masks = selectedMasks,
            all_paired_maps_built = mapFailures == 0,
            range_correspondence = verified.Select(l => new { l.Source, l.Target,
                source_proof = Proof(l.Source), target_proof = Proof(l.Target),
                both_endpoints_accounted_for = Proof(l.Source) is not null && Proof(l.Target) is not null }),
            pages_read_a = pagesA.Select(p => p.Number), pages_read_b = pagesB.Select(p => p.Number),
            layouts_a = DescribeLayouts(inference.Layouts.A), layouts_b = DescribeLayouts(inference.Layouts.B), links, evidence, pages };

        string? Proof(Band band) => mappedBands.Contains(band) ? "paired_page_structural_range"
            : unpairedBands.Contains(band) ? "unpaired_page_verified_range" : null;

        List<Page> Read(PdfReader pdf, PdfTextReader text, string side)
        {
            var result = new List<Page>();
            foreach (var number in selected ?? Enumerable.Range(1, pdf.PageCount).ToArray())
            {
                if (number > pdf.PageCount) continue;
                using var page = pdf.ReadPage(number);
                Save($"{side}-p{number}.png", page.Pixels);
                result.Add(Describe(number, page.Pixels, text.ReadRowWords(number, page.Pixels.Size(), 300)));
            }
            return result;
        }
        void Save(string name, Mat image) => File.WriteAllBytes(Path.Combine(folder, name), image.ImEncode(".png"));
    }

    private static object DescribeLayouts(IReadOnlyList<Layout> layouts) => layouts.Select(l => new
    {
        page = l.Page.Number, l.HeaderEnd, l.FooterStart, l.BodyStart, l.BodyEnd, l.Pitch, l.Regular,
        body = l.Body.Select(row => new { text = Text(row), row.Bounds, row.Baseline })
    }).ToArray();

    private static long Residual(Mat page, Layout layout, IReadOnlyList<Band> bands)
    {
        var width = page.Width;
        long pixels = 0; var row = new byte[width * 3];
        for (var y = layout.HeaderEnd; y < layout.FooterStart; y++)
        {
            if (bands.Any(b => y >= b.Start && y < b.Start + b.Length)) continue;
            Marshal.Copy(page.Ptr(y), row, 0, row.Length);
            for (var x = 0; x < width; x++) if (row[x * 3] != 255 || row[x * 3 + 1] != 255 || row[x * 3 + 2] != 255) pixels++;
        }
        return pixels;
    }
    private static (string Name, DiffOptions Diff)[] Profiles() => [("normal", new()), ("strict", new() { MaxShiftMm = 0, EdgeTolerance = 0 }), ("loose", new() { MaxShiftMm = .30 })];
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
