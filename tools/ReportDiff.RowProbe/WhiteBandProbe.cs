using System.Text.Json;
using OpenCvSharp;
using ReportDiff.Core;

internal static class WhiteBandProbe
{
    internal static void Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var outcomes = new List<object>();
        var losses = 0;
        const int width = 240, height = 400, cut = 120, gap = 60;
        foreach (var profile in new[] { "normal", "strict", "loose" })
        foreach (var distance in new[] { 0, 1, 2, 4, 8 })
        foreach (var reverse in new[] { false, true })
        {
            var name = $"tone-{profile}-distance-{distance}-{(reverse ? "reverse" : "forward")}";
            var options = Profile(profile);
            using var sourceA = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));
            Cv2.Rectangle(sourceA, new Rect(40, 40, 6, 260), Scalar.All(0), -1);
            using var sourceB = sourceA.Clone();
            Cv2.Rectangle(sourceB, new Rect(40, cut + distance, 6, 2), Scalar.All(64), -1);
            using var baseline = PageComparer.Compare(reverse ? sourceB : sourceA, reverse ? sourceA : sourceB, options);
            using var insertedB = Insert(sourceB, cut, gap);
            Cv2.Rectangle(insertedB, new Rect(40, cut, 6, gap), Scalar.All(0), -1);
            Cv2.Rectangle(insertedB, new Rect(110, cut + 20, 35, 12), Scalar.All(0), -1);
            var map = Map(sourceA.Size(), cut, gap);
            using var displayA = map.Render(sourceA, PageSpace.A);
            using var displayB = map.Render(insertedB, PageSpace.B);
            var bands = new[] { new Rect(0, cut, width, gap), new Rect(0, height, width, gap) };
            var comparedOptions = options with { Exclude = bands.Select(r => ProbeMasks.Band(r.Y, r.Width, r.Height, displayA.Size())).ToArray() };
            using var old = PageComparer.Compare(reverse ? displayB : displayA, reverse ? displayA : displayB, comparedOptions);
            using var preparedA = WhiteBands(displayA, bands);
            using var preparedB = WhiteBands(displayB, bands);
            using var actual = PageComparer.Compare(reverse ? preparedB : preparedA, reverse ? preparedA : preparedB, comparedOptions);
            var lost = baseline.Clusters.Count > 0 && actual.Clusters.Count == 0;
            if (lost) losses++;
            outcomes.Add(new { name, profile, distance, reverse, lost,
                baseline = Snapshot(baseline), previous = Snapshot(old), candidate = Snapshot(actual),
                target_source = new { x = 40, y = cut + distance, w = 6, h = 2 },
                target_canvas = new { x = 40, y = cut + gap + distance, w = 6, h = 2 } });
            if (distance == 0)
            {
                Save(name + "-source-a.png", sourceA); Save(name + "-source-b.png", sourceB);
                Save(name + "-display-a.png", displayA); Save(name + "-display-b.png", displayB);
                Save(name + "-prepared-a.png", preparedA); Save(name + "-prepared-b.png", preparedB);
                Save(name + "-baseline-raw.png", baseline.RawMask); Save(name + "-previous-raw.png", old.RawMask); Save(name + "-candidate-raw.png", actual.RawMask);
            }
            Console.WriteLine($"{name}: 基準={baseline.RawPixels}px/{baseline.Clusters.Count}件 候補={actual.RawPixels}px/{actual.Clusters.Count}件 検出消失={lost}");
        }
        File.WriteAllText(Path.Combine(folder, "boundary-tone.json"), JsonSerializer.Serialize(new { losses, cases = outcomes }, new JsonSerializerOptions { WriteIndented = true }));
        void Save(string name, Mat image) => File.WriteAllBytes(Path.Combine(folder, name), image.ImEncode(".png"));
    }

    internal static ComparisonParameters Profile(string profile) => new()
    {
        Diff = profile switch
        {
            "normal" => new(),
            "strict" => new() { MaxShiftMm = 0, EdgeTolerance = 0 },
            "loose" => new() { MaxShiftMm = 0.30 },
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        }
    };

    internal static object Snapshot(PageComparison result) => new
    {
        result.Status, result.RawPixels, result.NoiseDropped, result.AbsorbedGroups, result.MaxShiftPx,
        clusters = result.Clusters.Select(c => new { x = c.Bounds.X, y = c.Bounds.Y, w = c.Bounds.Width,
            h = c.Bounds.Height, c.Pixels, c.Kind }).ToArray()
    };

    internal static PageMap Map(Size sourceSize, int cut, int gap) => new(sourceSize, sourceSize,
        new(sourceSize.Width, sourceSize.Height + gap),
        [new(0, cut, 0, 0), new(cut, gap, null, cut), new(cut + gap, sourceSize.Height - cut - gap, cut, cut + gap),
         new(sourceSize.Height, gap, sourceSize.Height - gap, null)]);

    internal static Mat Insert(Mat source, int cut, int gap)
    {
        using var tail = new Mat(source, new Rect(0, source.Height - gap, source.Width, gap));
        using var white = new Mat(tail.Size(), tail.Type(), Scalar.All(255));
        if (Cv2.Norm(tail, white, NormTypes.INF) != 0)
            throw new InvalidOperationException("挿入のために捨てるページ末尾は純白に限ります。");
        var output = new Mat(source.Size(), source.Type(), Scalar.All(255));
        try
        {
            using var upper = new Mat(source, new Rect(0, 0, source.Width, cut));
            using var upperTarget = new Mat(output, new Rect(0, 0, source.Width, cut)); upper.CopyTo(upperTarget);
            using var lower = new Mat(source, new Rect(0, cut, source.Width, source.Height - cut - gap));
            using var lowerTarget = new Mat(output, new Rect(0, cut + gap, source.Width, lower.Height)); lower.CopyTo(lowerTarget);
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    internal static Mat WhiteBands(Mat image, IReadOnlyList<Rect> bands)
    {
        var prepared = image.Clone();
        try
        {
            foreach (var band in bands)
            {
                using var roi = new Mat(prepared, band); roi.SetTo(Scalar.All(255));
            }
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }
}
