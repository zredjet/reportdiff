using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Core;
using ReportDiff.Pdf;
using ReportDiff.Tests;

[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("macOS")]

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("未使用の出力先を指定してください。");
Directory.CreateDirectory(args[0]);
var root = args[0];
var cluster = new[] { new DifferenceCluster(1, new Rect(0, 0, 1000, 1250), 1) };
var many = Enumerable.Range(0, 600).Select(i => new TextRun(i.ToString("D6"), 5 + i % 20 * 11, 290 - i / 20 * 9, 3)).ToArray();
var simple = new[] { new TextRun("帳票 AB 123", 20, 200) };
var fixtures = new (string Id, byte[] Data, Size Size, TextOptions Options, int Page, bool Empty, bool FontsFirst)[]
{
    ("type3", PdfFixture.CreateTextPage(simple), new(1000, 1250), new(), 1, false, false),
    ("many-words", PdfFixture.CreateTextPage(many), new(1000, 1250), new(), 1, false, false),
    ("crop", PdfFixture.CreateTextPage(simple, crop: "10 10 230 290"), new(917,1167), new(), 1, false, false),
    ("rotation", PdfFixture.CreateTextPage(simple, rotation:90), new(1250,1000), new(), 1, false, false),
    ("user-unit", PdfFixture.CreateTextPage(simple, userUnit:2), new(2000,2500), new(), 1, false, false),
    ("broken-text", PdfFixture.CreateTextPage(simple, brokenText:true), new(1000,1250), new(), 1, false, false),
    ("letter-limit", PdfFixture.CreateTextPage(simple), new(1000,1250), new() { MaxLettersPerPage = 1 }, 1, false, false),
    ("word-limit", PdfFixture.CreateTextPage(many), new(1000,1250), new() { MaxWordsPerPage = 1 }, 1, false, false),
    ("truncate", PdfFixture.CreateTextPage(simple), new(1000,1250), new() { MaxRunesPerCluster = 1 }, 1, false, false),
    ("no-clusters", PdfFixture.CreateTextPage(simple), new(1000,1250), new(), 1, true, false),
    ("fonts-first", PdfFixture.CreateTextPage(simple), new(1000,1250), new(), 1, false, true),
    ("standard14", PdfFixture.CreateFontReport(), new(417,417), new(), 1, false, false),
    ("truetype", PdfFixture.CreateFontReport(font:PdfFixture.EmbeddedTrueType), new(417,417), new(), 1, false, false),
    ("form-annotation", PdfFixture.CreateFontReport(form:true, annotation:true), new(417,417), new(), 1, false, false),
    ("page2", PdfFixture.CreateFontReport(contents:[PdfFixture.FontText,PdfFixture.FontText]), new(417,417), new(), 2, false, false),
    ("invalid-page", PdfFixture.CreateTextPage(simple), new(1000,1250), new(), 2, false, false),
    ("invalid-pdf", [37,80,68,70,45,49,46,55,10], new(1000,1250), new(), 1, false, false)
};
var records = new List<object>();
foreach (var f in fixtures)
{
    var path = Path.Combine(root, f.Id + ".pdf"); File.WriteAllBytes(path, f.Data);
    string Describe(PdfTextReader reader, bool shifted)
    {
        var fontsBefore = f.FontsFirst ? reader.InspectFonts(f.Page) : null;
        var text = reader.Annotate(f.Page, f.Size, 300, f.Empty ? [] : cluster,
            shifted ? [new RectMm(1, 1, 2, 2)] : [], shifted ? new GlobalShift(1,-1) : null);
        var fonts = reader.InspectFonts(f.Page);
        var repeated = reader.InspectFonts(f.Page);
        if (JsonSerializer.Serialize(fonts) != JsonSerializer.Serialize(repeated)) throw new InvalidOperationException("フォントキャッシュ不一致");
        return JsonSerializer.Serialize(new { text, fonts, fontsBefore });
    }
    string? first = null;
    for (var repeat = 0; repeat < 8; repeat++)
    {
        using var a = new PdfTextReader(path, f.Options); using var b = new PdfTextReader(path, f.Options);
        string? resultA = null, resultB = null;
        var started = Stopwatch.GetTimestamp();
        Run(() => resultA = Describe(a, false), () => resultB = Describe(b, true));
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        using var expectedA = new PdfTextReader(path, f.Options); using var expectedB = new PdfTextReader(path, f.Options);
        if (resultA != Describe(expectedA, false) || resultB != Describe(expectedB, true)) throw new InvalidOperationException("逐次参照と不一致");
        var serialized = JsonSerializer.Serialize(new { resultA, resultB });
        first ??= serialized;
        if (first != serialized) throw new InvalidOperationException("反復時に結果が変化");
        records.Add(new { f.Id, repeat, elapsed_ms = elapsed, exact = true,
            result_sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(serialized))),
            input_sha256 = Convert.ToHexString(SHA256.HashData(f.Data)) });
    }
}

#if PAIR2
// 両方の例外を回収するまで返らないことと、A側を優先することを試作自体で確認。
if (Environment.ProcessorCount > 1)
{
    using var started = new Barrier(2); using var enteredB = new ManualResetEventSlim();
    using var releaseB = new ManualResetEventSlim(); var finishedB = false;
    var job = Task.Run(() => PdfPairTrial.Run(true,
        () => { if (!started.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); throw new InvalidOperationException("A"); },
        () => { if (!started.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); enteredB.Set();
            if (!releaseB.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); finishedB = true; throw new InvalidOperationException("B"); }));
    try
    {
        if (!enteredB.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        if (job.IsCompleted) throw new InvalidOperationException("B終了前に復帰");
    }
    finally { releaseB.Set(); }
    try { job.GetAwaiter().GetResult(); throw new InvalidOperationException("例外なし"); }
    catch (InvalidOperationException e) when (e.Message == "A" && finishedB) { }
}
#endif
File.WriteAllText(Path.Combine(root,"checks.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));

static void Run(Action a, Action b)
{
#if PAIR2
    PdfPairTrial.Run(true, a, b);
#else
    a(); b();
#endif
}
