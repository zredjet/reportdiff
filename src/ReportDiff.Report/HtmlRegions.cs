using System.Text;
using ReportDiff.Core;

namespace ReportDiff.Report;

public static partial class HtmlReportWriter
{
    private static void AppendRegionSettings(StringBuilder html, ReportConfiguration config)
    {
        if (config.RegionAudit is { } audit)
        {
            html.Append("<section class=\"region-audit\"><h3>領域を無効にした監査比較</h3><p class=\"notice\"><code>--no-regions</code> により、regions と exclude を比較・全体補正・PDF 注釈・判定画像から外しました。ページの比較設定と全体補正の有効／無効は維持しています。</p>");
            AppendRegionDeclarations(html, audit.Regions, "無視した領域の宣言");
            html.Append("<h4>無視した除外の宣言</h4><ul>");
            foreach (var e in audit.Exclude)
                html.Append($"<li>{PageNumber(e.Page)}: X {N(e.X)} / Y {N(e.Y)} / 幅 {N(e.W)} / 高さ {N(e.H)} mm · {H(e.Note)}</li>");
            html.Append("</ul></section>");
        }
        if (config.Regions is { Count: > 0 } regions) AppendRegionDeclarations(html, regions, "領域別の実効設定");
    }

    private static void AppendRegionDeclarations(StringBuilder html, IReadOnlyList<ReportRegion> regions, string title)
    {
        html.Append($"<h3>{H(title)}</h3>");
        if (regions.Count == 0) { html.Append("<p>宣言はありません。</p>"); return; }
        html.Append("<p class=\"muted\">ページ設定 → 領域の profile → 領域の diff の順に合成します。内側の領域もページ設定を継承します。座標は A／補正後 B、左上原点です。</p>");
        html.Append($"<div class=\"table-scroll\" role=\"region\" aria-label=\"{H(title)}\" tabindex=\"0\"><table class=\"region-settings\"><caption>領域の宣言と適用する条件</caption><thead><tr><th>番号・名前</th><th>ページ・処理</th><th>位置・大きさ（mm）</th><th>上書きの指定</th><th>実効 diff</th></tr></thead><tbody>");
        foreach (var (r, index) in regions.Select((r, i) => (r, i)))
            html.Append($"<tr><th scope=\"row\">R{index + 1} {H(r.Name)}</th><td>{PageNumber(r.Page)} · {(r.Mode == "exclude" ? "除外" : "比較")}</td><td>X {N(r.X)} / Y {N(r.Y)}<br>幅 {N(r.W)} / 高さ {N(r.H)}</td><td>profile: {H(r.Profile ?? "継承")}<br>ずれ: {Override(r.Diff?.MaxShiftMm)} / 色: {Override(r.Diff?.ColorThreshold)} / 輪郭: {Override(r.Diff?.EdgeTolerance)}</td><td>{DiffText(r.EffectiveDiff)}</td></tr>");
        html.Append("</tbody></table></div>");
    }

    private static string Override(double? value) => value is double number ? N(number) : "継承";
    private static string PageNumber(int? page) => page is int number ? N(number) + " ページ" : "すべて";
    private static string DiffText(DiffOptions? diff) => diff is null ? "—" : $"ずれ {N(diff.MaxShiftMm)} mm<br>色 {N(diff.ColorThreshold)} / 輪郭 {N(diff.EdgeTolerance)}";

    private static void AppendPageRegions(StringBuilder html, ReportPage page)
    {
        if (page.Regions is not { } regions) return;
        html.Append($"<section class=\"page-regions\" aria-label=\"{page.Page} ページの領域設定の影響\"><h3>領域設定の影響</h3><p>領域設定で抑制した差分: <strong>{regions.SuppressedPixels} 画素・{regions.SuppressedComponents} 箇所</strong>。除外で消した基準差分: {regions.ExcludedPixels} 画素（重複なし）。</p>");
        html.Append("<p class=\"muted\">抑制した箇所は、ページ既定の生差分から領域設定で消えた画素の 8 近傍連結成分です。ノイズ除去や結合を行う通常の相違箇所とは別で、相違件数には加えません。基準のノイズ閾値未満の画素も含みます。判定画像の薄い橙色が抑制画素、青の破線と R 番号が領域、黄色が除外です。元 A/B と確認用オーバーレイには描き込みません。</p>");
        html.Append($"<div class=\"table-scroll\" role=\"region\" aria-label=\"{page.Page} ページの領域適用結果\" tabindex=\"0\"><table class=\"region-results\"><caption>A／補正後 B の座標。所有画素は内包・除外の優先順位を反映します。</caption><thead><tr><th>番号・名前</th><th>適用状態</th><th>範囲（px）</th><th>所有画素</th><th>生差分</th><th>抑制画素・箇所</th><th>除外画素</th><th>実行</th></tr></thead><tbody>");
        foreach (var r in regions.Items)
            html.Append($"<tr><th scope=\"row\">R{r.Index + 1} {H(r.Name)}</th><td>{RegionStatus(r.Status)}</td><td>X {r.BoundsPx.X} / Y {r.BoundsPx.Y}<br>{r.BoundsPx.W} × {r.BoundsPx.H}</td><td>{r.EffectivePixels}</td><td>{r.RawPixels}</td><td>{r.SuppressedPixels} / {r.SuppressedComponents}</td><td>{r.ExcludedPixels}</td><td>{(r.RunId is int run ? N(run) : "—")}</td></tr>");
        html.Append("</tbody></table></div><details class=\"region-runs\"><summary>比較設定ごとの全ページ実行</summary><p class=\"muted\">実行 0 がページ既定の基準実行です。ページと要約の吸収数・最大ずれには、この基準値だけを使います。以下の吸収数は全ページの実行値で、領域内の数ではありません。</p><ul>");
        foreach (var run in regions.Runs)
            html.Append($"<li>実行 {run.Id}: {DiffText(run.Diff)} · 吸収 {run.AbsorbedGroups} グループ / 最大ずれ {run.MaxShiftPx} px</li>");
        html.Append("</ul></details></section>");
    }

    private static string RegionStatus(string status) => status switch
    {
        "applied" => "適用", "outside_page" => "ページ外", "shadowed" => "内側領域・除外が優先",
        "not_applicable" => "対象外ページ", "not_compared" => "片側ページ・未比較", _ => H(status)
    };
}
