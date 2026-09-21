using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>採用した連結成分に注釈を付ける。検出マスク・クラスタの形は変更しない。</summary>
internal static class DifferenceClassifier
{
    public static string?[] Classify(Mat a, Mat b, ComparisonParameters parameters, byte[] raw,
        int[] labels, bool[] keep, Rect region, out Mat? removalMask, ComparisonInk? prepared = null, RegionMap? regions = null)
    {
        var width = a.Width;
        var height = a.Height;
        var (preparedA, preparedB) = prepared?.ReadFor(a, b, parameters) ?? (null, null);
        var (originalA, inkA) = ReadInk(a, region, parameters, preparedA, regions is null ? null : true);
        var (originalB, inkB) = ReadInk(b, region, parameters, preparedB, regions is null ? null : true);
        var kinds = new string?[keep.Length];
        // 各種の有無だけで分類できるため、割合や多数決のしきい値を持たない。
        var states = new byte[keep.Length];
        var shapeMismatch = new bool[keep.Length];
        var exclusions = parameters.Exclude.Select(e => PageMap.CanvasRectangle(e, parameters.Dpi, new(width, height))).ToArray();
        byte[]? removed = null;
        for (var y = region.Top; y < region.Bottom; y++)
        for (var x = region.Left; x < region.Right; x++)
        {
            var pixel = y * width + x;
            var label = labels[pixel];
            if (!keep[label]) continue;
            var local = (y - region.Top) * region.Width + x - region.Left;
            if (originalA[local] != originalB[local] && !IsExcluded(exclusions, x, y))
                shapeMismatch[label] = true;
            if (raw[pixel] == 0) continue;
            var strict = regions is not null && regions.DiffAt(pixel).EdgeTolerance == 0;
            var hasA = (strict ? originalA : inkA)[local] != 0;
            var hasB = (strict ? originalB : inkB)[local] != 0;
            var state = hasA ? (hasB ? 4 : 1) : (hasB ? 2 : 8);
            states[label] |= (byte)state;
            if (state == 1)
            {
                removed ??= new byte[raw.Length];
                removed[pixel] = 255;
            }
        }
        for (var label = 1; label < keep.Length; label++)
            if (keep[label]) kinds[label] = states[label] switch
            {
                1 => "removed",
                2 => "added",
                4 when !shapeMismatch[label] => "color_changed",
                _ => "changed"
            };
        removalMask = removed is null ? null : MatBuffers.Mask(removed, width, height);
        return kinds;
    }

    private static bool IsExcluded(Rect[] exclusions, int x, int y)
    {
        foreach (var exclusion in exclusions) if (exclusion.Contains(x, y)) return true;
        return false;
    }

    private static (byte[] Original, byte[] Ink) ReadInk(Mat image, Rect region, ComparisonParameters parameters, Mat? prepared, bool? expand = null)
    {
        var edge = expand ?? parameters.Diff.EdgeTolerance > 0;
        // 生成済みインクには局所背景の余白は不要。分類用の 1px 膨張の余白だけを残す。
        // 未生成の場合は従来どおり背景半径も含め、ROI の端に偽の背景を作らない。
        var margin = (prepared is null ? Math.Max(1, Units.RoundPixels(parameters.Ink.BackgroundRadiusMm, parameters.Dpi)) : 0)
            + (edge ? 1 : 0);
        var left = Math.Max(0, region.Left - margin);
        var top = Math.Max(0, region.Top - margin);
        var right = Math.Min(image.Width, region.Right + margin);
        var bottom = Math.Min(image.Height, region.Bottom + margin);
        var bounds = new Rect(left, top, right - left, bottom - top);
        using var ink = prepared is null ? CreateInk(image, bounds, parameters) : new Mat(prepared, bounds);
        var local = new Rect(region.Left - left, region.Top - top, region.Width, region.Height);
        using var original = new Mat(ink, local);
        var data = MatBuffers.Bytes(original);
        if (!edge) return (data, data);
        using var expanded = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(ink, expanded, kernel);
        using var matched = new Mat(expanded, local);
        return (data, MatBuffers.Bytes(matched));
    }

    private static Mat CreateInk(Mat image, Rect bounds, ComparisonParameters parameters)
    {
        using var source = new Mat(image, bounds);
        using var lab = ImageInk.ToLab(source);
        return ImageInk.FromLab(lab, parameters.Dpi, parameters.Ink);
    }
}
