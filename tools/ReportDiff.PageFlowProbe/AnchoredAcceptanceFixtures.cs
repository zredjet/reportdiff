using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenCvSharp;
using ReportDiff.Pdf;

/// <summary>固定PDFを再生成して照合してから、実描画で受け入れる独立の合成PDFを作る。</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
internal static class AnchoredAcceptanceFixtures
{
    internal static void Create(string root, string output)
    {
        Directory.CreateDirectory(output);
        var original = Path.Combine(root, "tests/ReportDiff.Tests/Fixtures/page-flow-same-page-support/same-page-two");
        var a = Enumerable.Range(0, 10).Select(i => $"ROW ITEM {(char)('A' + i)}{(char)('A' + i)}").ToArray();
        var b = new List<string>();
        for (var i = 0; i < a.Length; i++)
        { if (i is 2 or 4) b.Add($"ROW ADDED {(char)('A' + i)} A"); b.Add(a[i]); }
        var hashes = new SortedDictionary<string, string>();
        var tone = Tone(Path.Combine(original, "b.pdf"));
        foreach (var variant in new[] { "original", "tone", "context", "too-different", "cluster-limit", "second-too-different", "second-cluster-limit" })
        foreach (var side in new[] { "a", "b" })
        {
            var revised = side == "b";
            var bytes = FlowFixture.Create(new("acceptance", 10), revised,
                layout: variant.EndsWith("too-different", StringComparison.Ordinal) || variant.EndsWith("cluster-limit", StringComparison.Ordinal) ? new(Width: 480) : null,
                explicitPages: (revised ? b.ToArray() : a).Chunk(6).ToArray(),
                pageDrawing: page => !revised || page != (variant.StartsWith("second-", StringComparison.Ordinal) ? 1 : 0)
                    ? "" : variant == "tone" ? tone : Drawing(variant));
            if (variant == "original")
            {
                if (!bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(original, side + ".pdf"))))
                    throw new InvalidOperationException("固定PDFの再生成が一致しません。");
                continue;
            }
            var relative = variant + "/" + side + ".pdf";
            Directory.CreateDirectory(Path.Combine(output, variant));
            File.WriteAllBytes(Path.Combine(output, relative), bytes);
            if (variant == "tone" && revised) VerifyTone(Path.Combine(original, "b.pdf"), Path.Combine(output, relative));
            hashes.Add(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        File.WriteAllText(Path.Combine(output, "sha256.json"), JsonSerializer.Serialize(hashes, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string Drawing(string variant)
    {
        // 座標は合成PDFのpt。製品のmm換算・しきい値には使わない。
        if (variant == "context") return "0 g 139.2 108 57.6 0.48 re f\n";
        if (variant == "too-different") return "0 g 0 84 480 144 re f\n";
        if (variant == "second-too-different") return "0 g 0 84 480 96 re f\n";
        if (!variant.EndsWith("cluster-limit", StringComparison.Ordinal)) return "";
        var result = new StringBuilder("0 g\n");
        for (var y = variant.StartsWith("second-", StringComparison.Ordinal) ? 506 : 306; y < 896; y += 18)
        for (var x = 500; x < 1980; x += 42)
            result.Append(CultureInfo.InvariantCulture, $"{x * .24} {300 - (y + 3) * .24} 0.72 0.72 re f\n");
        return result.ToString();
    }

    private static string Tone(string path)
    {
        using var pdf = PdfReader.Open(path); using var page = pdf.ReadPage(1);
        using var original = new Mat(page.Pixels, new Rect(0, 600, page.Pixels.Width, 100)); using var gray = new Mat();
        original.ConvertTo(gray, MatType.CV_8UC3, (255 - 166) / 255.0, 166);
        using var rgb = new Mat(); Cv2.CvtColor(gray, rgb, ColorConversionCodes.BGR2RGB);
        var bytes = new byte[rgb.Rows * rgb.Cols * 3]; Marshal.Copy(rgb.Data, bytes, 0, bytes.Length);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zip.Write(bytes);
        return FormattableString.Invariant($"q {rgb.Width * .24} 0 0 24 0 132 cm\nBI /W {rgb.Width} /H 100 /CS /RGB /BPC 8 /I false /F [/ASCIIHexDecode /FlateDecode] ID\n{Convert.ToHexString(compressed.ToArray())}>\nEI\nQ\n");
    }
    private static void VerifyTone(string originalPath, string targetPath)
    {
        using var originalPdf = PdfReader.Open(originalPath); using var targetPdf = PdfReader.Open(targetPath);
        using var original = originalPdf.ReadPage(1); using var actual = targetPdf.ReadPage(1);
        using var band = new Mat(original.Pixels, new Rect(0, 600, original.Pixels.Width, 100));
        band.ConvertTo(band, MatType.CV_8UC3, (255 - 166) / 255.0, 166);
        if (Cv2.Norm(original.Pixels, actual.Pixels, NormTypes.INF) != 0)
        {
            var differences = new List<string>();
            var size = original.Pixels.Size();
            for (var y = 0; y < size.Height && differences.Count < 10; y++)
            for (var x = 0; x < size.Width && differences.Count < 10; x++)
                if (original.Pixels.At<Vec3b>(y, x) != actual.Pixels.At<Vec3b>(y, x))
                    differences.Add($"({x},{y}) {original.Pixels.At<Vec3b>(y, x)} != {actual.Pixels.At<Vec3b>(y, x)}");
            throw new InvalidOperationException("色変更PDFの再描画が一致しません: " + string.Join("; ", differences));
        }
    }
}
