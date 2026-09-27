using ReportDiff.Tests;

if (args.Length != 1) throw new ArgumentException("合成PDFを書き出す空のフォルダを指定してください。");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new ArgumentException("出力先が空ではありません。");
Directory.CreateDirectory(root);
foreach (var name in new[] { "adopted", "columns", "fixed-grid", "short" })
    foreach (var revised in new[] { false, true })
        File.WriteAllBytes(Path.Combine(root, $"{name}-{(revised ? "b" : "a")}.pdf"), PdfFixture.RowA4Benchmark(name, revised));
foreach (var id in new[] { "R01", "R02" })
    foreach (var revised in new[] { false, true })
        File.WriteAllBytes(Path.Combine(root, $"{id}-{(revised ? "b" : "a")}.pdf"), PdfFixture.RowScenario(id, revised));
foreach (var revised in new[] { false, true })
    File.WriteAllBytes(Path.Combine(root, $"excluded-{(revised ? "b" : "a")}.pdf"), PdfFixture.ExcludedRowBenchmark(revised));
foreach (var revised in new[] { false, true })
    File.WriteAllBytes(Path.Combine(root, $"excluded-blocks-{(revised ? "b" : "a")}.pdf"), PdfFixture.ExcludedRowBenchmark(revised, true));
