using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReportDiff.Report;

/// <summary>Complete 済みの結果と同じディレクトリへ、オフライン用 HTML を追加する。</summary>
public static partial class HtmlReportWriter
{
    private static readonly string Styles = ReadResource("ReportStyles.css");
    private static readonly string Interactions = ReadResource("ReportInteractions.js");

    public static void Write(string outputDirectory, ReportDocument report)
    {
        var html = Render(report);
        try
        {
            using var stream = new FileStream(Path.Combine(outputDirectory, "report.html"), FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(html);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReportWriteException("HTML を書き込めません。出力先・空き容量と report.html が既に存在しないかを確認してください。", ex);
        }
    }

    public static string Render(ReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="ja">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'self' file:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <title>比較結果 — ReportDiff</title>
            <style>{{Styles}}</style>
            </head>
            <body>
            <a class="skip-link" href="#pages">ページ別の結果へ</a>
            <main>
            <header>
            <p class="eyebrow">ReportDiff · 比較レポート</p>
            <h1>帳票の比較結果 <span class="status {{(report.Summary.Status == "same" ? "same" : "different")}}">{{Status(report.Summary.Status)}}</span></h1>
            <p class="muted">{{H(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))}} · {{H(report.Tool.Name)}} {{H(report.Tool.Version)}}</p>
            </header>
            <section aria-label="要約" class="card">
            <div class="inputs">
            {{Input("A · 基準", report.Inputs.A)}}
            {{Input("B · 比較", report.Inputs.B)}}
            </div>
            <dl class="metrics summary-metrics">
            {{Metric("比較したページ", report.Summary.PagesCompared)}}
            {{Metric("相違のあるページ", report.Summary.PagesDifferent)}}
            {{Metric("相違箇所", report.Summary.Clusters)}}
            {{Metric("位置ずれを吸収した数", report.Summary.AbsorbedGroups)}}
            </dl>
            <p class="muted">比較・相違のページ数は今回の出力対象分です。片側だけのページも相違に含みます。吸収数は位置ずれとして相違から除いたグループ数です。</p>
            </section>
            """);
        html.Append("<section class=\"card warnings\" aria-labelledby=\"warnings-title\"><h2 id=\"warnings-title\">警告</h2>");
        if (report.Warnings.Count == 0) html.Append("<p>警告はありません。</p>");
        else
        {
            html.Append("<ul>");
            foreach (var warning in report.Warnings)
                html.Append($"<li>{H(warning.Message)} <code>{H(warning.Code)}</code></li>");
            html.Append("</ul>");
        }
        html.Append("</section>");
        AppendSettings(html, report.Config);
        html.Append("<section id=\"pages\" aria-labelledby=\"pages-title\"><h2 id=\"pages-title\">ページ別の結果</h2><p class=\"muted\">重ね描きの赤は差分・輪郭・番号、黄色は除外領域です。画像を選ぶと元の大きさで開きます。</p>");
        foreach (var page in report.Pages) AppendPage(html, page);
        html.Append("</section><footer>ReportDiff · オフライン比較レポート</footer></main>");
        // HTML の終了タグとして解釈されない既定エンコーダを使用する。スラッシュも符号化し、URL のリテラルを残さない。
        var json = JsonSerializer.Serialize(report, ReportJson.Options).Replace("/", "\\u002F", StringComparison.Ordinal);
        html.Append($"<script type=\"application/json\" id=\"result\">{json}</script><script>{Interactions}</script></body></html>");
        return html.ToString();
    }

    private static string Input(string label, ReportInput input) => $"""
        <div><h2>{label}</h2><p class="input-path">{H(input.Path)}</p><p class="muted">{H(input.Type.ToUpperInvariant())} · {input.Pages} ページ</p></div>
        """;

    private static void AppendSettings(StringBuilder html, ReportConfiguration config)
    {
        html.Append("<details class=\"card settings\"><summary>使った設定・除外領域</summary><dl class=\"settings-grid\">");
        html.Append(Metric("PDF の描画解像度", $"{config.Dpi} dpi"));
        html.Append(Metric("画像の換算解像度", $"{config.ImageDpi} dpi"));
        html.Append(Metric("位置ずれの許容幅", $"{N(config.Diff.MaxShiftMm)} mm"));
        html.Append(Metric("色の差のしきい値", config.Diff.ColorThreshold));
        html.Append(Metric("コントラスト比例の許容係数", N(config.Diff.EdgeTolerance)));
        html.Append(Metric("クラスタの結合距離・横", $"{N(config.Cluster.MergeXMm)} mm"));
        html.Append(Metric("クラスタの結合距離・縦", $"{N(config.Cluster.MergeYMm)} mm"));
        html.Append(Metric("クラスタの最小画素数", config.Cluster.MinPixels));
        html.Append(Metric("ページのクラスタ数上限", config.Cluster.MaxClustersPerPage));
        html.Append(Metric("差分の割合の上限", N(config.Cluster.MaxDiffRatio)));
        html.Append(Metric("切り出し画像の余白", $"{N(config.Report.CropMarginMm)} mm"));
        html.Append("</dl><h3>除外領域</h3>");
        if (config.Exclude.Count == 0) html.Append("<p>除外領域はありません。</p>");
        else
        {
            html.Append("<div class=\"table-scroll\" role=\"region\" aria-label=\"除外領域の一覧\" tabindex=\"0\"><table><caption>左上を原点とする位置と大きさ（mm）</caption><thead><tr><th scope=\"col\">ページ</th><th scope=\"col\">X</th><th scope=\"col\">Y</th><th scope=\"col\">幅</th><th scope=\"col\">高さ</th><th scope=\"col\">メモ</th></tr></thead><tbody>");
            foreach (var exclusion in config.Exclude)
                html.Append($"<tr><td>{(exclusion.Page is null ? "すべて" : N(exclusion.Page.Value))}</td><td>{N(exclusion.X)}</td><td>{N(exclusion.Y)}</td><td>{N(exclusion.W)}</td><td>{N(exclusion.H)}</td><td>{H(exclusion.Note)}</td></tr>");
            html.Append("</tbody></table></div>");
        }
        html.Append("</details>");
    }

    private static void AppendPage(StringBuilder html, ReportPage page)
    {
        var same = page.Status == "same";
        html.Append($"<details class=\"card page\" id=\"page-{page.Page}\"{(same ? "" : " open")}><summary><span>{page.Page} ページ</span> <span class=\"status {(same ? "same" : "different")}\">{Status(page.Status)}</span></summary>");
        html.Append("<dl class=\"metrics page-metrics\">");
        html.Append(Metric("画像サイズ", $"{page.SizePx.W} × {page.SizePx.H} px"));
        html.Append(Metric("生の差分の画素数", page.RawPixels));
        html.Append(Metric("除外したノイズの数", page.NoiseDropped));
        html.Append(Metric("位置ずれを吸収した数", page.AbsorbedGroups));
        html.Append(Metric("吸収に使った最大ずれ", $"{page.MaxShiftPx} px"));
        html.Append("</dl>");
        if (page.SizeMismatch) html.Append("<p class=\"notice\">画像サイズが異なるため、右と下を白で埋めて比較しました。</p>");
        AppendViewer(html, page);
        if (page.Clusters.Count > 0)
        {
            html.Append($"<h3>相違箇所 <span class=\"muted\">{page.Clusters.Count} 件</span></h3><p class=\"muted table-hint\">一覧は横にスクロールできます。</p><div class=\"table-scroll\" role=\"region\" aria-label=\"{page.Page} ページの相違箇所\" tabindex=\"0\"><table class=\"clusters\"><caption>位置は左上が原点です。位置と大きさは mm（小数第 2 位まで）、画素数は差分に属する画素数です。</caption><thead><tr><th scope=\"col\">番号</th><th scope=\"col\">位置・大きさ（mm）</th><th scope=\"col\">画素数</th><th scope=\"col\">A · 基準</th><th scope=\"col\">B · 比較</th><th scope=\"col\">差分</th></tr></thead><tbody>");
            foreach (var cluster in page.Clusters)
            {
                var box = cluster.BboxMm;
                html.Append($"<tr><th scope=\"row\">{cluster.Id}</th><td class=\"bounds\">X {Mm(box.X)} · Y {Mm(box.Y)}<br>幅 {Mm(box.W)} × 高さ {Mm(box.H)}</td><td>{cluster.Pixels}</td>");
                AppendCrop(html, cluster.Crops.A, $"{page.Page} ページ・相違 {cluster.Id}・A の切り出し");
                AppendCrop(html, cluster.Crops.B, $"{page.Page} ページ・相違 {cluster.Id}・B の切り出し");
                AppendCrop(html, cluster.Crops.Diff, $"{page.Page} ページ・相違 {cluster.Id}・差分の切り出し");
                html.Append("</tr>");
            }
            html.Append("</tbody></table></div>");
        }
        else html.Append($"<p class=\"muted\">{page.Status switch
        {
            "too_different" => "差分の割合が上限を超えたため、相違箇所のクラスタ化を省略しました。",
            "only_in_a" or "only_in_b" => "対応するページがないため、画素の比較と相違箇所の切り出しは行っていません。",
            _ => "相違箇所はありません。"
        }}</p>");
        html.Append("</details>");
    }

    private static void AppendViewer(StringBuilder html, ReportPage page)
    {
        (string Label, string? Path)[] views = [("A · 基準", page.Images.A), ("B · 比較", page.Images.B), ("重ね描き", page.Images.Overlay)];
        foreach (var view in views) if (view.Path is not null) ValidateImagePath(view.Path);
        var initial = views.LastOrDefault(v => v.Path is not null);
        if (initial.Path is null)
        {
            html.Append("<p class=\"muted\">相違のないページの画像は保存されていません。</p>");
            return;
        }
        html.Append($"<figure class=\"viewer\"><figcaption class=\"viewer-toolbar\"><div class=\"image-switch\" role=\"group\" aria-label=\"{page.Page} ページの表示画像\" hidden>");
        foreach (var view in views)
        {
            var alt = $"{page.Page} ページ・{view.Label}";
            html.Append($"<button type=\"button\" aria-pressed=\"{(view.Path == initial.Path ? "true" : "false")}\" data-src=\"{view.Path}\" data-label=\"{H(alt)}\"{(view.Path is null ? " disabled" : "")}>{view.Label}</button>");
        }
        html.Append($"</div><span class=\"viewer-label\" aria-live=\"polite\">{page.Page} ページ・{initial.Label}</span></figcaption><a class=\"page-image-link\" href=\"{initial.Path}\"><img class=\"page-image\" src=\"{initial.Path}\" alt=\"{page.Page} ページ・{initial.Label}\" loading=\"lazy\" width=\"{page.SizePx.W}\" height=\"{page.SizePx.H}\"></a></figure>");
    }

    private static void AppendCrop(StringBuilder html, string path, string alt)
    {
        ValidateImagePath(path);
        html.Append($"<td><a href=\"{path}\"><img class=\"crop\" src=\"{path}\" alt=\"{H(alt)}\" loading=\"lazy\"></a></td>");
    }

    private static string Metric(string label, object value) => $"<div><dt>{label}</dt><dd>{H(Convert.ToString(value, CultureInfo.InvariantCulture)!)}</dd></div>";
    private static string N(double value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Mm(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string H(string value) => WebUtility.HtmlEncode(value).Replace("/", "&#47;", StringComparison.Ordinal);
    private static string Status(string value) => value switch
    {
        "same" => "相違なし", "different" => "相違あり", "too_different" => "差分が多すぎます",
        "only_in_a" => "A のみに存在", "only_in_b" => "B のみに存在",
        _ => throw new ReportWriteException("比較結果の状態が不正です。")
    };
    private static void ValidateImagePath(string path)
    {
        if (!LocalImagePath().IsMatch(path)) throw new ReportWriteException("画像には pages または crops 内の PNG 相対パスを指定してください。");
    }
    [GeneratedRegex(@"\A(?:pages|crops)/[A-Za-z0-9_-]+\.png\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalImagePath();
    private static string ReadResource(string name)
    {
        using var stream = typeof(HtmlReportWriter).Assembly.GetManifestResourceStream("ReportDiff.Report." + name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
