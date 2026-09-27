using OpenCvSharp;
using static ReportDiff.Core.PageFlowInference;

namespace ReportDiff.Core;

/// <summary>全選択ページの範囲を先に確定する。PDF・ファイル・出力は扱わず、再読込は一度に隣接2画像まで。</summary>
public sealed class PageFlowPlan
{
    public sealed record Page(int Number, PageFlowSurface.Built? Built, bool UnpairedCovered);
    private readonly PageFlowDocumentDescriptor document;
    private readonly IReadOnlyDictionary<int, ComparisonParameters> parameters;
    private RowOptions terminalOptions = new();
    private bool terminalSelectionLimited;
    private bool terminalAlignmentEnabled;
    public Inference Inference { get; }
    public IReadOnlyList<PageFlowBandVerifier.Verification> Verifications { get; }
    /// <summary>補正後Gの論理リンク。集約・写像の専用入力。</summary>
    public IReadOnlyList<Proposal> Links { get; }
    /// <summary>同じ順序・IDの元画像Oの端点。帯の証明・保存・レポートの専用入力。</summary>
    public IReadOnlyList<Proposal> OriginalLinks { get; }
    public IReadOnlyList<Page> Pages { get; }
    public PageFlowRangeDecision Decision { get; private set; }
    public PageFlowNonflow.Result Nonflow { get; private set; } = new([], 0, null);
    public PageFlowNumericRows.Result NumericRows { get; private set; } = PageFlowNumericRows.Result.Empty;
    public PageFlowAmbiguity.Result Ambiguity { get; private set; } = PageFlowAmbiguity.Result.Empty;
    public PageFlowUsage Usage => document.Usage with { DescriptorBytes = document.Usage.DescriptorBytes + Nonflow.DescriptorBytes + NumericRows.DescriptorBytes + Ambiguity.DescriptorBytes };

    private PageFlowPlan(PageFlowDocumentDescriptor document, IReadOnlyDictionary<int, ComparisonParameters> parameters, Inference inference,
        PageFlowBandVerifier.Verification[] verifications, Proposal[] links, Page[] pages, PageFlowRangeDecision decision)
    {
        this.document = document; this.parameters = parameters; Inference = inference;
        OriginalLinks = Array.AsReadOnly(verifications.Select(v => v.Proposal).ToArray());
        Verifications = Array.AsReadOnly(verifications); Links = Array.AsReadOnly(links); Pages = Array.AsReadOnly(pages); Decision = decision;
    }

    /// <param name="readOriginal">新しく所有権を渡す元Matを返す。CoreがDisposeする。未選択のページは要求しない。</param>
    public static PageFlowPlan Prepare(PageFlowDocumentDescriptor document, Func<PageFlowPageKey, Mat> readOriginal,
        ComparisonParameters parameters, RowOptions options, bool selectionLimited = false, bool alignmentEnabled = false)
        => PrepareForPages(document, readOriginal, _ => parameters, options, selectionLimited, alignmentEnabled);

    public static PageFlowPlan PrepareForPages(PageFlowDocumentDescriptor document, Func<PageFlowPageKey, Mat> readOriginal,
        Func<int, ComparisonParameters> forPage, RowOptions options, bool selectionLimited = false, bool alignmentEnabled = false)
    {
        var parameters = document.Pages.Select(p => p.Key.Page).Distinct().ToDictionary(n => n, n =>
        {
            var p = forPage(n);
            return p with { Exclude = Array.AsReadOnly(p.Exclude.ToArray()), Regions = Array.AsReadOnly(p.Regions.ToArray()) };
        });
        var dpi = parameters.Values.First().Dpi;
        if (parameters.Values.Any(p => p.Dpi != dpi)) throw new ArgumentException("送りの各ページは同じDPIを指定してください。");
        options.Validated(dpi);
        var inference = options.Enabled ? PageFlowInference.Find(document, options, dpi)
            : new Inference(new("skipped", "rows_disabled", [], []), []);
        var numeric = PageFlowNumericRows.Find(document, inference, selectionLimited, n => parameters[n], options);
        var nonflow = PageFlowNonflow.Find(document, inference, selectionLimited, numeric);
        if (numeric.FailureReason is not null || nonflow.FailureReason is not null)
        {
            var reason = numeric.FailureReason ?? nonflow.FailureReason!;
            numeric = numeric.Discard(reason); nonflow = new([], 0, reason);
        }
        var baseline = Build(inference.Proposals, PageFlowAmbiguity.Result.Empty);
        if (baseline.Decision.Ready || numeric.FailureReason is not null || nonflow.FailureReason is not null) return baseline;
        var ambiguity = PageFlowAmbiguity.Find(document, inference, options, dpi, selectionLimited,
            PageFlowLimits.MaximumDescriptorBytes - baseline.Usage.DescriptorBytes);
        if (ambiguity.FailureReason is { } failure)
            baseline.Decision = baseline.Decision with { Reasons = Array.AsReadOnly(baseline.Decision.Reasons.Append(failure).Distinct().Order(StringComparer.Ordinal).ToArray()) };
        if (ambiguity.Proofs.Count == 0) return TryTerminal(baseline);
        var resolved = Build(ambiguity.Proposals, ambiguity);
        // 元画素と全ページの写像まで成立した場合だけ再検証の証拠を採用する。
        return resolved.Decision.Ready ? resolved : TryTerminal(baseline);

        PageFlowPlan TryTerminal(PageFlowPlan previous)
        {
            var additional = PageFlowNonflow.FindTerminal(document, inference, parameters, options, selectionLimited, alignmentEnabled, numeric);
            if (additional.FailureReason is { } reason)
            {
                previous.Decision = previous.Decision with { Reasons = Array.AsReadOnly(previous.Decision.Reasons.Append(reason).Distinct().Order(StringComparer.Ordinal).ToArray()) };
                return previous;
            }
            var originalNonflow = nonflow;
            if (additional.Proofs.Count > 0)
            {
                nonflow = additional;
                var independent = Build(inference.Proposals, PageFlowAmbiguity.Result.Empty);
                if (independent.Decision.Ready) return independent;
                nonflow = originalNonflow;
            }
            // 同一連鎖の末尾拡張は、数値対応や非送り証明を混ぜずに再検証する。
            if (numeric.Proofs.Count != 0 || nonflow.Proofs.Count != 0) return previous;
            var ambiguity = PageFlowAmbiguity.FindTerminal(document, inference, parameters, options,
                selectionLimited, alignmentEnabled, PageFlowLimits.MaximumDescriptorBytes - previous.Usage.DescriptorBytes);
            if (ambiguity.FailureReason is { } failure)
                previous.Decision = previous.Decision with { Reasons = Array.AsReadOnly(previous.Decision.Reasons.Append(failure).Distinct().Order(StringComparer.Ordinal).ToArray()) };
            if (ambiguity.Proofs.Count == 0) return previous;
            var terminal = Build(ambiguity.Proposals, ambiguity);
            return terminal.Decision.Ready ? terminal : previous;
        }

        PageFlowPlan Build(IReadOnlyList<Proposal> proposals, PageFlowAmbiguity.Result ambiguity)
        {
            var descriptors = document.Pages.ToDictionary(p => p.Key);
            var verifications = new List<PageFlowBandVerifier.Verification>();
            foreach (var proposal in proposals)
            {
                var original = proposal with { Source = OriginalBand(proposal.Source), Target = OriginalBand(proposal.Target) };
                if (proposal.Status != "candidate") { verifications.Add(new(original, 0)); continue; }
                if (original.Source is null || original.Target is null)
                { verifications.Add(new(original with { Status = "skipped", Reason = "invalid_original_endpoint" }, 0)); continue; }
                using var source = readOriginal(original.Source.Page); using var target = readOriginal(original.Target.Page);
                verifications.Add(PageFlowBandVerifier.Verify(original, descriptors[original.Source.Page].Original, descriptors[original.Target.Page].Original,
                    source, target, parameters[original.Source.Page.Page], parameters[original.Target.Page.Page]));
            }
            // Oの証明結果だけをGの候補へ対応付ける。端点座標は置き換えない。
            var links = proposals.Select((p, i) => p with
                { Status = verifications[i].Proposal.Status, Reason = verifications[i].Proposal.Reason }).ToArray();
            foreach (var proof in nonflow.Proofs)
                links[proof.CandidateIndex] = links[proof.CandidateIndex] with { Reason = PageFlowNonflow.Reason };
            var verified = links.Where(l => l.Status == "band_verified").ToArray();
            var endpoints = verified.SelectMany(l => new[] { l.Source!, l.Target! }).ToArray();
            if (endpoints.GroupBy(b => b.Page).Any(g => g.OrderBy(b => b.Top)
                .Zip(g.OrderBy(b => b.Top).Skip(1), (a, b) => a.Bottom > b.Top).Any(overlap => overlap)))
            {
                links = links.Select(l => l.Status == "band_verified" ? l with { Status = "skipped", Reason = "overlapping_candidates" } : l).ToArray();
                verified = []; endpoints = [];
            }
            var layouts = inference.Layouts.A.Concat(inference.Layouts.B).ToDictionary(l => l.Page.Key);
            var pages = new List<Page>(); var proofs = new List<PageFlowPageProof>();
            foreach (var number in document.Pages.Select(p => p.Key.Page).Distinct().Order())
            {
                var ka = new PageFlowPageKey(PageSpace.A, number); var kb = new PageFlowPageKey(PageSpace.B, number);
                if (!descriptors.ContainsKey(ka) || !descriptors.ContainsKey(kb))
                {
                    var key = descriptors.ContainsKey(ka) ? ka : kb;
                    var bands = endpoints.Where(b => b.Page == key).ToArray();
                    var covered = layouts.TryGetValue(key, out var layout) && bands.Length > 0
                        && !Enumerable.Range(layout.HeaderEnd, layout.FooterStart - layout.HeaderEnd)
                            .Any(y => layout.Page.RowHasNonwhite(y) && !bands.Any(b => y >= b.Top && y < b.Bottom));
                    pages.Add(new(number, null, covered)); proofs.Add(new(number, false, Array.AsReadOnly(bands)));
                    continue;
                }
                PageFlowSurface.Built? built = null;
                if (layouts.TryGetValue(ka, out var la) && layouts.TryGetValue(kb, out var lb))
                {
                    using var a = ReadComparison(ka); using var b = ReadComparison(kb);
                    built = PageFlowSurface.Create(la, lb, a, b, verified, parameters[number], numeric);
                }
                pages.Add(new(number, built, false));
                proofs.Add(new(number, built?.Status == "built", Array.AsReadOnly(built?.Removed
                    .Where(r => r.Proof == "verified_carry_range").Select(r => r.Band).ToArray() ?? [])));
            }
            var decision = PageFlowRangeGate.Evaluate(document, inference.Layouts.Status == "prepared" && nonflow.FailureReason is null && numeric.FailureReason is null,
                numeric.FailureReason ?? nonflow.FailureReason ?? inference.Layouts.Reason,
                links.Select(l => new PageFlowCandidate(l.Source, l.Target, l.Status == "band_verified", l.Reason)).ToArray(), proofs, nonflow.Proofs, ambiguity.Proofs);
            return new(document, parameters, inference, verifications.ToArray(), links, pages.ToArray(), decision)
                { Nonflow = nonflow, NumericRows = numeric, Ambiguity = ambiguity, terminalOptions = options with { },
                    terminalSelectionLimited = selectionLimited, terminalAlignmentEnabled = alignmentEnabled };

            Mat ReadComparison(PageFlowPageKey key)
            {
                var original = readOriginal(key); var descriptor = descriptors[key]; var transferred = false;
                try
                {
                    PageFlowBandVerifier.VerifyOriginal(descriptor.Original, original);
                    if (descriptor.GlobalMap is null) { transferred = true; return original; }
                    return descriptor.GlobalMap.Render(original, key.Side);
                }
                finally { if (!transferred) original.Dispose(); }
            }
            PageFlowBand? OriginalBand(PageFlowBand? band) => band is null ? null : descriptors[band.Page].MapOriginalBand(band);
        }
    }

    public void VerifyOriginal(PageFlowPageKey key, Mat image) =>
        PageFlowBandVerifier.VerifyOriginal(document.Pages.Single(p => p.Key == key), image);

    public void VerifyComparisonImage(PageFlowPageKey key, Mat image)
    {
        var descriptor = document.Pages.Single(p => p.Key == key);
        if (descriptor.GlobalMap is null) { PageFlowBandVerifier.VerifyOriginal(descriptor, image); return; }
        if (image.Empty() || image.Type() != MatType.CV_8UC3 || image.Size() != descriptor.Size
            || PageFlowBandVerifier.Digest(image) != descriptor.PixelSha256)
            throw new InvalidOperationException("flow_comparison_changed: 補正後の比較画像が記述と異なります。");
    }

    public PageFlowComparison Compare(int page, Mat a, Mat b)
    {
        if (!Decision.Ready || !Decision.Selections.Any(s => s.Page == page && s.Choice == "candidate"))
            throw new InvalidOperationException("送り写像が文書全体で確定していません。既存の比較経路を使用してください。");
        VerifyComparisonImage(new(PageSpace.A, page), a); VerifyComparisonImage(new(PageSpace.B, page), b);
        return PageFlowComparison.Create(page, Pages.Single(p => p.Number == page).Built!.Surface!, a, b, parameters[page],
            document.Pages.Single(p => p.Key == new PageFlowPageKey(PageSpace.B, page)).GlobalMap);
    }

    public PageFlowAggregation.Decision Aggregate(IReadOnlyList<PageFlowAggregation.Page> pages, bool selectionLimited, bool enabled = true)
    {
        // 出力済みの全ページが揃った時だけ集約する。欠落を完全な文書として扱わない。
        if (!pages.Select(p => p.Number).Order().SequenceEqual(Pages.Select(p => p.Number)))
            throw new ArgumentException("範囲確定時と集約時のページ集合が異なります。");
        if (pages.Any(p => p.Paired != Decision.Selections.Any(s => s.Page == p.Number && s.Choice != "unpaired")))
            throw new ArgumentException("範囲確定時と集約時の片側ページが異なります。");
        pages = pages.Select(p => p.Paired ? p : p with { Complete = false, UnpairedCovered = Pages.Single(x => x.Number == p.Number).UnpairedCovered }).ToArray();
        var rows = Inference.Layouts.A.Concat(Inference.Layouts.B).SelectMany(l => l.Body.Select((line, i) =>
            new PageFlowAggregation.Row(l.Page.Key.Side, l.Page.Key.Page, l.BodyStart + i * l.Pitch, l.Pitch, line.Text)))
            .Select(r => NumericRows.Identify(r)).ToArray();
        var input = new PageFlowAggregation.Input(Decision.Ready, selectionLimited, rows,
            Decision.Ready ? Links.Where(l => l.Status == "band_verified").ToArray() : [], pages);
        var remaining = PageFlowLimits.MaximumDescriptorBytes - Usage.DescriptorBytes;
        var legacy = PageFlowAggregation.Evaluate(input, enabled, remaining);
        if (!enabled || !Decision.Ready || selectionLimited || legacy.Status == "grouped" || NumericRows.Proofs.Count != 0
            || !PageFlowTerminalScope.Eligible(document, Inference, parameters, terminalOptions, terminalSelectionLimited, terminalAlignmentEnabled)) return legacy;
        var reserved = PageFlowTerminalScope.AggregationWorkspace(input);
        if (reserved > remaining) return legacy with { Reason = "terminal_descriptor_limit" };
        var scope = PageFlowTerminalScope.CreateReserved(document, Inference, reserved, remaining);
        scope.VerifyBinding(document, Inference);
        var independent = PageFlowComponents.Evaluate(input, legacy, scope, remaining);
        if (independent.Status == "grouped" || Nonflow.Proofs.Count != 0) return independent;
        return PageFlowSharedCauses.Evaluate(input, independent, remaining, scope);
    }
}
