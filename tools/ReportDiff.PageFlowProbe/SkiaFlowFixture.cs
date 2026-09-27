using System.Security.Cryptography;
using System.Text.Json;
using ReportDiff.Core;
using SkiaSharp;

internal static class SkiaFlowFixture
{
    private static readonly FlowCase[] Cases = [new("skia-R10", 10), new("skia-R11", 12), new("skia-chain3", 16),
        new("skia-text-change", 10, "text"), new("skia-band-tone", 10, "pixels"),
        new("skia-paired-tone", 10, "paired"), new("skia-boundary-tone", 10, "paired-edge"),
        new("skia-extra", 12, "extra"), new("skia-repeated", 10, "repeated"),
        new("skia-partial-chain", 16, "partial"), new("skia-edge-repeat", 12, "edge-repeat"),
        new("skia-fractional", 10, "fractional")];

    internal static void Create(string output, bool localControls = false)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new ArgumentException("出力先が空ではありません。");
        Directory.CreateDirectory(output);
        var fontBytes = ProbeTrueType.Create();
        using var data = SKData.CreateCopy(fontBytes); using var typeface = SKTypeface.FromData(data);
        if (typeface is null || typeface.GlyphCount != 96) throw new InvalidOperationException("自作TrueTypeを読み込めません。");
        File.WriteAllBytes(Path.Combine(output, "probe.ttf"), fontBytes);
        var records = new List<object>();
        foreach (var test in localControls ? new FlowCase[] { new("skia-local-R10", 10, "local"),
            new("skia-local-R11", 12, "local"), new("skia-local-chain3", 16, "local") } : Cases)
        {
            var directory = Path.Combine(output, test.Id); Directory.CreateDirectory(directory);
            var a = Document(test, false, typeface); var b = Document(test, true, typeface);
            File.WriteAllBytes(Path.Combine(directory, "a.pdf"), a); File.WriteAllBytes(Path.Combine(directory, "b.pdf"), b);
            records.Add(new { test, generator = "SkiaSharp/4.150.1", font_sha256 = Hash(fontBytes),
                a_sha256 = Hash(a), b_sha256 = Hash(b),
                links = test.Mutation == "edge-repeat" ? [] : FlowFixture.Links(test).Select(l => new { link = l,
                    band_a = new { y = Px(72 + l.ASlot * 24), h = Px(24) },
                    band_b = new { y = Px(72 + l.BSlot * 24), h = Px(24) } }).ToArray() });
        }
        File.WriteAllText(Path.Combine(output, "observations.json"), JsonSerializer.Serialize(records,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }

    private static byte[] Document(FlowCase test, bool revised, SKTypeface typeface)
    {
        using var stream = new MemoryStream();
        using var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Creator = "ReportDiff.PageFlowProbe", Title = test.Id,
            Creation = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc),
            Modified = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc)
        });
        using var font = new SKFont(typeface, 9.6f) { Hinting = SKFontHinting.None, Subpixel = true };
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        var repeatedEdges = test.Mutation == "edge-repeat";
        var rows = FlowFixture.Rows(test, revised).Chunk(repeatedEdges ? 4 : 6).ToArray();
        for (var page = 0; page < rows.Length; page++)
        {
            using var canvas = document.BeginPage(240, 300);
            Text("HEAD START", 24); Text("SUB FIRST", 48);
            var body = repeatedEdges ? new[] { "REPEAT FIRST", "REPEAT SECOND" }.Concat(rows[page]).ToArray() : rows[page];
            for (var slot = 0; slot < body.Length; slot++)
            {
                var top = 72 + slot * 24;
                if (test.Mutation == "local") { canvas.Save(); canvas.Translate(0, top); top = 0; }
                paint.Color = revised && ((page == 1 && slot == 0 && test.Mutation == "pixels")
                    || (page == 0 && slot == 3 && test.Mutation == "paired")) ? new SKColor(166, 166, 166) : SKColors.Black;
                Text(body[slot], top + (test.Mutation == "fractional" ? .12f : 0));
                paint.Color = SKColors.Black;
                canvas.DrawRect(24, top + 20, 192, .48f, paint);
                canvas.DrawRect(24, top, 1.44f, 24, paint);
                if (test.Mutation == "local") canvas.Restore();
            }
            Text("FOOT FIXED", 252); Text("END CHECK", 276);
            if (revised && page == 0 && test.Mutation == "paired-edge")
            {
                paint.Color = new(128, 128, 128); canvas.DrawRect(24, 144, 8, .48f, paint);
            }
            if (revised && page == rows.Length - 1 && test.Mutation is "extra" or "partial")
            {
                paint.Color = new(217, 217, 217);
                canvas.DrawRect(130, test.Mutation == "partial" ? 202 : 139, 25, 8, paint);
            }
            paint.Color = SKColors.Black;
            document.EndPage();
            void Text(string value, float top) => canvas.DrawText(value, 40, top + 16.8f, SKTextAlign.Left, font, paint);
        }
        document.Close();
        return stream.ToArray();
    }
    private static int Px(double points) => Units.RoundPixels(points * 25.4 / 72, 300);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
