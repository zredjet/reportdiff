using static CandidateInference;

// 文書単位の範囲確定だけを試す。製品のcarried・原因の集約・総件数は作らない。
internal static class DocumentGate
{
    internal sealed record Page(int Number, bool Paired, bool MapBuilt, IReadOnlyList<Band> ProofBands);
    internal sealed record Selection(int Page, string Choice);
    internal sealed record Decision(bool Ready, IReadOnlyList<string> Reasons,
        IReadOnlyList<Proposal> SelectedLinks, IReadOnlyList<Selection> Selections);

    internal static Decision Evaluate(Prepared layout, IReadOnlyList<Proposal> proposals, IReadOnlyList<Page> pages)
    {
        var reasons = new List<string>();
        if (layout.Status != "prepared") reasons.Add(layout.Reason ?? "unprepared_layout");
        var verified = proposals.Where(p => p.Status == "band_verified").ToArray();
        if (verified.Length == 0) reasons.Add("no_verified_candidates");
        if (proposals.Any(p => p.Status != "band_verified")) reasons.Add("unverified_candidates");
        if (pages.Any(p => p.Paired && !p.MapBuilt)) reasons.Add("unproven_page_map");
        var proofs = pages.SelectMany(p => p.ProofBands).ToHashSet();
        if (verified.Any(p => !proofs.Contains(p.Source) || !proofs.Contains(p.Target)))
            reasons.Add("unproven_endpoint_range");
        var ready = reasons.Count == 0;
        return new(ready, reasons, ready ? verified : [], pages.Select(p =>
            new Selection(p.Number, !p.Paired ? "unpaired" : ready ? "candidate" : "baseline")).ToArray());
    }
}
