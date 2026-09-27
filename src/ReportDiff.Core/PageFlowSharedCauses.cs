using static ReportDiff.Core.PageFlowAggregation;
using Band = ReportDiff.Core.PageFlowBand;

namespace ReportDiff.Core;

/// <summary>同符号の複数原因を、実構造を複製せず成分全体の累積量で説明する。</summary>
internal static class PageFlowSharedCauses
{
    private sealed class Unproven : Exception;
    private sealed class BudgetExceeded : Exception;
    private sealed record Cause(SharedCause Proof, int End);

    internal static Decision Evaluate(Input input, Decision legacy, long available, PageFlowTerminalScope? terminal = null)
    {
        if (!input.GateReady || input.SelectionLimited || (terminal is null && input.Pages.Any(p => !p.Paired))) return legacy;
        long charged = 0, terminalReserved = 0;
        try
        {
            Require(input.Pages.Count <= PageFlowLimits.MaximumSelectedPages && input.Rows.Count <= PageFlowLimits.MaximumLines
                && input.Links.Count <= PageFlowLimits.MaximumCandidates
                && input.Rows.Sum(r => (long)r.Text.Length) <= PageFlowLimits.MaximumTextCharacters
                && input.Rows.Sum(r => (long)r.Length) <= int.MaxValue);
            if (terminal is not null)
            {
                terminalReserved = PageFlowTerminalScope.AggregationWorkspace(input);
                Charge(terminalReserved);
                Require(terminal.Matches(input));
            }
            var structureCount = input.Pages.Sum(p => (long)p.Structures.Count);
            Require(structureCount <= PageFlowLimits.MaximumLines * 2L);
            // 行索引・構造索引の一時領域も含め、確保前に保守的な固定費を予約する。
            Charge(512 + 128L * input.Rows.Count + 128L * structureCount + 64L * input.Pages.Count + 64L * input.Links.Count);
            var pages = input.Pages.OrderBy(p => p.Number).ToArray();
            Require(pages.Select(p => p.Number).SequenceEqual(Enumerable.Range(1, pages.Length)));
            if (terminal is not null)
                Require(pages.Length >= 3 && pages.SkipLast(1).All(p => p.Paired) && !pages[^1].Paired
                    && pages[^1].UnpairedCovered && pages[^1].Structures.Count == 0 && pages[^1].Clusters == 0 && pages[^1].DifferenceCount == 0);
            Require(input.Rows.All(r => r.Side is PageSpace.A or PageSpace.B && r.Page >= 1 && r.Page <= pages.Length
                && r.Start >= 0 && r.Length > 0 && (long)r.Start + r.Length <= int.MaxValue));
            var a = Ordered(PageSpace.A); var b = Ordered(PageSpace.B);
            Require(a.Length > 0 && b.Length > 0 && a.Select(r => r.Identity).Distinct().Count() == a.Length
                && b.Select(r => r.Identity).Distinct().Count() == b.Length);
            Require(input.Rows.GroupBy(r => (r.Side, r.Page)).All(g => g.OrderBy(r => r.Start)
                .Zip(g.OrderBy(r => r.Start).Skip(1), (x, y) => (long)x.Start + x.Length <= y.Start).All(v => v)));
            var byA = a.ToDictionary(r => r.Identity); var byB = b.ToDictionary(r => r.Identity);
            Require(a.Where(r => byB.ContainsKey(r.Identity)).Select(r => r.Identity)
                .SequenceEqual(b.Where(r => byA.ContainsKey(r.Identity)).Select(r => r.Identity)));
            var structures = pages.SelectMany(p => p.Structures).ToArray();
            Require(structures.Select(s => s.Reference).Distinct().Count() == structures.Length);
            foreach (var page in pages)
            {
                Require(page.Clusters >= 0 && page.DifferenceCount == (long)page.Clusters + page.Structures.Count);
                foreach (var s in page.Structures)
                {
                    Require(!s.Excluded && s.Reference.Page == page.Number && s.Reference.StructuralChangeId > 0);
                    Require(s.Kind is "inserted" or "deleted" or "block_moved"
                        && (s.A is null) == (s.Kind == "inserted") && (s.B is null) == (s.Kind == "deleted"));
                    Require((s.A is null || s.A.Page == new PageFlowPageKey(PageSpace.A, page.Number))
                        && (s.B is null || s.B.Page == new PageFlowPageKey(PageSpace.B, page.Number)));
                }
            }
            Require(input.Links.Count > 0 && input.Links.All(l => l.Status == "band_verified" && l.Reason is null
                && l.Source is not null && l.Target is not null && l.Source.Page.Side != l.Target.Page.Side
                && l.Target.Page.Page == l.Source.Page.Page + 1 && l.Target.Page.Page <= pages.Length
                && l.Source.Height == l.Target.Height));
            Require(input.Links.Select(l => l.Source!.Page.Page).Distinct().Count() == input.Links.Count);
            var boundaries = input.Links.Select(l => l.Source!.Page.Page).ToHashSet();
            if (terminal is not null) Require(boundaries.SetEquals(Enumerable.Range(1, pages.Length - 1)));
            var component = new int[pages.Length + 1];
            for (var n = 2; n <= pages.Length; n++) component[n] = component[n - 1] + (boundaries.Contains(n - 1) ? 0 : 1);
            Require(a.Where(r => byB.ContainsKey(r.Identity)).All(r => component[r.Page] == component[byB[r.Identity].Page]));
            var groups = new List<Group>(); var shared = new List<SharedComponent>();
            foreach (var block in pages.GroupBy(p => component[p.Number]))
            {
                var numbers = block.Select(p => p.Number).ToArray(); var set = numbers.ToHashSet();
                var aa = a.Where(r => set.Contains(r.Page)).ToArray(); var bb = b.Where(r => set.Contains(r.Page)).ToArray();
                var ss = block.SelectMany(p => p.Structures).OrderBy(s => s.Reference.Page).ThenBy(s => s.Reference.StructuralChangeId).ToArray();
                var ll = input.Links.Where(l => set.Contains(l.Source!.Page.Page)).OrderBy(l => l.Source!.Page.Page).ToArray();
                if (ll.Length == 0)
                {
                    Require(ss.Length == 0 && aa.Select(r => (r.Page, r.Start, r.Length, r.Identity))
                        .SequenceEqual(bb.Select(r => (r.Page, r.Start, r.Length, r.Identity))));
                    continue;
                }
                Charge(256 + 8L * numbers.Length);
                var lengths = aa.Concat(bb).Select(r => r.Length).Distinct().ToArray(); Require(lengths.Length == 1);
                var pitch = lengths[0]; int? origin = null;
                foreach (var n in numbers)
                {
                    var ra = aa.Where(r => r.Page == n).ToArray(); var rb = bb.Where(r => r.Page == n).ToArray();
                    if (terminal is not null && n == pages.Length)
                    {
                        Require((ra.Length == 0) != (rb.Length == 0));
                        Require(origin == (ra.Length > 0 ? ra[0].Start : rb[0].Start));
                    }
                    else
                    {
                        Require(ra.Length > 0 && rb.Length > 0 && ra[0].Start == rb[0].Start);
                        origin ??= ra[0].Start; Require(origin == ra[0].Start);
                    }
                    Require(ra.Zip(ra.Skip(1), (x, y) => x.Start + pitch == y.Start).All(v => v)
                        && rb.Zip(rb.Skip(1), (x, y) => x.Start + pitch == y.Start).All(v => v));
                    Require(n == numbers[^1] || ra.Length == rb.Length);
                }
                var extraA = aa.Count(r => !byB.ContainsKey(r.Identity)); var extraB = bb.Count(r => !byA.ContainsKey(r.Identity));
                Require((extraA > 0) != (extraB > 0));
                var sign = extraB > 0 ? 1 : -1; var expanded = sign > 0 ? bb : aa; var original = sign > 0 ? aa : bb;
                if (terminal is not null) Require(!original.Any(r => r.Page == pages.Length) && expanded.Any(r => r.Page == pages.Length));
                var originalLookup = sign > 0 ? byA : byB; var expandedLookup = sign > 0 ? byB : byA;
                Require(ll.All(l => l.Source!.Page.Side == (sign > 0 ? PageSpace.A : PageSpace.B)));
                var causes = new List<Cause>();
                for (var i = 0; i < expanded.Length; i++)
                {
                    if (originalLookup.ContainsKey(expanded[i].Identity)) continue;
                    var first = i;
                    while (i + 1 < expanded.Length && !originalLookup.ContainsKey(expanded[i + 1].Identity)) i++;
                    Require(expanded[first].Page == expanded[i].Page);
                    var causeBand = new Band(new(expanded[first].Side, expanded[first].Page), expanded[first].Start, (i - first + 1) * pitch);
                    var found = ss.Where(s => s.Kind == (sign > 0 ? "inserted" : "deleted") && (sign > 0 ? s.B : s.A) == causeBand).ToArray();
                    Require(found.Length == 1); Charge(128);
                    causes.Add(new(new(found[0].Reference, causeBand, i - first + 1, sign * causeBand.Height), i));
                }
                if (terminal is not null) Require(causes.Count > 1);
                var active = new Dictionary<PageFlowRowIdentity, int>(); var count = 0;
                for (var i = 0; i < expanded.Length; i++)
                {
                    if (count < causes.Count && causes[count].End == i) count++;
                    if (originalLookup.ContainsKey(expanded[i].Identity)) active.Add(expanded[i].Identity, count);
                }
                var assigned = causes.Select(c => c.Proof.Reference).ToHashSet();
                var crossed = new HashSet<PageFlowRowIdentity>(); var flows = new List<SharedLink>();
                List<Band>? auxiliary = terminal is null ? null : [];
                foreach (var link in ll)
                {
                    var source = link.Source!; var target = link.Target!; var ra = Within(source); var rb = Within(target);
                    Require(Tiles(source, ra) && Tiles(target, rb) && ra.Select(r => r.Text).SequenceEqual(rb.Select(r => r.Text))
                        && ra.Select(r => r.Text).SequenceEqual(link.Text));
                    var incoming = causes.Count(c => c.Proof.Band.Page.Page <= source.Page.Page);
                    Require(source.Height == causes.Take(incoming).Sum(c => c.Proof.Band.Height)
                        && source.Height == (long)ra.Length * pitch && Signature(ra) == incoming);
                    Require(source.Bottom == original.Where(r => r.Page == source.Page.Page).Max(r => r.Start + r.Length)
                        && target.Top == expanded.Where(r => r.Page == target.Page.Page).Min(r => r.Start));
                    Require(ra.All(r => crossed.Add(r.Identity)));
                    var refs = new List<Reference>(); IReadOnlyList<Band>? flowAuxiliary = null;
                    foreach (var endpoint in new[] { source, target })
                    {
                        if (terminal is not null && endpoint.Page.Page == pages.Length)
                        {
                            Require(endpoint == target && endpoint.Page.Side == (sign > 0 ? PageSpace.B : PageSpace.A)
                                && auxiliary!.Count == 0 && Tiles(endpoint, expanded.Where(r => r.Page == pages.Length).ToArray()));
                            Charge(256); // 帯と成分・リンク・出力の参照配列を確保前に予約する。
                            auxiliary!.Add(endpoint); flowAuxiliary = Array.AsReadOnly(new[] { endpoint });
                            continue;
                        }
                        var found = ss.Where(s => s.Kind == (endpoint.Page.Side == PageSpace.A ? "deleted" : "inserted")
                            && (endpoint.Page.Side == PageSpace.A ? s.A : s.B) == endpoint).ToArray();
                        Require(found.Length == 1 && assigned.Add(found[0].Reference)); refs.Add(found[0].Reference);
                    }
                    Charge(128 + 8L * (refs.Count + incoming));
                    flows.Add(new(link, refs.AsReadOnly(), References(incoming), ra.Length, flowAuxiliary));
                }
                if (terminal is not null) Require(auxiliary!.Count == 1);
                Require(crossed.SetEquals(original.Where(r => r.Page != expandedLookup[r.Identity].Page).Select(r => r.Identity)));
                var moved = new HashSet<PageFlowRowIdentity>(); var movements = new List<SharedMovement>();
                foreach (var s in ss)
                {
                    if (assigned.Contains(s.Reference)) continue;
                    Require(s.Kind == "block_moved"); var ra = Within(s.A!); var rb = Within(s.B!);
                    Require(Tiles(s.A!, ra) && Tiles(s.B!, rb) && ra.Select(r => r.Identity).SequenceEqual(rb.Select(r => r.Identity)));
                    var contributors = Signature(ra); var delta = causes.Take(contributors).Sum(c => c.Proof.Delta);
                    Require(delta != 0 && s.Dy == delta && s.B!.Top - s.A!.Top == delta && ra.All(r => moved.Add(r.Identity)));
                    assigned.Add(s.Reference); Charge(128 + 8L * contributors);
                    movements.Add(new(s.Reference, References(contributors), delta));
                }
                Require(moved.SetEquals(original.Where(r => r.Page == expandedLookup[r.Identity].Page
                    && r.Start != expandedLookup[r.Identity].Start).Select(r => r.Identity)));
                Require(assigned.SetEquals(ss.Select(s => s.Reference)));
                var balance = new List<Balance>();
                foreach (var n in numbers)
                {
                    var na = aa.Count(r => r.Page == n); var nb = bb.Count(r => r.Page == n);
                    var delta = sign * causes.Where(c => c.Proof.Band.Page.Page == n).Sum(c => c.Proof.Rows);
                    var incoming = sign * flows.Where(f => f.Link.Target!.Page.Page == n).Sum(f => f.Rows);
                    var outgoing = sign * flows.Where(f => f.Link.Source!.Page.Page == n).Sum(f => f.Rows);
                    Require(nb - na == delta + incoming - outgoing); Charge(64);
                    balance.Add(new(n, na, nb, delta, incoming, outgoing));
                }
                Charge(8L * ss.Length + 8L * causes.Count);
                var references = Array.AsReadOnly(ss.Select(s => s.Reference).ToArray());
                if (causes.Count == 1)
                    groups.Add(new(causes[0].Proof.Reference, references, [], Array.AsReadOnly(ll), balance.AsReadOnly()));
                else
                    shared.Add(new(Array.AsReadOnly(numbers), Array.AsReadOnly(causes.Select(c => c.Proof).ToArray()), references,
                        movements.AsReadOnly(), flows.AsReadOnly(), balance.AsReadOnly(), auxiliary?.AsReadOnly()));

                Row[] Within(Band band) => input.Rows.Where(r => band.Page.Side == r.Side && band.Page.Page == r.Page
                    && r.Start >= band.Top && (long)r.Start + r.Length <= band.Bottom).OrderBy(r => r.Start).ToArray();
                int Signature(Row[] rr)
                {
                    Require(rr.Length > 0 && rr.All(r => active.ContainsKey(r.Identity)));
                    var k = active[rr[0].Identity]; Require(rr.All(r => active[r.Identity] == k)); return k;
                }
                IReadOnlyList<Reference> References(int k) => Array.AsReadOnly(causes.Take(k).Select(c => c.Proof.Reference).ToArray());
            }
            Require(shared.Count > 0);
            var all = groups.SelectMany(g => g.Structures).Concat(shared.SelectMany(g => g.Structures)).ToArray();
            Require(all.Distinct().Count() == all.Length && all.ToHashSet().SetEquals(structures.Select(s => s.Reference)));
            return legacy with { Status = "grouped", Reason = null, AggregatedDifferenceCount = legacy.DifferenceCount - all.Length
                + groups.Count + shared.Sum(c => c.Causes.Count), Groups = groups.AsReadOnly(), SharedComponents = shared.AsReadOnly(), SharedDescriptorBytes = charged - terminalReserved,
                TerminalDescriptorBytes = terminalReserved, DifferenceCountComplete = terminal is null && legacy.DifferenceCountComplete,
                AggregatedDifferenceCountComplete = terminal is null && legacy.AggregatedDifferenceCountComplete == true };
        }
        catch (Unproven) { return legacy; }
        catch (BudgetExceeded) { return legacy with { Reason = terminal is null ? "shared_descriptor_limit" : "terminal_descriptor_limit",
            Groups = [], SharedComponents = null, SharedDescriptorBytes = 0, TerminalDescriptorBytes = 0 }; }

        void Charge(long bytes)
        {
            if (bytes > Math.Min(available, PageFlowLimits.MaximumDescriptorBytes) - charged) throw new BudgetExceeded();
            charged += bytes;
        }
        Row[] Ordered(PageSpace side) => input.Rows.Where(r => r.Side == side).OrderBy(r => r.Page).ThenBy(r => r.Start).ToArray();
    }

    private static void Require(bool condition) { if (!condition) throw new Unproven(); }
    private static bool Tiles(Band b, Row[] r) => r.Length > 0 && r[0].Start == b.Top && r[^1].Start + r[^1].Length == b.Bottom
        && r.Zip(r.Skip(1), (x, y) => x.Start + x.Length == y.Start).All(v => v);
}
