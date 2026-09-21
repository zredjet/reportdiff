using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ReportDiff.Report;

/// <summary>個別結果への安全な相対リンクを持つ、スクリプト不要の一覧。</summary>
public static class DirectoryReportWriter
{
    public static void Write(string output, DirectoryReportDocument report, bool noHtml)
    {
        try
        {
            Directory.CreateDirectory(output);
            using (var stream = new FileStream(Path.Combine(output, "index.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(stream, report, ReportJson.Options);
            if (!noHtml)
            {
                using var stream = new FileStream(Path.Combine(output, "index.html"), FileMode.CreateNew);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(Render(report));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ReportWriteException("一覧レポートを保存できません。出力先・アクセス権・空き容量を確認してください。", ex); }
    }

    public static string Render(DirectoryReportDocument report)
    {
        var s = report.Summary;
        var html = new StringBuilder("""
            <!doctype html><html lang="ja"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <title>フォルダ比較 — ReportDiff</title><style>
            *{box-sizing:border-box}body{margin:0;background:#f3f5f8;color:#172b3a;font:16px/1.65 system-ui,sans-serif}
            main{max-width:1180px;margin:auto;padding:32px 24px}h1{margin:0;font-size:1.8rem}h2{font-size:1.2rem;margin:28px 0 12px}
            p{margin:8px 0}.muted{color:#516575}.path,.error{overflow-wrap:anywhere;white-space:pre-wrap}
            .counts{display:flex;flex-wrap:wrap;gap:10px;margin:20px 0}.count{background:white;border:1px solid #d4dde4;border-radius:8px;padding:10px 16px}
            .count strong{display:block;font-size:1.45rem}.file{background:white;border:1px solid #cbd7e0;border-radius:10px;padding:18px 20px;margin:12px 0}
            .file h3{font-size:1.05rem;margin:0 0 8px}.badge{display:inline-block;border-radius:4px;padding:2px 8px;font-weight:650;background:#e7eff5;margin-bottom:8px}
            .same{background:#dff3e9;color:#155836}.different,.only_in_a,.only_in_b{background:#fff0c9;color:#72520b}.error{background:#fff0ef;color:#8a2521}
            dl{display:grid;grid-template-columns:7em minmax(0,1fr);margin:8px 0}dt{color:#516575}dd{margin:0;overflow-wrap:anywhere}
            a{color:#075aa2;text-underline-offset:3px}a:focus-visible,summary:focus-visible{outline:3px solid #075aa2;outline-offset:4px}
            .links{display:flex;flex-wrap:wrap;gap:20px;margin-top:12px}.links a{padding:6px 0;min-height:40px}
            .error{padding:10px;border-radius:5px}details{margin-top:24px}summary{cursor:pointer;font-weight:650;padding:8px 0}
            li{margin:8px 0;overflow-wrap:anywhere}footer{margin-top:32px;color:#516575;border-top:1px solid #cbd7e0;padding-top:16px}
            @media(max-width:600px){main{padding:20px 14px}.file{padding:14px}h1{font-size:1.4rem}dl{grid-template-columns:1fr}dd{margin-bottom:6px}.count{padding:8px 12px}}
            </style></head><body><main><h1>フォルダ比較</h1>
            """);
        html.Append($"<p><span class=\"badge {E(s.Status)}\">{Status(s.Status)}</span></p>");
        html.Append($"<dl><dt>入力 A</dt><dd class=\"path\">{E(report.Inputs.A)}</dd><dt>入力 B</dt><dd class=\"path\">{E(report.Inputs.B)}</dd></dl>");
        html.Append("<div class=\"counts\">");
        foreach (var (label, count) in new[] { ("全対象", s.Total), ("比較成功", s.Compared), ("相違なし", s.Same), ("相違あり", s.Different),
            ("A のみ", s.OnlyInA), ("B のみ", s.OnlyInB), ("エラー", s.Error), ("対象外", s.Ignored) })
            html.Append($"<div class=\"count\">{label}<strong>{count.ToString(CultureInfo.InvariantCulture)}</strong></div>");
        html.Append("</div>");
        foreach (var warning in report.Warnings) html.Append($"<p class=\"error\">{E(warning.Code)}: {E(warning.Message)}</p>");
        html.Append($"<p class=\"muted path\">共通設定: {E(report.Configuration.Common?.Path ?? "既定値")}<br>選択定義: {E(report.Configuration.Rules?.Path ?? "指定なし")}</p>");
        html.Append("<h2>ファイルごとの結果</h2><p class=\"muted\">片側のみのファイルは存在の差分です。内容は検査していません。</p>");
        foreach (var file in report.Files)
        {
            html.Append($"<article class=\"file\" id=\"{E(file.Id)}\"><span class=\"badge {E(file.Status)}\">{Status(file.Status)}</span><h3 class=\"path\">{E(file.RelativePath)}</h3>");
            html.Append($"<dl><dt>A</dt><dd class=\"path\">{E(file.RelativePathA ?? "存在しません")}</dd><dt>B</dt><dd class=\"path\">{E(file.RelativePathB ?? "存在しません")}</dd>");
            var setting = file.Status is "only_in_a" or "only_in_b" ? "未適用（未比較）"
                : file.SelectedRule is { } rule ? $"規則 {rule.Index}: {rule.Config} / {rule.Pattern}" : file.Error?.Code == "CONFIG_RULE_AMBIGUOUS" ? "未確定（複数の規則が一致）" : "共通設定";
            html.Append($"<dt>適用設定</dt><dd>{E(setting)}</dd><dt>比較結果</dt><dd>");
            html.Append(file.Comparison is { } c ? $"比較 {c.PagesCompared} ページ / 相違 {c.PagesDifferent} ページ / {c.Clusters} 箇所 / 警告 {file.WarningCount} 件" : "未比較（ページ数・警告数は未検査）");
            html.Append("</dd></dl>");
            if (file.Error is { } error)
            {
                html.Append($"<p class=\"error\">{E(error.Code)}: {E(error.Message)}</p>");
                foreach (var match in error.MatchedRules) html.Append($"<p class=\"path\">一致した規則 {match.Index}: {E(match.Pattern)} → {E(match.Config)}</p>");
            }
            if (file.Json is not null || file.Html is not null)
            {
                html.Append("<nav class=\"links\" aria-label=\"個別結果\">");
                if (file.Html is not null) Link(file.Html, "個別レポートを開く");
                if (file.Json is not null) Link(file.Json, "個別 JSON");
                html.Append("</nav>");
            }
            html.Append("</article>");
        }
        html.Append($"<details><summary>対象外ファイル（{report.Ignored.Count} 件）</summary><ul>");
        foreach (var ignored in report.Ignored) html.Append($"<li>{E(ignored.Side)}: {E(ignored.RelativePath)} — {E(ignored.Reason)}</li>");
        html.Append($"</ul></details><footer>{E(report.Tool.Name)} {E(report.Tool.Version)} / {E(report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture))}<p><a href=\"index.json\">一覧 JSON</a> · 個別の警告・実効設定は各レポートを参照してください。</p></footer></main></body></html>");
        return html.ToString();

        void Link(string path, string label)
        {
            // 呼び出し元の文書でも任意の URL をリンクにしない。製品の出力 ID は ASCII 固定。
            if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"\Afiles/f[0-9]{6,}/(report\.html|result\.json)\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new ArgumentException("個別結果への相対リンクが不正です。");
            html.Append($"<a href=\"{E(path)}\">{label}</a>");
        }
    }
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Status(string status) => status switch
    { "same" => "相違なし", "different" => "相違あり", "only_in_a" => "A のみ", "only_in_b" => "B のみ", _ => "エラーあり" };
}
