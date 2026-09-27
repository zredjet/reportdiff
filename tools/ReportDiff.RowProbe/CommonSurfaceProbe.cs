using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;
using ReportDiff.Report;

// 5.2〜5.5 は既存コアをそのまま呼ぶ。比較面・表示面の対応だけを独立して試す。
internal static class CommonSurfaceProbe
{
    internal sealed record Piece(int common_y, int display_y, int length, int? a_y, int? b_y, string kind);
    internal sealed record Surface(PageMap Map, IReadOnlyList<Piece> Pieces);

    internal static Surface Build(Mat a, Mat b, PageMap display, bool retainWhite)
    {
        var commonY = 0; var nextA = 0; var nextB = 0;
        var pieces = new List<Piece>(); var removedSides = new HashSet<PageSpace>();
        foreach (var segment in display.Segments)
        {
            if (segment.AStart is int ay)
            {
                if (ay != nextA || ay + segment.Length > a.Height) throw new ArgumentException("A の全行を一度ずつ扱ってください。");
                nextA += segment.Length;
            }
            if (segment.BStart is int by)
            {
                if (by != nextB || by + segment.Length > b.Height) throw new ArgumentException("B の全行を一度ずつ扱ってください。");
                nextB += segment.Length;
            }
            var kind = "paired";
            if (segment.AStart is not null && segment.BStart is not null)
            {
                if (removedSides.Count > 1) throw new InvalidOperationException("unanchored_join");
                removedSides.Clear();
            }
            else
            {
                var side = segment.AStart is not null ? PageSpace.A : PageSpace.B;
                var source = side == PageSpace.A ? a : b;
                var y = (side == PageSpace.A ? segment.AStart : segment.BStart)!.Value;
                using var roi = new Mat(source, new Rect(0, y, source.Width, segment.Length));
                using var white = new Mat(roi.Size(), roi.Type(), Scalar.All(255));
                var pureWhite = Cv2.Norm(roi, white, NormTypes.INF) == 0;
                if (!pureWhite) removedSides.Add(side);
                if (!retainWhite || !pureWhite) continue;
                kind = "white_space";
            }
            pieces.Add(new(commonY, segment.CanvasStart, segment.Length, segment.AStart, segment.BStart, kind));
            commonY += segment.Length;
        }
        if (removedSides.Count > 1) throw new InvalidOperationException("unanchored_join");
        if (nextA != a.Height || nextB != b.Height) throw new ArgumentException("元画像に未処理の行があります。");
        if (!pieces.Any(p => p.kind == "paired")) throw new InvalidOperationException("no_common_bands");
        var map = new PageMap(a.Size(), b.Size(), new(a.Width, commonY),
            pieces.Select(p => new PageSegment(p.common_y, p.length, p.a_y, p.b_y)));
        return new(map, pieces);
    }

    internal static void Run(string folder)
    {
        using var jobs = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "jobs.json")));
        var outcomes = new List<object>(); var evidenceNames = new HashSet<string>();
        foreach (var job in jobs.RootElement.EnumerateArray())
        {
            var name = job.GetProperty("name").GetString()!; var id = job.GetProperty("id").GetString()!;
            using var a = Read(name + "-a.png"); using var b = Read(name + "-b.png");
            var aHash = Hash(a); var bHash = Hash(b);
            var bands = job.GetProperty("bands").EnumerateArray().Select(s => new PageSegment(s[0].GetInt32(), s[1].GetInt32(),
                s[2].ValueKind == JsonValueKind.Null ? null : s[2].GetInt32(), s[3].ValueKind == JsonValueKind.Null ? null : s[3].GetInt32())).ToArray();
            var display = new PageMap(a.Size(), b.Size(), new(a.Width, bands.Sum(s => s.Length)), bands);
            var surface = Build(a, b, display, job.GetProperty("retain_white").GetBoolean());
            using var ca = surface.Map.Render(a, PageSpace.A); using var cb = surface.Map.Render(b, PageSpace.B);
            var swappedDisplay = new PageMap(b.Size(), a.Size(), display.CanvasSize,
                bands.Select(s => new PageSegment(s.CanvasStart, s.Length, s.BStart, s.AStart)));
            var swapped = Build(b, a, swappedDisplay, job.GetProperty("retain_white").GetBoolean());
            using var swappedA = swapped.Map.Render(b, PageSpace.A); using var swappedB = swapped.Map.Render(a, PageSpace.B);
            if (Cv2.Norm(ca, swappedB, NormTypes.INF) != 0 || Cv2.Norm(cb, swappedA, NormTypes.INF) != 0
                || !surface.Pieces.Select(p => p with { a_y = p.b_y, b_y = p.a_y }).SequenceEqual(swapped.Pieces))
                throw new InvalidOperationException("A/B 交換で比較面の入力・写像が対応しません。");
            var reverse = job.GetProperty("reverse").GetBoolean();
            using var result = PageComparer.Compare(reverse ? cb : ca, reverse ? ca : cb, Parameters(job.GetProperty("params")));
            using var displayRaw = new Mat(display.CanvasSize, MatType.CV_8UC1, Scalar.All(0));
            foreach (var piece in surface.Pieces)
            {
                using var from = new Mat(result.RawMask, new Rect(0, piece.common_y, a.Width, piece.length));
                using var to = new Mat(displayRaw, new Rect(0, piece.display_y, a.Width, piece.length));
                from.CopyTo(to);
            }
            if (Cv2.CountNonZero(displayRaw) != result.RawPixels) throw new InvalidOperationException("差分の転写で画素数が変わりました。");
            Save(id + "-csharp-raw.png", result.RawMask); Save(id + "-csharp-display-raw.png", displayRaw);
            if (aHash != Hash(a) || bHash != Hash(b)) throw new InvalidOperationException("元画像を変更しました。");
            if (evidenceNames.Add(name))
            {
                using var evidence = RawOverlay.Create(a, b);
                Save(name + "-raw-evidence.png", evidence);
            }
            outcomes.Add(new { id, candidate = WhiteBandProbe.Snapshot(result), pieces = surface.Pieces,
                source_a_sha256 = aHash, source_b_sha256 = bHash, originals_unchanged = true, swap_map_equal = true });
            Console.WriteLine($"{id}: {result.RawPixels}px/{result.Clusters.Count}件");
        }
        // 同じ位置の置換を、片側帯の相互削除で隠す写像を拒否する。
        using (var a = new Mat(120, 80, MatType.CV_8UC3, Scalar.All(255)))
        using (var b = a.Clone())
        {
            Cv2.Rectangle(a, new Rect(20, 40, 10, 40), Scalar.All(0), -1);
            Cv2.Rectangle(b, new Rect(50, 40, 10, 40), Scalar.All(0), -1);
            var map = new PageMap(a.Size(), b.Size(), new(80, 160), [new(0, 40, 0, 0), new(40, 40, 40, null), new(80, 40, null, 40), new(120, 40, 80, 80)]);
            foreach (var retain in new[] { false, true })
            {
                var rejected = false;
                try { _ = Build(a, b, map, retain); }
                catch (InvalidOperationException e) when (e.Message == "unanchored_join") { rejected = true; }
                if (!rejected) throw new InvalidOperationException("内容置換の隠蔽を拒否しませんでした。");
            }
        }
        File.WriteAllText(Path.Combine(folder, "csharp.json"), JsonSerializer.Serialize(outcomes, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Mat Read(string name) => Cv2.ImDecode(File.ReadAllBytes(Path.Combine(folder, name)), ImreadModes.Color);
        void Save(string name, Mat image) => File.WriteAllBytes(Path.Combine(folder, name), image.ImEncode(".png"));
    }

    private static string Hash(Mat image) => Convert.ToHexStringLower(SHA256.HashData(image.ImEncode(".png")));

    private static ComparisonParameters Parameters(JsonElement j)
    {
        double V(string key) => j.GetProperty(key).GetDouble();
        return new()
        {
            Dpi = (int)V("dpi"),
            Diff = new() { MaxShiftMm = V("max_shift_mm"), ColorThreshold = V("color_threshold"), EdgeTolerance = V("edge_tolerance") },
            Ink = new() { BackgroundRadiusMm = V("ink_background_radius_mm"), ContrastThreshold = V("ink_contrast_threshold") },
            Cluster = new() { MergeXMm = V("merge_x_mm"), MergeYMm = V("merge_y_mm"), MinPixels = (int)V("min_pixels"),
                MaxDiffRatio = V("max_diff_ratio"), ReadingBandMm = V("reading_band_mm") },
            Exclude = j.GetProperty("exclude_mm").EnumerateArray().Select(r => new RectMm(r[0].GetDouble(), r[1].GetDouble(), r[2].GetDouble(), r[3].GetDouble())).ToArray()
        };
    }
}
