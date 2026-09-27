using System.Text;

namespace ReportDiff.Report;

public static partial class HtmlReportWriter
{
    private static void AppendAnchoredAudit(StringBuilder html, ReportDocument report)
    {
        if (report.PageFlow?.AnchoredContent is not { } a) return;
        html.Append($"<section class=\"card\"><h2>元ページを保持する内容面</h2><p>{(a.Status == "applied" ? "全ページの安全条件を確認して採用しました。" : "全文書を従来比較へ戻しました。理由: " + H(a.Reason ?? "unknown"))}</p>");
        html.Append($"<p>記述予算の最大予約 {a.Budget.PeakBytes} / {a.Budget.LimitBytes} B。処理対象面 {a.Budget.SurfacePixels} / {a.Budget.LimitPixels} 画素。実メモリ使用量とは異なります。</p>");
        html.Append($"<details><summary>元候補 {a.Evidence.CandidateId} の証拠と出自 O</summary><p>帯の全画素一致: {(a.Evidence.PixelEqualityProven ? "検証済み" : "未成立")}。支持行の画素一致の証明には転用していません。</p><p>指紋: <code>{H(a.Fingerprint)}</code></p>");
        foreach (var cause in a.Evidence.Causes) html.Append($"<p>原因 · {OriginalLabel(cause.Original)} · <code>{H(cause.Text)}</code></p>");
        foreach (var (label, rows) in new[] { ("原因前の支持", a.Evidence.BeforeSupport), ("原因間の支持", a.Evidence.BetweenSupport),
            ("次ページの支持", a.Evidence.NextPageSupport), ("ページを跨ぐ行", a.Evidence.Crossing) })
        {
            html.Append($"<h3>{label}</h3><ul>");
            foreach (var row in rows) html.Append($"<li>{OriginalLabel(row.Source.Original)} → {OriginalLabel(row.Counterpart.Original)} · <code>{H(row.Source.Text)}</code> / <code>{H(row.Counterpart.Text)}</code></li>");
            html.Append("</ul>");
        }
        html.Append("</details></section>");
    }

    private static void AppendAnchoredPage(StringBuilder html, ReportPage page, ReportDocument report, int dpi, double margin)
    {
        if (page.ContentDisplay is not { } display) return;
        html.Append($"<section><h3>内容面 C{page.Page} と物理ページ {page.Page} の表示 D</h3><p>このページで計上する内容差分 {page.Clusters.Count} 件。別ページで計上する参照表示 {display.ReferenceCount} 件。表示上の生差分画素数 {display.RawPixels}（ノイズを含む和集合）です。検出画素数は内容面 C の値を使います。</p><p>");
        foreach (var (label, path) in new[] { ($"C{page.Page} · 内容比較 A", page.Images.ContentA), ($"C{page.Page} · 内容比較 B", page.Images.ContentB) })
            if (path is not null) { ValidateImagePath(path); html.Append($"<a href=\"{path}\">{label}</a>　"); }
        html.Append("</p><p>除外候補は表示断片の元Aの物理ページへ戻してから余白を付けています。Aを表示していない参照断片は所有者へ案内します。</p>");
        foreach (var c in page.Clusters)
        {
            html.Append($"<p>内容差分 <a href=\"#page-{page.Page}-cluster-{c.Id}\">P{page.Page}-C{c.Id}（{page.Page}ページで計上）</a>");
            foreach (var target in report.Pages.Where(p => p.ContentReferences?.Any(r => r.ContentRef == c.ContentRef) == true))
                html.Append($" · <a href=\"#page-{target.Page}-ref-{page.Page}-{c.Id}\">物理ページ {target.Page} の参照表示</a>");
            html.Append("</p>");
        }
        foreach (var reference in page.ContentReferences!)
        {
            var owner = $"P{reference.OwnerPage}-C{reference.OwnerClusterId}";
            html.Append($"<article id=\"page-{page.Page}-ref-{reference.OwnerPage}-{reference.OwnerClusterId}\"><h4>{owner}の参照表示 · D{page.Page}</h4><p><a href=\"#page-{reference.OwnerPage}-cluster-{reference.OwnerClusterId}\">{owner} の内容差分へ</a>。表示上の画素数 {reference.DisplayPixels}。このページの差分件数には加えません。</p>");
            html.Append("<div class=\"table-scroll\"><table><caption>物理ページ " + page.Page + " の D 切り出し</caption><tbody><tr>");
            AppendCrop(html, reference.Crops.A, $"D{page.Page} · {owner} 参照 A");
            AppendCrop(html, reference.Crops.B, $"D{page.Page} · {owner} 参照 B");
            AppendCrop(html, reference.Crops.Diff, $"D{page.Page} · {owner} 参照差分");
            html.Append("</tr></tbody></table></div>");
            foreach (var part in reference.Parts)
            {
                html.Append($"<p>C{reference.ContentRef.SurfaceId} Y {part.ContentBboxPx.Y} → D{page.Page} Y {part.DisplayBboxPx.Y}。元座標 O: {OriginalLabel(part.SourceA)} / {OriginalLabel(part.SourceB)}</p>");
                if (part.SourceA?.Page == page.Page && part.DisplaySides.Contains("a"))
                    html.Append($"<details><summary>この断片の除外 YAML · 元A {part.SourceA.Page}ページ</summary><pre>{H(ExclusionSnippet.CreateOriginal(part.SourceA, page.RowAlignment.OriginalSizeA!, dpi, margin))}</pre></details>");
                else html.Append($"<p>この参照位置には設定用Aを表示していません。<a href=\"#page-{reference.OwnerPage}-cluster-{reference.OwnerClusterId}\">所有者の断片を確認</a>してください。</p>");
            }
            html.Append("</article>");
        }
        html.Append("</section>");
    }
    private static string OriginalLabel(ReportOriginalBox? b) => b is null ? "元画像なし" :
        $"{H(b.Side.ToUpperInvariant())}{b.Page} · O (X {b.BoundsPx.X}, Y {b.BoundsPx.Y}, {b.BoundsPx.W}×{b.BoundsPx.H})";
}
