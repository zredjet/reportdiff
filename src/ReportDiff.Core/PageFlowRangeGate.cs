namespace ReportDiff.Core;

public sealed record PageFlowCandidate(PageFlowBand? Source, PageFlowBand? Target, bool Verified, string? Reason);
public sealed record PageFlowPageProof(int Page, bool MapBuilt, IReadOnlyList<PageFlowBand> Bands);
public sealed record PageFlowPageSelection(int Page, string Choice);
public sealed record PageFlowRangeDecision(bool Ready, IReadOnlyList<string> Reasons,
    IReadOnlyList<PageFlowCandidate> SelectedLinks, IReadOnlyList<PageFlowPageSelection> Selections);

/// <summary>画像一致とは別に、全選択ページの写像と端点を確定。因果・集約件数はここで扱わない。</summary>
public static class PageFlowRangeGate
{
    public static PageFlowRangeDecision Evaluate(PageFlowDocumentDescriptor document, bool layoutReady, string? layoutReason,
        IReadOnlyList<PageFlowCandidate> candidates, IReadOnlyList<PageFlowPageProof> proofs,
        IReadOnlyList<PageFlowNonflow.Proof>? nonflow = null, IReadOnlyList<PageFlowAmbiguity.Proof>? ambiguity = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var pages = document.Pages.ToDictionary(p => p.Key);
        var numbers = pages.Keys.Select(k => k.Page).Distinct().Order().ToArray();
        if (!layoutReady) reasons.Add(layoutReason ?? "unprepared_layout");
        // 上限超過の列挙や配列化をしない。後続の通常比較は呼び出し側が維持する。
        if (candidates.Count > PageFlowLimits.MaximumCandidates) return Limited("flow_candidate_limit");
        if (proofs.Count > numbers.Length) return Limited("invalid_page_proof_set");
        if (proofs.Sum(p => (long)p.Bands.Count) > 2L * PageFlowLimits.MaximumCandidates) return Limited("flow_proof_limit");
        if (proofs.Count != numbers.Length || proofs.Select(p => p.Page).Distinct().Count() != proofs.Count
            || proofs.Any(p => !numbers.Contains(p.Page))) reasons.Add("invalid_page_proof_set");
        var verified = candidates.Where(c => c.Verified).ToArray();
        if (verified.Length == 0) reasons.Add("no_verified_candidates");
        var samePage = new HashSet<int>();
        foreach (var proof in nonflow ?? [])
        {
            if ((uint)proof.CandidateIndex >= (uint)candidates.Count || !samePage.Add(proof.CandidateIndex))
            { reasons.Add("invalid_nonflow_proof"); continue; }
            var candidate = candidates[proof.CandidateIndex];
            if (!ReferenceEquals(proof.Document, document) || candidate.Verified || candidate.Reason != PageFlowNonflow.Reason
                || candidate.Source != proof.Source || candidate.Target != proof.Target) reasons.Add("invalid_nonflow_proof");
        }
        foreach (var proof in ambiguity ?? [])
        {
            if ((uint)proof.CandidateIndex >= (uint)candidates.Count || !ReferenceEquals(proof.Document, document))
            { reasons.Add("invalid_nonflow_proof"); continue; }
            if (proof.SelectedDy is not null) continue;
            var candidate = candidates[proof.CandidateIndex];
            if (!samePage.Add(proof.CandidateIndex) || candidate.Verified || candidate.Reason != PageFlowAmbiguity.NonflowReason
                || candidate.Source is not null || candidate.Target is not null) reasons.Add("invalid_nonflow_proof");
        }
        if (verified.Length + samePage.Count != candidates.Count) reasons.Add("unverified_candidates");
        if (numbers.Any(n => Paired(n) && !proofs.Any(p => p.Page == n && p.MapBuilt))) reasons.Add("unproven_page_map");
        var provenBands = new HashSet<PageFlowBand>();
        foreach (var proof in proofs)
        foreach (var band in proof.Bands)
        {
            if (band.Page.Page != proof.Page || !InBounds(band) || !provenBands.Add(band)) reasons.Add("invalid_endpoint_proof");
        }
        var endpoints = new List<PageFlowBand>();
        foreach (var candidate in verified)
        {
            if (candidate.Reason is not null || candidate.Source is not { } source || candidate.Target is not { } target
                || !InBounds(source) || !InBounds(target)
                || source.Page.Side == target.Page.Side || (long)source.Page.Page + 1 != target.Page.Page
                || source.Height != target.Height || pages[source.Page].Size.Width != pages[target.Page].Size.Width)
            { reasons.Add("invalid_flow_endpoint"); continue; }
            endpoints.Add(source); endpoints.Add(target);
            if (!provenBands.Contains(source) || !provenBands.Contains(target)) reasons.Add("unproven_endpoint_range");
        }
        foreach (var group in endpoints.GroupBy(e => e.Page))
        {
            var ordered = group.OrderBy(b => b.Top).ThenBy(b => b.Height).ToArray();
            if (ordered.Zip(ordered.Skip(1), (a, b) => a.Bottom > b.Top).Any(overlap => overlap)) reasons.Add("overlapping_flow_endpoints");
        }
        if (provenBands.Any(b => !endpoints.Contains(b))) reasons.Add("orphan_endpoint_proof");
        var ready = reasons.Count == 0;
        return new(ready, Array.AsReadOnly(reasons.Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(ready ? verified.OrderBy(c => c.Source!.Page.Page).ThenBy(c => c.Source!.Page.Side)
                .ThenBy(c => c.Source!.Top).ToArray() : []),
            Array.AsReadOnly(numbers.Select(n => new PageFlowPageSelection(n, !Paired(n) ? "unpaired" : ready ? "candidate" : "baseline")).ToArray()));

        bool Paired(int n) => pages.ContainsKey(new(PageSpace.A, n)) && pages.ContainsKey(new(PageSpace.B, n));
        bool InBounds(PageFlowBand band) => pages.TryGetValue(band.Page, out var page) && band.Bottom <= page.Size.Height;
        PageFlowRangeDecision Limited(string reason) => new(false, Array.AsReadOnly(new[] { reason }), Array.AsReadOnly(Array.Empty<PageFlowCandidate>()),
            Array.AsReadOnly(numbers.Select(n => new PageFlowPageSelection(n, Paired(n) ? "baseline" : "unpaired")).ToArray()));
    }
}
