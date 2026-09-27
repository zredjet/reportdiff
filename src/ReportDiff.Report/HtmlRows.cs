using System.Text;

namespace ReportDiff.Report;

public static partial class HtmlReportWriter
{
    private static void AppendRowSettings(StringBuilder html, ReportConfiguration config)
    {
        var r = config.Rows;
        html.Append("<section class=\"row-settings\"><h3>PDF の行整列</h3><dl class=\"settings-grid\">");
        html.Append(Metric("行整列", r.Enabled ? "有効" : "無効"));
        html.Append(Metric("追加の縦ずれ上限", $"{N(r.MaxShiftMm)} mm"));
        html.Append(Metric("行の単語一致率", N(r.MinWordMatch)));
        html.Append(Metric("画像の再探索半径", $"{N(r.RefineMm)} mm"));
        html.Append(Metric("生差分の改善率下限", N(r.MinImprovement)));
        html.Append(Metric("次点との差の下限", N(r.MinScoreGap)));
        html.Append(Metric("各区間の支持帯数", r.MinSupportBands));
        html.Append(Metric("支持帯の黒換算面積", $"{N(r.MinSupportInkMm2)} mm²"));
        html.Append(Metric("内容区間数の上限", r.MaxSegments));
        html.Append("</dl></section>");
    }

    private static void AppendRows(StringBuilder html, ReportPage page)
    {
        var row = page.RowAlignment;
        if (row.Status == "disabled") return;
        html.Append($"<section class=\"row-alignment\" aria-label=\"{page.Page} ページの行整列\"><h3>行整列</h3><p class=\"notice\">{RowReason(row.Reason)}</p>");
        html.Append("<dl class=\"settings-grid\">");
        html.Append(Metric("総箇所数", page.DifferenceCount));
        html.Append(Metric("内容の差分", page.Clusters.Count));
        html.Append(Metric("行の構造変化", page.StructuralChangeCount));
        html.Append(Metric("基準の生差分", row.BaselineRawPixels?.ToString() ?? "未計算"));
        html.Append(Metric("候補の生差分", row.CandidateRawPixels?.ToString() ?? "未計算"));
        html.Append(Metric("生差分の改善率", ScoreValue(row.Improvement)));
        html.Append(Metric("支持画素の一致度", ScoreValue(row.Score)));
        html.Append(Metric("次点との差", ScoreValue(row.ScoreGap)));
        html.Append(Metric("各対応区間の支持帯数", row.SupportBands is null ? "未計算" : string.Join("、", row.SupportBands)));
        html.Append("</dl>");
        if (!page.DifferenceCountComplete) html.Append("<p class=\"notice\">この総箇所数では、上限による省略や未比較の相違を網羅できません。</p>");
        if (row.Detail is not null) html.Append($"<p class=\"muted\">見送り・判定の詳細: <code>{H(row.Detail)}</code></p>");
        if (row.Status != "applied") { html.Append("</section>"); return; }
        html.Append("<p>表示画像は、相手にない帯を白で補った画像です。紫の S 番号は行の構造変化、灰色の S 番号は全内容が除外された構造変化です。内容の差分は赤い番号で示します。移動量は全体補正後の A→B です。</p>");
        html.Append("<p class=\"muted\">差分の計算には、挿入・削除と確定した帯を外し、純白余白を残した内容比較画像を使っています。画素数・ノイズ・上限・抑制数はこの画像上の値です。表示画像の白い詰め物によって相違率を薄めません。分かれた同じ番号の断片も1件です。除外 YAML は各断片から行整列前の設定用 A 座標へ戻した候補です。</p>");
        if (row.ContentCanvas is { } content)
        {
            html.Append($"<h4>内容比較に使った画像</h4><p>{content.SizePx.W} × {content.SizePx.H} px（表示画像: {page.SizePx.W} × {page.SizePx.H} px）。</p><p>");
            foreach (var (label, path) in new[] { ("内容比較 A", page.Images.ContentA), ("内容比較 B", page.Images.ContentB) })
                if (path is not null) { ValidateImagePath(path); html.Append($"<a href=\"{path}\">{label}</a>　"); }
            html.Append("</p>");
        }
        if (row.OmissionAudit is { } audit)
            html.Append($"<p class=\"muted\">省略した構造帯: {audit.OmittedBandPixels} 画素分。内容画素 {audit.OmittedContentPixels}（うちユーザー除外 {audit.UserExcludedOmittedContentPixels}）。残した純白余白: {audit.PreservedWhitePixels} 画素分。省略帯の候補生差分は未計算です。ユーザー除外・領域抑制とは別の記録で、加算しません。</p>");
        html.Append($"<h4>行の構造変化</h4><p>{page.StructuralChangeCounts.Description}。除外された操作は下表に残し、件数に加えません。</p><div class=\"table-scroll\" role=\"region\" aria-label=\"{page.Page} ページの構造変化\" tabindex=\"0\"><table><caption>表示画像上の帯と、元PDFの補足本文。移動量は全体補正後の A→B。</caption><thead><tr><th>番号・種類</th><th>表示範囲・量</th><th>A 本文</th><th>B 本文</th></tr></thead><tbody>");
        foreach (var change in row.StructuralChanges)
        {
            var b = change.BboxPx;
            html.Append($"<tr id=\"page-{page.Page}-structure-{change.Id}\"><th scope=\"row\">S{change.Id} · {StructureKind(change.Kind)}{(change.Excluded ? "（除外）" : "")}</th><td>Y {b.Y} / 高さ {b.H} px<br>{change.BandCount} 帯");
            if (change.DisplacementPx is { } shift) html.Append($"<br>{Direction(shift.Dx, shift.Dy)}");
            if (change.EquivalentPositions.Any(p => p.FirstStart != p.LastStart)) html.Append("<br>同じ画像になる反復位置があります（件数は増えません）。");
            if (page.Images.Overlay is { } path) { ValidateImagePath(path); html.Append($"<br><a href=\"{path}\">S{change.Id} の帯を画像で確認</a>"); }
            html.Append("</td>");
            foreach (var (side, text) in new[] { ("A", change.TextA), ("B", change.TextB) })
                html.Append($"<td><div class=\"text-value\" tabindex=\"0\" role=\"region\" aria-label=\"構造変化 S{change.Id} {side} 本文\">{H(text is null ? "テキスト注釈なし" : text.Length == 0 ? "該当テキストなし" : text)}</div></td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table></div><details><summary>帯の写像・詰め物</summary><p>元範囲は全体補正後、行整列前の座標です。詰め物は対応する元画素を持ちません。</p><ul>");
        foreach (var band in row.Segments)
            html.Append($"<li>表示 Y {band.CanvasStart} / 高さ {band.Length} px · A: {band.AStart?.ToString() ?? "白い詰め物"} · B: {band.BStart?.ToString() ?? "白い詰め物"} · {(band.Kind == "structural" ? "構造帯（内容比較から省略）" : band.Kind == "white_space" ? "純白余白（内容比較に保持）" : "対応帯")}</li>");
        html.Append("</ul></details>");
        if (row.AnnotationOmissions.Count > 0)
            html.Append("<p class=\"notice\">表示画像で一つの平行移動として表せないため、次の内容差分の移動注釈を省略しました。差分と件数は残しています: "
                + string.Join("、", row.AnnotationOmissions.Select(o => H(string.Join(",", o.ClusterIds)) + "（" + H(o.Reason) + "）")) + "</p>");
        html.Append("</section>");
    }
    private static string StructureKind(string kind) => kind switch { "inserted" => "行の挿入", "deleted" => "行の削除", "block_moved" => "ブロック移動", _ => H(kind) };
    private static string RowReason(string reason) => reason switch
    {
        "applied" => "行の挿入・削除に合わせて整列しました。", "identical" => "画像が完全一致するため行整列を省略しました。",
        "not_compared" => "片側だけのページのため行整列していません。", "size_mismatch" => "元ページの寸法が異なるため行整列していません。",
        "no_text" => "PDF同士の比較で使える文字層がないため行整列していません。", "text_unavailable" => "PDF文字層や座標を安全に取得できないため行整列していません。",
        "no_bands" => "内容を保った安全な帯の対応を作れないため見送りました。", "insufficient_support" => "独立した支持が不足するため見送りました。",
        "ambiguous" => "複数の対応を区別できないため見送りました。", "low_improvement" => "生差分の改善が不足するため見送りました。",
        "column_conflict" => "列ごとに異なるずれを示すため見送りました。", "too_many_segments" => "内容区間数が上限を超えたため見送りました。",
        "resource_limit" => "安全な計算上限に達したため見送りました。", _ => H(reason)
    };
}
