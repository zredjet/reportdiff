using System.Security.Cryptography;
using System.Text.Json;
using ReportDiff.Core;
using SkiaSharp;

internal sealed record ScaleCase(string Id, int Pages, int Capacity = 24, string Mutation = "none", int Repeats = 3);

// A4寸法と本文量だけを拡張する。自作フォント・行内描画は既存Skia検証と同じ。
internal static class ScaleFixtures
{
    internal static readonly ScaleCase[] Cases = [
        new("a4-2", 2), new("a4-8", 8), new("a4-16", 16),
        new("a4-30", 30, Repeats: 1), new("a4-31", 31, Repeats: 1),
        new("a4-dense-2", 2, 48), new("a4-content-2", 2, Mutation: "content"),
        new("a4-band-change-2", 2, Mutation: "band")];
    private const float Width = (float)(210 * 72 / 25.4), Height = (float)(297 * 72 / 25.4);

    internal static void VerifyLegacy(string input)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(input, "observations.json")));
        foreach (var test in FlowFixture.Cases)
        {
            var expected = manifest.RootElement.EnumerateArray().Single(e => e.GetProperty("test").GetProperty("id").GetString() == test.Id);
            foreach (var revised in new[] { false, true })
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(FlowFixture.Create(test, revised)));
                if (hash != expected.GetProperty(revised ? "b_sha256" : "a_sha256").GetString())
                    throw new InvalidOperationException($"既存Type3入力の再生成結果が変わりました: {test.Id}/{revised}");
            }
        }
        Console.WriteLine("既存Type3 20PDFの再生成ハッシュ一致");
    }

    internal static void CreateGrid(string output, bool integralPoints = false, bool sparseOnly = false)
    {
        if (Directory.Exists(output)) throw new ArgumentException("未使用の出力先を指定してください。");
        Directory.CreateDirectory(output);
        var records = new List<object>();
        foreach (var test in sparseOnly ? new ScaleCase[] { new("a4-sparse-2", 2, 6), new("a4-sparse-16", 16, 6) } : Cases)
        {
            var folder = Path.Combine(output, test.Id); Directory.CreateDirectory(folder);
            var total = test.Pages * test.Capacity - 2;
            var a = Enumerable.Range(0, total).Select(i => $"ITEM R{i:D6} DESCRIPTION VALUE").ToArray();
            var b = a.Take(2).Concat(["NEW ADDED ROW VALUE"]).Concat(a.Skip(2)).ToArray();
            var pitch = Math.Min(24, 576.0 / test.Capacity);
            var layout = new FlowLayout(integralPoints ? 594 : 595.2, integralPoints ? 840 : 841.92, 96, pitch, 744,
                test.Capacity == 48 ? 4.8 : 9.6, pitch * .7, pitch - 4, 540);
            var flowCase = new FlowCase(test.Id, total, test.Mutation == "content" ? "paired" : test.Mutation == "band" ? "pixels" : "none");
            File.WriteAllBytes(Path.Combine(folder, "a.pdf"), FlowFixture.Create(flowCase, false, layout, a.Chunk(test.Capacity).ToArray()));
            File.WriteAllBytes(Path.Combine(folder, "b.pdf"), FlowFixture.Create(flowCase, true, layout, b.Chunk(test.Capacity).ToArray()));
            records.Add(new { test, layout, dpi = 300, a_rows = a, b_rows = b,
                a_sha256 = Hash(Path.Combine(folder, "a.pdf")), b_sha256 = Hash(Path.Combine(folder, "b.pdf")),
                expected_flow = test.Pages == 31 || test.Mutation == "band" ? "skipped" : "applied",
                expected_aggregate = test.Pages == 31 || test.Mutation == "band" ? (int?)null : test.Mutation == "content" ? 2 : 1 });
        }
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { generator = "Type3/pixel-grid-control", cases = records },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }

    internal static void Create(string output, bool insetControl = false)
    {
        if (Directory.Exists(output)) throw new ArgumentException("未使用の出力先を指定してください。");
        Directory.CreateDirectory(output);
        var bytes = ProbeTrueType.Create();
        using var data = SKData.CreateCopy(bytes); using var face = SKTypeface.FromData(data);
        if (face is null) throw new InvalidOperationException("検証フォントを読み込めません。");
        var records = new List<object>();
        foreach (var test in Cases)
        {
            var folder = Path.Combine(output, test.Id); Directory.CreateDirectory(folder);
            var total = test.Pages * test.Capacity - 2;
            var a = Enumerable.Range(0, total).ToArray();
            var b = a.Take(3).Concat([-1]).Concat(a.Skip(3)).ToArray();
            var paths = new[] { Path.Combine(folder, "a.pdf"), Path.Combine(folder, "b.pdf") };
            File.WriteAllBytes(paths[0], Document(test, a, false, face, insetControl));
            File.WriteAllBytes(paths[1], Document(test, b, true, face, insetControl));
            // 期待する所属は生成時の行IDから保存する。製品にはPDFしか渡さない。
            records.Add(new { test, inset_control = insetControl, width_mm = 210, height_mm = 297, dpi = 300,
                a_sha256 = Hash(paths[0]), b_sha256 = Hash(paths[1]), a_rows = a, b_rows = b,
                links = Enumerable.Range(1, test.Pages - 1).Select(p => new { source_page = p, target_page = p + 1, row_id = p * test.Capacity - 1 }),
                expected_flow = test.Pages == 31 || test.Mutation == "band" ? "skipped" : "applied",
                expected_aggregate = test.Pages == 31 || test.Mutation == "band" ? (int?)null : test.Mutation == "content" ? 2 : 1 });
        }
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { generator = "SkiaSharp/4.150.1", font_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), cases = records },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }

    private static byte[] Document(ScaleCase test, int[] rows, bool revised, SKTypeface face, bool insetControl)
    {
        using var stream = new MemoryStream();
        using var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata {
            Creator = "ReportDiff.PageFlowProbe", Title = test.Id,
            Creation = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc),
            Modified = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc) });
        using var body = new SKFont(face, test.Capacity == 48 ? 7.2f : 9.6f) { Hinting = SKFontHinting.None, Subpixel = true };
        using var fixedFont = new SKFont(face, 9.6f) { Hinting = SKFontHinting.None, Subpixel = true };
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        var pitch = 576f / test.Capacity;
        foreach (var page in rows.Chunk(test.Capacity))
        {
            using var canvas = document.BeginPage(Width, Height);
            canvas.DrawText("REPORT HEADER", 40, 40.8f, SKTextAlign.Left, fixedFont, paint);
            canvas.DrawText("FIXED COLUMNS", 40, 64.8f, SKTextAlign.Left, fixedFont, paint);
            for (var slot = 0; slot < page.Length; slot++)
            {
                var id = page[slot];
                canvas.Save(); canvas.Translate(0, 96 + slot * pitch);
                var text = id == -1 ? "NEW ADDED ROW VALUE" : $"ITEM R{id:D6} DESCRIPTION VALUE";
                canvas.DrawText(text, 40, pitch * .7f, SKTextAlign.Left, body, paint);
                canvas.DrawRect(24, pitch - 4, 540, .48f, paint);
                canvas.DrawRect(24, 0, 1.44f, pitch - (insetControl ? .12f : 0), paint);
                // 内容変更は送り帯を避ける。帯変更は最初の送り行だけを変える。
                var changed = revised && (test.Mutation == "band" && id == test.Capacity - 1
                    || test.Mutation == "content" && id == rows.Length - 5);
                paint.Color = changed ? new SKColor(166, 166, 166) : SKColors.Black;
                canvas.DrawRect(480, pitch * .25f, 24, pitch * .35f, paint);
                paint.Color = SKColors.Black;
                canvas.Restore();
            }
            canvas.DrawText("FOOTER FIXED", 40, 760.8f, SKTextAlign.Left, fixedFont, paint);
            canvas.DrawText("END CHECK", 40, 784.8f, SKTextAlign.Left, fixedFont, paint);
            document.EndPage();
        }
        document.Close();
        return stream.ToArray();
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
