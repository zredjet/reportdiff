using System.Runtime.InteropServices;
using OpenCvSharp;
using ReportDiff.Core;

// 検討用の逐次実装。元画像の特徴量を写す以外は SPEC 5.2 / 5.3 の式・探索順を維持する。
// 製品への入口は追加しない。恒等写像で製品コアと照合する。
internal static class MappedFeatures
{
    internal sealed record Features(float[] Values, float[] Contrast, byte[] Ink, int Width, int Height);
    internal sealed record GroupTrace(int Id, int Before, int After, int Dx, int Dy);
    internal sealed class Trace
    {
        public byte[] Initial { get; set; } = [];
        public List<GroupTrace> Groups { get; } = [];
    }

    private static Features Create(Mat image, ComparisonParameters p)
    {
        using var normalized = new Mat(); using var lab = new Mat();
        image.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
        Cv2.CvtColor(normalized, lab, ColorConversionCodes.BGR2Lab);
        using var values = new Mat(); using var contrast = new Mat(image.Size(), MatType.CV_32FC3, Scalar.All(0));
        if (p.Diff.EdgeTolerance == 0) lab.CopyTo(values);
        else
        {
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            using var minimum = new Mat();
            Cv2.Blur(lab, values, new Size(3, 3));
            Cv2.Dilate(lab, contrast, kernel); Cv2.Erode(lab, minimum, kernel);
            Cv2.Subtract(contrast, minimum, contrast);
        }
        var radius = Math.Max(1, Units.RoundPixels(p.Ink.BackgroundRadiusMm, p.Dpi));
        using var inkKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * radius + 1, 2 * radius + 1));
        using var lightness = new Mat(); using var background = new Mat(); using var ink = new Mat();
        Cv2.ExtractChannel(lab, lightness, 0); Cv2.Dilate(lightness, background, inkKernel);
        Cv2.Subtract(background, lightness, background); Cv2.Compare(background, p.Ink.ContrastThreshold, ink, CmpTypes.GT);
        return new(MemoryMarshal.Cast<Vec3f, float>(values.AsSpan<Vec3f>()).ToArray(),
            MemoryMarshal.Cast<Vec3f, float>(contrast.AsSpan<Vec3f>()).ToArray(), ink.AsSpan<byte>().ToArray(), image.Width, image.Height);
    }

    private static Features Map(Features source, PageMap map, PageSpace side)
    {
        if (map.Dx != 0 || map.CanvasSize.Width != source.Width) throw new ArgumentException("試行は横ずれなし・同一幅に限ります。");
        var count = checked(map.CanvasSize.Width * map.CanvasSize.Height);
        var values = new float[count * 3]; var contrast = new float[count * 3]; var ink = new byte[count];
        for (var pixel = 0; pixel < count; pixel++) values[pixel * 3] = 100;
        var next = 0;
        foreach (var band in map.Segments)
        {
            var y = side == PageSpace.A ? band.AStart : band.BStart;
            if (y is not int start) continue;
            if (start != next || start + band.Length > source.Height) throw new ArgumentException("元画像は全行を順序どおり一度だけ転写します。");
            var length = band.Length * source.Width; var from = start * source.Width; var to = band.CanvasStart * source.Width;
            Array.Copy(source.Values, from * 3, values, to * 3, length * 3);
            Array.Copy(source.Contrast, from * 3, contrast, to * 3, length * 3);
            Array.Copy(source.Ink, from, ink, to, length);
            next += band.Length;
        }
        if (next != source.Height) throw new ArgumentException("元画像の未転写行があります。");
        return new(values, contrast, ink, source.Width, map.CanvasSize.Height);
    }

    internal static RawDifference Calculate(Mat sourceA, Mat sourceB, ComparisonParameters p, PageMap map, bool reverse, Trace? trace = null)
    {
        var a = Map(Create(sourceA, p), map, PageSpace.A); var b = Map(Create(sourceB, p), map, PageSpace.B);
        if (reverse) (a, b) = (b, a);
        var first = Candidates(a, b, p.Diff, 0, 0);
        if (trace is not null) trace.Initial = first.ToArray();
        var s = Units.RoundPixels(p.Diff.MaxShiftMm, p.Dpi);
        if (s <= 0 || !first.Contains((byte)255)) return new(Mask(first, a.Width, a.Height), 0, 0);
        using var inkA = Mask(a.Ink, a.Width, a.Height); using var inkB = Mask(b.Ink, a.Width, a.Height);
        using var ink = new Mat(); using var inkLabelsMat = new Mat();
        Cv2.BitwiseOr(inkA, inkB, ink);
        var inkCount = Cv2.ConnectedComponents(ink, inkLabelsMat, PixelConnectivity.Connectivity8);
        var inkLabels = inkLabelsMat.AsSpan<int>(); var touched = new bool[inkCount];
        for (var i = 0; i < first.Length; i++) if (first[i] != 0) touched[inkLabels[i]] = true;
        touched[0] = false;
        using var firstMask = Mask(first, a.Width, a.Height); using var region = new Mat();
        using var near = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(firstMask, region, near);
        var regionData = region.AsSpan<byte>();
        for (var i = 0; i < first.Length; i++) if (touched[inkLabels[i]]) regionData[i] = 255;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * s + 1, 2 * s + 1));
        using var expanded = new Mat(); using var labelsMat = new Mat();
        Cv2.Dilate(region, expanded, kernel);
        var count = Cv2.ConnectedComponents(expanded, labelsMat, PixelConnectivity.Connectivity8);
        var labels = labelsMat.AsSpan<int>();
        var shifts = (from dx in Enumerable.Range(-s, 2 * s + 1) from dy in Enumerable.Range(-s, 2 * s + 1)
                      orderby Math.Abs(dx) + Math.Abs(dy), dx, dy select (dx, dy)).ToArray();
        var bestCounts = Enumerable.Repeat(int.MaxValue, count).ToArray(); var firstCounts = new int[count];
        var bestShifts = new (int dx, int dy)[count]; var raw = new byte[first.Length];
        foreach (var (dx, dy) in shifts)
        {
            var mask = dx == 0 && dy == 0 ? first : Candidates(a, b, p.Diff, dx, dy);
            var counts = new int[count];
            for (var i = 0; i < mask.Length; i++) if (mask[i] != 0) counts[labels[i]]++;
            if (dx == 0 && dy == 0) firstCounts = counts.ToArray();
            var changed = new bool[count];
            for (var g = 1; g < count; g++)
                if (counts[g] < bestCounts[g]) { bestCounts[g] = counts[g]; bestShifts[g] = (dx, dy); changed[g] = true; }
            for (var i = 0; i < raw.Length; i++) if (changed[labels[i]]) raw[i] = mask[i];
        }
        var absorbed = 0; var maxShift = 0;
        for (var g = 1; g < count; g++)
        {
            if (firstCounts[g] == 0) continue;
            var (dx, dy) = bestShifts[g];
            trace?.Groups.Add(new(g, firstCounts[g], bestCounts[g], dx, dy));
            if (bestCounts[g] != 0) continue;
            absorbed++; maxShift = Math.Max(maxShift, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        }
        for (var i = 0; i < raw.Length; i++) if (labels[i] == 0 || firstCounts[labels[i]] == 0) raw[i] = 0;
        return new(Mask(raw, a.Width, a.Height), absorbed, maxShift);
    }

    private static byte[] Candidates(Features a, Features b, DiffOptions p, int dx, int dy)
    {
        var result = new byte[a.Width * a.Height];
        var threshold = (float)p.ColorThreshold; var tolerance = (float)p.EdgeTolerance;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            var ai = (y * a.Width + x) * 3;
            var bi = (Math.Clamp(y - dy, 0, a.Height - 1) * a.Width + Math.Clamp(x - dx, 0, a.Width - 1)) * 3;
            for (var c = 0; c < 3; c++)
                if (MathF.Abs(a.Values[ai + c] - b.Values[bi + c]) > threshold + tolerance * MathF.Max(a.Contrast[ai + c], b.Contrast[bi + c]))
                { result[ai / 3] = 255; break; }
        }
        return result;
    }

    private static Mat Mask(byte[] data, int width, int height)
    {
        var result = new Mat(height, width, MatType.CV_8UC1);
        try { data.CopyTo(result.AsSpan<byte>()); return result; }
        catch { result.Dispose(); throw; }
    }
}
