using System.Runtime.Versioning;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Report;

[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("macOS")]

if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args.Length != 1) throw new ArgumentException("存在しない出力ディレクトリを指定してください。");
var output = args[0];
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("既存の出力先は上書きしません。");
Directory.CreateDirectory(output);
using var a = Draw(false);
using var b = Draw(true);
var inputA = Path.Combine(output, "inspection-a.png");
var inputB = Path.Combine(output, "inspection-b.png");
File.WriteAllBytes(inputA, a.ImEncode(".png"));
File.WriteAllBytes(inputB, b.ImEncode(".png"));
var settings = new AppSettings();
using var normalized = PageNormalizer.Normalize(a, b);
using var comparison = PageComparer.Compare(normalized.A, normalized.B, settings.ForPage(1, settings.Dpi));
var reportDirectory = Path.Combine(output, "result");
// 掲載サンプルは入力名と日時を固定し、利用者の絶対パスを埋め込まない。
var inputs = new ReportInputs(
    ReportInput.FromFile(inputA, InputFormat.Png, 1) with { Path = "inspection-a.png" },
    ReportInput.FromFile(inputB, InputFormat.Png, 1) with { Path = "inspection-b.png" });
var writer = new ReportWriter(reportDirectory, inputs, settings.ToReportConfiguration(), true,
    new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.FromHours(9)));
writer.AddComparedPage(1, normalized, comparison, settings.Dpi);
var report = writer.Complete();
HtmlReportWriter.Write(reportDirectory, report);
Console.WriteLine(JsonSerializer.Serialize(report.Summary, ReportJson.Options));

static Mat Draw(bool revised)
{
    // 架空の検査記録。外部帳票・画像・ロゴ・個人情報・フォントファイルは読み込まない。
    // 文字は既存依存OpenCVの内蔵Hershey書体で描画する。
    var image = new Mat(650, 1100, MatType.CV_8UC3, Scalar.All(255));
    var ink = new Scalar(62, 48, 35);
    var blue = new Scalar(140, 85, 38);
    var muted = new Scalar(130, 118, 104);
    var rule = new Scalar(216, 208, 197);
    void Text(string value, int x, int y, double size = 0.72, Scalar? color = null, int thickness = 1) =>
        Cv2.PutText(image, value, new Point(x, y), HersheyFonts.HersheyDuplex, size, color ?? ink, thickness, LineTypes.AntiAlias);
    Cv2.Rectangle(image, new Rect(52, 44, 8, 72), blue, -1);
    Text("DAILY INSPECTION REPORT", 80, 78, 1.08, blue, 2);
    Text("SAMPLE / 2026-09-21 / DEMO-001", 80, 108, 0.59, muted);
    Text("PROJECT", 56, 163, 0.52, muted);
    Text("CLIENT", 590, 163, 0.52, muted);
    Text("Report output verification", 56, 192, 0.72);
    Text("Sample Company (fictional)", 590, 192, 0.72);
    Cv2.Rectangle(image, new Rect(52, 224, 996, 44), new Scalar(247, 243, 237), -1);
    Text("ITEM", 72, 253, 0.64, blue);
    Text("COUNT", 606, 253, 0.64, blue);
    Text("RESULT", 826, 253, 0.64, blue);
    var items = new[] { "Documents", "Figures", "Tables" };
    var counts = new[] { revised ? "85" : "80", "48", "16" };
    for (var row = 0; row < 3; row++)
    {
        var y = 310 + row * 63;
        Text(items[row], 72, y, 0.79);
        Text(counts[row], 610, y, 0.84, ink, 2);
        Text("PASS", 828, y, 0.68);
        Cv2.Line(image, new Point(52, y + 23), new Point(1048, y + 23), rule, 1);
    }
    if (revised) Text("CHECKED", 792, 521, 0.86, blue, 2);
    else Text("DRAFT COPY", 72, 521, 0.86, muted, 2);
    Cv2.Line(image, new Point(52, 565), new Point(1048, 565), rule, 1);
    Text("SYNTHETIC DATA - REPORTDIFF DEMO", 56, 601, 0.5, muted);
    Text("01 / 01", 946, 601, 0.5, muted);
    return image;
}
