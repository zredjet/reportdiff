using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>RawMask の所有権を持つ。呼び出し側で破棄する。</summary>
public sealed class RawDifference(Mat rawMask, int absorbedGroups, int maxShiftPx) : IDisposable
{
    public Mat RawMask { get; } = rawMask;
    public int AbsorbedGroups { get; } = absorbedGroups;
    public int MaxShiftPx { get; } = maxShiftPx;
    public void Dispose() => RawMask.Dispose();
}

public static class TolerantDifference
{
    public static RawDifference Calculate(Mat a, Mat b, ComparisonParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (a.Empty() || b.Empty() || a.Type() != MatType.CV_8UC3 || b.Type() != MatType.CV_8UC3 || a.Size() != b.Size())
            throw new ArgumentException("比較画像は同じ大きさの空でない BGR 8bit 画像にしてください。");
        var width = a.Cols;
        var height = a.Rows;
        using (var difference = new Mat())
        {
            Cv2.Absdiff(a, b, difference);
            using var channels = difference.Reshape(1);
            if (Cv2.CountNonZero(channels) == 0)
                return new(new Mat(height, width, MatType.CV_8UC1, Scalar.All(0)), 0, 0);
        }

        using var labA = ToLab(a);
        using var labB = ToLab(b);
        var featuresA = Features.Create(labA, parameters.Diff.EdgeTolerance);
        var featuresB = Features.Create(labB, parameters.Diff.EdgeTolerance);
        var candidates = Candidates(featuresA, featuresB, width, height, parameters.Diff, 0, 0);
        var shift = Units.RoundPixels(parameters.Diff.MaxShiftMm, parameters.Dpi);
        if (shift <= 0 || !candidates.Contains((byte)255))
            return new(MatBuffers.Mask(candidates, width, height), 0, 0);

        var labels = Groups(labA, labB, candidates, parameters.Dpi, shift, out var count);
        var shifts = (from dx in Enumerable.Range(-shift, checked(2 * shift + 1))
                      from dy in Enumerable.Range(-shift, checked(2 * shift + 1))
                      orderby Math.Abs(dx) + Math.Abs(dy), dx, dy
                      select (dx, dy)).ToArray();
        var masks = new List<byte[]>();
        var bestCounts = Enumerable.Repeat(int.MaxValue, count).ToArray();
        var initialCounts = new int[count];
        var best = new int[count];
        // T1-2 は参照実装と同じく全ページ・全ずれを評価する。
        for (var i = 0; i < shifts.Length; i++)
        {
            var (dx, dy) = shifts[i];
            var mask = i == 0 ? candidates : Candidates(featuresA, featuresB, width, height, parameters.Diff, dx, dy);
            masks.Add(mask);
            var counts = new int[count];
            for (var pixel = 0; pixel < mask.Length; pixel++)
                if (mask[pixel] != 0) counts[labels[pixel]]++;
            if (i == 0) initialCounts = counts;
            for (var group = 1; group < count; group++)
                if (counts[group] < bestCounts[group])
                {
                    bestCounts[group] = counts[group];
                    best[group] = i;
                }
        }
        var absorbed = 0;
        var maxShift = 0;
        for (var group = 1; group < count; group++)
        {
            if (initialCounts[group] == 0 || bestCounts[group] != 0) continue;
            absorbed++;
            var (dx, dy) = shifts[best[group]];
            maxShift = Math.Max(maxShift, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        }
        var raw = new byte[candidates.Length];
        for (var pixel = 0; pixel < raw.Length; pixel++)
        {
            var group = labels[pixel];
            if (group > 0 && initialCounts[group] > 0 && bestCounts[group] > 0)
                raw[pixel] = masks[best[group]][pixel];
        }
        return new(MatBuffers.Mask(raw, width, height), absorbed, maxShift);
    }

    private static Mat ToLab(Mat image)
    {
        using var normalized = new Mat();
        image.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
        var lab = new Mat();
        try { Cv2.CvtColor(normalized, lab, ColorConversionCodes.BGR2Lab); return lab; }
        catch { lab.Dispose(); throw; }
    }

    private sealed record Features(float[] Values, float[] Contrast)
    {
        public static Features Create(Mat lab, double tolerance)
        {
            if (tolerance == 0) return new(MatBuffers.Floats(lab), []);
            using var blur = new Mat();
            using var maximum = new Mat();
            using var minimum = new Mat();
            using var contrast = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.Blur(lab, blur, new Size(3, 3));
            Cv2.Dilate(lab, maximum, kernel);
            Cv2.Erode(lab, minimum, kernel);
            Cv2.Subtract(maximum, minimum, contrast);
            return new(MatBuffers.Floats(blur), MatBuffers.Floats(contrast));
        }
    }

    private static byte[] Candidates(Features a, Features b, int width, int height, DiffOptions options, int dx, int dy)
    {
        var mask = new byte[width * height];
        var threshold = (float)options.ColorThreshold;
        var tolerance = (float)options.EdgeTolerance;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = Math.Clamp(y - dy, 0, height - 1) * width;
            for (var x = 0; x < width; x++)
            {
                var pixel = y * width + x;
                var source = sourceRow + Math.Clamp(x - dx, 0, width - 1);
                for (var channel = 0; channel < 3; channel++)
                {
                    var ai = pixel * 3 + channel;
                    var bi = source * 3 + channel;
                    var limit = tolerance == 0 ? threshold : threshold + tolerance * MathF.Max(a.Contrast[ai], b.Contrast[bi]);
                    if (MathF.Abs(a.Values[ai] - b.Values[bi]) > limit)
                    {
                        mask[pixel] = 255;
                        break;
                    }
                }
            }
        }
        return mask;
    }

    private static int[] Groups(Mat labA, Mat labB, byte[] candidates, int dpi, int shift, out int count)
    {
        using var inkA = Ink(labA, dpi);
        using var inkB = Ink(labB, dpi);
        using var ink = new Mat();
        Cv2.BitwiseOr(inkA, inkB, ink);
        using var inkLabels = new Mat();
        var inkCount = Cv2.ConnectedComponents(ink, inkLabels, PixelConnectivity.Connectivity8);
        var inkData = MatBuffers.Integers(inkLabels);
        var touched = new bool[inkCount];
        for (var i = 0; i < candidates.Length; i++)
            if (candidates[i] != 0) touched[inkData[i]] = true;
        touched[0] = false;
        using var original = MatBuffers.Mask(candidates, labA.Cols, labA.Rows);
        using var region = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(original, region, kernel);
        var regionData = MatBuffers.Bytes(region);
        for (var i = 0; i < regionData.Length; i++)
            if (touched[inkData[i]]) regionData[i] = 255;
        using var united = MatBuffers.Mask(regionData, labA.Cols, labA.Rows);
        using var dilated = new Mat();
        using var expansion = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(checked(2 * shift + 1), checked(2 * shift + 1)));
        Cv2.Dilate(united, dilated, expansion);
        using var groupLabels = new Mat();
        count = Cv2.ConnectedComponents(dilated, groupLabels, PixelConnectivity.Connectivity8);
        return MatBuffers.Integers(groupLabels);
    }

    private static Mat Ink(Mat lab, int dpi)
    {
        var radius = Math.Max(1, Units.RoundPixels(1.5, dpi));
        using var lightness = new Mat();
        using var background = new Mat();
        using var difference = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * radius + 1, 2 * radius + 1));
        Cv2.ExtractChannel(lab, lightness, 0);
        Cv2.Dilate(lightness, background, kernel);
        Cv2.Subtract(background, lightness, difference);
        var mask = new Mat();
        try { Cv2.Compare(difference, 25, mask, CmpTypes.GT); return mask; }
        catch { mask.Dispose(); throw; }
    }
}
