using System.Text;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Cli;
using ReportDiff.Report;

namespace ReportDiff.Tests;

internal sealed class DirectoryTestFiles : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "reportdiff-batch 帳票-" + Guid.NewGuid().ToString("N"));
    public string A => Path.Combine(Root, "A");
    public string B => Path.Combine(Root, "B");
    public string Output => Path.Combine(Root, "比較 結果");
    public CompareCommand Command => CommandLine.Parse(["compare-dir", A, B, "--out", Output]);
    public DirectoryTestFiles() { Directory.CreateDirectory(A); Directory.CreateDirectory(B); }
    public string Bytes(string name, byte[] bytes)
    {
        // Windows でも単一比較とフォルダ比較へ同じ区切り表記の絶対パスを渡す。
        var path = Path.GetFullPath(Path.Combine(Root, name)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes); return path;
    }
    public string Text(string name, string text) => Bytes(name, Encoding.UTF8.GetBytes(text));
    public string Image(string name, bool changed = false)
    {
        using var image = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(255));
        if (changed) Cv2.Rectangle(image, new Rect(20, 20, 10, 8), Scalar.All(0), -1);
        return Bytes(name, image.ImEncode(".png"));
    }
    public DirectoryReportDocument Read() => JsonSerializer.Deserialize<DirectoryReportDocument>(File.ReadAllText(Path.Combine(Output, "index.json")), ReportJson.Options)!;
    public ReportDocument Child(DirectoryFileResult file) => JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(Path.Combine(Output, file.Json!)), ReportJson.Options)!;
    public Task<CliResult> Run(params string[] extra) => CliProcess.Run(["compare-dir", A, B, "--out", Output, .. extra]);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
