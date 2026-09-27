using System.Text;

namespace ReportDiff.Report;

public static partial class HtmlReportWriter
{
    private static void AppendPageFlow(StringBuilder html, ReportDocument report)
    {
        if (report.PageFlow is not { } flow) return;
        html.Append("<section class=\"card page-flow\" aria-labelledby=\"page-flow-title\"><h2 id=\"page-flow-title\">ページ送りと文書集約</h2>");
        html.Append($"<p>集約後 <strong>{flow.Aggregation.AggregatedDifferenceCount}</strong> 箇所（ページ別内訳 {flow.Aggregation.DifferenceCount} 箇所）。相違の判定は維持しています。</p>");
        html.Append(flow.Status == "applied" ? "<p>送り帯の画素一致と全ページの採用条件を確認しました。</p>" : "<p>送りの採用を見送り、ページごとの比較を維持しました。</p>");
        if (flow.Aggregation.Status != "grouped") html.Append($"<p>原因の集約は見送り: {FlowReason(flow.Aggregation.Reason)}</p>");
        if (flow.Aggregation.AggregatedDifferenceCountComplete != true)
            html.Append("<p class=\"notice\">選択範囲外・片側ページ・比較上限などにより、この件数だけでは相違を網羅できません。</p>");
        if (flow.Reasons.Count > 0)
        {
            html.Append("<ul>"); foreach (var reason in flow.Reasons) html.Append($"<li>{FlowReason(reason)}</li>"); html.Append("</ul>");
        }
        foreach (var group in flow.Aggregation.Groups)
        {
            html.Append($"<details><summary>原因グループ {group.Id} — {group.Structures.Count} 件を1件へ集約</summary><p>原因: ");
            Reference(group.Cause); html.Append("</p><p>ページ別内訳: ");
            foreach (var r in group.Structures) { Reference(r); html.Append(' '); }
            html.Append("</p></details>");
        }
        if (flow.Aggregation.SharedComponents is { Count: > 0 } shared)
        {
            html.Append("<h3>同じ送り連鎖の複数原因</h3><p>各構造を一度だけ数え、累積する挿入・削除で説明できる原因数へ集約しました。内容の差分は残しています。</p>");
            foreach (var c in shared)
            {
                html.Append($"<details><summary>送り連鎖 {c.Id} — {c.Structures.Count} 件を{c.Causes.Count}件へ集約</summary><p>原因: ");
                foreach (var cause in c.Causes) { Reference(cause.Reference); html.Append($"（{cause.Rows}行） "); }
                html.Append("</p><p>ページ別内訳: "); foreach (var r in c.Structures) { Reference(r); html.Append(' '); }
                html.Append("</p><p>各移動への寄与（全体補正後の座標での累積変位）:</p><ul>");
                foreach (var move in c.Movements)
                {
                    html.Append("<li>"); Reference(move.Structure); html.Append($": {move.Dy}px、原因 ");
                    foreach (var r in move.Causes) { Reference(r); html.Append(' '); }
                    html.Append("</li>");
                }
                html.Append("</ul><p>送り帯: ");
                foreach (var link in c.Links)
                {
                    html.Append($"<a href=\"#flow-{link.Link}\">送り候補 {link.Link}</a>（{link.Rows}行）、原因 ");
                    foreach (var r in link.Causes) { Reference(r); html.Append(' '); }
                }
                html.Append("</p>");
                if (c.AuxiliaryBands is { Count: > 0 } auxiliary)
                {
                    html.Append("<p>末尾の片側ページは補助帯として参照します。構造件数に加えず、未比較の状態を維持します。</p>");
                    foreach (var band in auxiliary) Endpoint(band, null, "片側ページの補助帯");
                }
                html.Append("</details>");
            }
        }
        if (flow.NumericMatches is { Count: > 0 } numeric)
        {
            html.Append("<h3>数値変更を含む行対応</h3><p>元のラベルと近傍の完全一致行から同じページ内の対応を推定しました。数値の差は内容比較に残し、変更行を完全一致の支持には数えていません。</p>");
            foreach (var match in numeric)
            {
                html.Append($"<details><summary>行対応 {match.Id} — {(match.Status == "applied" ? "写像に使用" : "採用見送り")}</summary>");
                NumericRow(match.A, "Aの元本文"); NumericRow(match.B, "Bの元本文");
                html.Append("<p>根拠となった完全一致行（元画像の座標）:</p>");
                foreach (var anchor in match.Anchors) { NumericRow(anchor.A, "Aの根拠行"); NumericRow(anchor.B, "Bの根拠行"); }
                html.Append("<p>行対応は文字・画像の一致の証明ではありません。</p></details>");
            }
        }
        html.Append("<h3>元画像の送り帯</h3><p class=\"muted\">以下は補正前の元帯です。帯の画像検証と原因の集約は別の確認です。確認用オーバーレイも比較判定から独立しています。</p>");
        if (flow.Links.Count == 0) html.Append("<p>記録できる送り候補はありません。</p>");
        foreach (var link in flow.Links)
        {
            html.Append($"<details id=\"flow-{link.Id}\"><summary>送り候補 {link.Id} — {(link.Status == "carried" ? "対応を確認" : "採用見送り")}</summary><p>画像検証: {(link.ImageStatus == "verified" ? "全画素一致・元ページ比較済み" : link.ImageStatus == "failed" ? "不一致・検証不成立" : "未実施")}。{FlowReason(link.Reason)}</p>");
            Endpoint(link.Source, link.Target, "送り元"); Endpoint(link.Target, link.Source, "送り先");
            if (link.Ambiguity is { } boundary)
            {
                html.Append($"<h4>曖昧な変位の再検証</h4><p>{H(boundary.SourceSide.ToUpperInvariant())}の{boundary.BoundaryPage}→{boundary.BoundaryPage + 1}ページ境界。変位は全体補正後の座標です。</p><ul>");
                foreach (var shift in boundary.Shifts)
                    html.Append($"<li>適格変位 {shift.Dy}px、完全一致の支持 {shift.SupportText.Count}行: {string.Join(" / ", shift.SupportText.Select(H))}</li>");
                html.Append("</ul>");
                if (boundary.SelectedDy is { } dy)
                    html.Append($"<p>境界を跨ぐ全行が末尾・先頭の帯を埋める変位 {dy}pxを選びました。本文対応と上記の画像検証は別の証明です。</p>");
                else html.Append("<p>全ての適格変位で、仮端点の全行が同じ物理ページ内に対応します。仮端点は送り画像の検証対象ではなく、画素一致を意味しません。内容比較は続けています。</p>");
                foreach (var row in boundary.CrossingRows) BoundaryRow(row);
                foreach (var alternative in boundary.Alternatives)
                {
                    html.Append($"<details><summary>変位 {alternative.Dy}pxの仮端点 — 同一ページ対応済み</summary>");
                    Endpoint(alternative.Source, alternative.Target, "仮の送り元"); Endpoint(alternative.Target, alternative.Source, "仮の送り先");
                    foreach (var row in alternative.SourceRows.Concat(alternative.TargetRows)) BoundaryRow(row);
                    html.Append("</details>");
                }
            }
            if (link.Nonflow is { } proof)
            {
                html.Append("<p>文書全体で本文の一意性と順序を確認し、この境界を跨ぐ共通行がないことを確認しました。候補帯の全行は同じページ内に対応しています。画素一致の証明ではなく、この帯の内容比較は続けています。</p>");
                foreach (var row in proof.SourceRows.Concat(proof.TargetRows))
                {
                    Endpoint(row.Row, row.Counterpart, "候補帯の行");
                    Endpoint(row.Counterpart, row.Row, "同じページ内の対応先");
                }
            }
            html.Append("</details>");
        }
        foreach (var page in flow.Pages.Where(p => p.Choice == "unpaired"))
            html.Append($"<p><a href=\"#page-{page.Page}\">{page.Page} ページ</a>: {(page.OnlyVerifiedBandsAndFixedParts ? "検証済みの送り帯と固定部分以外に非白内容はありません。" : "送り帯以外の内容は確認済みとしていません。")}片側ページの未比較状態は維持します。</p>");
        html.Append("</section>");
        void NumericRow(ReportFlowNumericRow row, string label)
        {
            html.Append($"<p>{label}: <code>{H(row.Text)}</code></p>");
            Endpoint(row.Original, null, label);
        }
        void BoundaryRow(ReportFlowBoundaryRow row)
        {
            html.Append($"<p>元本文: <code>{H(row.Text)}</code></p>");
            Endpoint(row.Row, row.Counterpart, "根拠行"); Endpoint(row.Counterpart, row.Row, "対応先");
        }
        void Reference(ReportFlowReference r) => html.Append($"<a href=\"#page-{r.Page}-structure-{r.StructuralChangeId}\">{r.Page}ページ S{r.StructuralChangeId}</a>");
        void Endpoint(ReportFlowEndpoint? endpoint, ReportFlowEndpoint? other, string label)
        {
            if (endpoint is null) return;
            html.Append($"<p>{label}: <a href=\"#page-{endpoint.Page}\">{endpoint.Side.ToUpperInvariant()}・{endpoint.Page}ページ</a>、元画像のY={endpoint.BoundsPx.Y}〜{endpoint.BoundsPx.Y + endpoint.BoundsPx.H}px。");
            if (other is not null) html.Append($" <a href=\"#page-{other.Page}\">対応する{other.Side.ToUpperInvariant()}・{other.Page}ページへ</a>");
            foreach (var r in endpoint.Structures) { html.Append(' '); Reference(r); }
            html.Append("</p>");
            if (endpoint.Image is { } path)
            {
                ValidateImagePath(path);
                html.Append($"<a href=\"{H(path)}\"><img loading=\"lazy\" src=\"{H(path)}\" alt=\"{label} {endpoint.Side.ToUpperInvariant()} {endpoint.Page}ページの元帯\" style=\"display:block;max-width:100%;height:auto\"></a>");
            }
        }
    }

    private static string FlowReason(string? reason) => reason switch
    {
        null => "",
        "ambiguous_same_page_rows_not_flow" => "複数の適格変位を全て調べ、同じページ内の対応と確認しました。送りとしては採用しません。",
        "global_alignment_applied" => "全体補正後の座標と元帯の合成が未検証のため、送りを見送りました。全体補正による比較は維持しています。",
        "not_pdf" => "ページ送りの対象は文字層付きPDF同士です。",
        "document_gate_not_ready" => "全ページの送りの安全条件が成立していません。",
        "adoption_not_ready" => "支持画素・移動の一意性・改善率のいずれかが採用条件を満たしません。",
        "selection_limited" => "ページを限定しているため、文書全体の原因を集約していません。",
        "same_page_rows_not_flow" => "同じページ内で全行の対応が確認できたため、ページ送りとしては扱いません。内容の差分は比較に残します。",
        "nonneutral_unlinked_page" => "送りのないページに別の構造変更があるため、文書全体の集約を見送りました。",
        "component_multiple_cause_ranges" or "component_multiple_or_missing_causes" => "同じ送り連鎖の原因を一つに確定できないため、文書全体の集約を見送りました。",
        "multiple_cause_ranges" or "multiple_or_missing_causes" => "原因を一つに確定できないため、ページ別の相違を残しています。",
        "terminal_descriptor_limit" => "末尾ページの証拠が記述予算を超えるため、文書全体の集約を見送りました。ページ別の比較結果は維持しています。",
        "shared_descriptor_limit" => "共有原因の証拠が記述予算を超えるため、文書全体の集約を見送りました。ページ別の比較結果は維持しています。",
        "unpaired_residual_not_proven" => "片側ページの帯外の内容を確認できていません。",
        _ => $"判定理由: <code>{H(reason)}</code>"
    };
}
