using OpenCvSharp;
using System.Diagnostics;

namespace ReportDiff.Core;

public static class PageComparer
{
    public static PageComparison Compare(Mat a, Mat b, ComparisonParameters parameters) => Compare(a, b, parameters, true);

    internal static PageComparison Compare(Mat a, Mat b, ComparisonParameters parameters,
        bool useGroupBounds, ComparisonTimings? timings = null, bool classify = true)
    {
        using var ink = classify ? new ComparisonInk() : null;
        using var raw = TolerantDifference.Calculate(a, b, parameters, useGroupBounds, timings, ink);
        var started = Stopwatch.GetTimestamp();
        var result = Cluster(raw, parameters, classify ? a : null, classify ? b : null, timings, ink);
        if (timings is not null) timings.ClusteringMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - timings.ClassificationMs - timings.MovementMs;
        return result;
    }

    internal static PageComparison Cluster(RawDifference difference, ComparisonParameters parameters,
        Mat? a = null, Mat? b = null, ComparisonTimings? timings = null, ComparisonInk? classificationInk = null)
    {
        var width = difference.RawMask.Cols;
        var height = difference.RawMask.Rows;
        var raw = MatBuffers.Bytes(difference.RawMask);
        foreach (var e in parameters.Exclude)
        {
            var x0 = (int)Math.Clamp(Math.Floor(Units.MmToPixels(e.X, parameters.Dpi)), 0, width);
            var y0 = (int)Math.Clamp(Math.Floor(Units.MmToPixels(e.Y, parameters.Dpi)), 0, height);
            var x1 = (int)Math.Clamp(Math.Ceiling(Units.MmToPixels(e.X + e.W, parameters.Dpi)), 0, width);
            var y1 = (int)Math.Clamp(Math.Ceiling(Units.MmToPixels(e.Y + e.H, parameters.Dpi)), 0, height);
            for (var y = y0; y < y1; y++)
                if (x1 > x0) Array.Clear(raw, y * width + x0, x1 - x0);
        }
        var rawPixels = raw.Count(value => value != 0);
        var labelMask = new byte[raw.Length];
        PageComparison Result(string status, IReadOnlyList<DifferenceCluster> clusters, int dropped, params string[] warnings) =>
            new(status, clusters, rawPixels, dropped, difference.AbsorbedGroups, difference.MaxShiftPx,
                MatBuffers.Mask(raw, width, height), MatBuffers.Mask(labelMask, width, height), warnings);
        if (rawPixels == 0) return Result("same", [], 0);
        if (rawPixels / (double)raw.Length > parameters.Cluster.MaxDiffRatio)
            return Result("too_different", [], 0, "TOO_DIFFERENT");

        var hx = checked((int)Math.Ceiling(Units.MmToPixels(parameters.Cluster.MergeXMm, parameters.Dpi) / 2));
        var hy = checked((int)Math.Ceiling(Units.MmToPixels(parameters.Cluster.MergeYMm, parameters.Dpi) / 2));
        using var rawMat = MatBuffers.Mask(raw, width, height);
        using var merged = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(checked(2 * hx + 1), checked(2 * hy + 1)));
        Cv2.Dilate(rawMat, merged, kernel);
        using var labelMat = new Mat();
        var count = Cv2.ConnectedComponents(merged, labelMat, PixelConnectivity.Connectivity8);
        var labels = MatBuffers.Integers(labelMat);
        var counts = new int[count];
        var xMin = Enumerable.Repeat(width, count).ToArray();
        var yMin = Enumerable.Repeat(height, count).ToArray();
        var xMax = new int[count];
        var yMax = new int[count];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            if (raw[i] == 0) continue;
            var label = labels[i];
            counts[label]++;
            xMin[label] = Math.Min(xMin[label], x); yMin[label] = Math.Min(yMin[label], y);
            xMax[label] = Math.Max(xMax[label], x); yMax[label] = Math.Max(yMax[label], y);
        }
        var dropped = 0;
        var accepted = new List<(int Label, DifferenceCluster Cluster)>();
        for (var label = 1; label < count; label++)
        {
            if (counts[label] < parameters.Cluster.MinPixels) { dropped++; continue; }
            var bounds = new Rect(xMin[label], yMin[label], xMax[label] - xMin[label] + 1, yMax[label] - yMin[label] + 1);
            accepted.Add((label, new(0, bounds, counts[label])));
        }
        var band = Math.Max(1, Units.RoundPixels(parameters.Cluster.ReadingBandMm, parameters.Dpi));
        accepted = accepted.OrderBy(item => item.Cluster.Bounds.Y / band).ThenBy(item => item.Cluster.Bounds.X).ToList();
        var limited = accepted.Count > parameters.Cluster.MaxClustersPerPage;
        if (limited)
            accepted = accepted.OrderByDescending(item => item.Cluster.Pixels).Take(parameters.Cluster.MaxClustersPerPage)
                .OrderBy(item => item.Cluster.Bounds.Y / band).ThenBy(item => item.Cluster.Bounds.X).ToList();
        var keep = new bool[count];
        foreach (var item in accepted) keep[item.Label] = true;
        for (var i = 0; i < labels.Length; i++) if (keep[labels[i]]) labelMask[i] = 255;
        Mat? removalMask = null;
        string?[]? kinds = null;
        if (accepted.Count > 0 && a is not null && b is not null)
        {
            var started = Stopwatch.GetTimestamp();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            // 形状確認には膨張した連結成分全体を含める。外接矩形内の別成分は混ぜない。
            var left = Math.Max(0, accepted.Min(item => item.Cluster.Bounds.Left) - hx);
            var top = Math.Max(0, accepted.Min(item => item.Cluster.Bounds.Top) - hy);
            var right = Math.Min(width, accepted.Max(item => item.Cluster.Bounds.Right) + hx);
            var bottom = Math.Min(height, accepted.Max(item => item.Cluster.Bounds.Bottom) + hy);
            kinds = DifferenceClassifier.Classify(a, b, parameters, raw, labels, keep,
                new Rect(left, top, right - left, bottom - top), out removalMask, classificationInk);
            if (timings is not null)
            {
                timings.ClassificationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                timings.ClassificationManagedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                timings.RetainedRemovalMaskBytes = removalMask is null ? 0 : (long)width * height;
            }
        }
        try
        {
            var clusters = accepted.Select((item, index) => item.Cluster with
                { Id = index + 1, Kind = kinds?[item.Label] }).ToArray();
            if (a is not null && b is not null && clusters.Length > 0 && parameters.Move.SearchMm > 0)
            {
                var started = Stopwatch.GetTimestamp();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                clusters = MovementAnnotator.Annotate(a, b, parameters, raw, labels,
                    accepted.Select(item => item.Label).ToArray(), clusters);
                if (timings is not null)
                {
                    timings.MovementMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    timings.MovementManagedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                }
            }
            return new(clusters.Length == 0 ? "same" : "different", clusters, rawPixels, dropped,
                difference.AbsorbedGroups, difference.MaxShiftPx, MatBuffers.Mask(raw, width, height),
                MatBuffers.Mask(labelMask, width, height), limited ? ["CLUSTER_LIMIT"] : [], removalMask);
        }
        catch { removalMask?.Dispose(); throw; }
    }
}
