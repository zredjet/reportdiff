using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>一意で、関連する差分全体を説明できる平行移動だけを注釈する。</summary>
internal static class MovementAnnotator
{
    private sealed record Candidate(MovementShift Shift, int[] Indices);

    public static DifferenceCluster[] Annotate(Mat a, Mat b, ComparisonParameters parameters,
        byte[] raw, int[] labels, int[] acceptedLabels, DifferenceCluster[] clusters)
    {
        var width = a.Width; var height = a.Height;
        var radius = (int)Math.Floor(Units.MmToPixels(parameters.Move.SearchMm, parameters.Dpi));
        if (radius < 1) return clusters;
        var margin = Math.Max(1, (int)Math.Ceiling(Units.MmToPixels(1, parameters.Dpi)));
        var kept = acceptedLabels.ToHashSet();
        var exclusions = parameters.Exclude.Select(e =>
        {
            var left = (int)Math.Clamp(Math.Floor(Units.MmToPixels(e.X, parameters.Dpi)), 0, width);
            var top = (int)Math.Clamp(Math.Floor(Units.MmToPixels(e.Y, parameters.Dpi)), 0, height);
            var right = (int)Math.Clamp(Math.Ceiling(Units.MmToPixels(e.X + e.W, parameters.Dpi)), 0, width);
            var bottom = (int)Math.Clamp(Math.Ceiling(Units.MmToPixels(e.Y + e.H, parameters.Dpi)), 0, height);
            return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }).ToArray();
        var windows = clusters.Select(c => new Rect(c.Bounds.X - margin, c.Bounds.Y - margin,
            c.Bounds.Width + 2 * margin, c.Bounds.Height + 2 * margin)).ToArray();
        var validation = parameters with { Diff = parameters.Diff with { MaxShiftMm = 0 } };
        var proposals = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var source = 0; source < clusters.Length; source++)
        {
            if (clusters[source].Kind is not ("removed" or "changed")) continue;
            var window = windows[source];
            if (!Safe(window)) continue;
            using var template = new Mat(a, window);
            if (!CompleteTemplate(template, parameters.Dpi)) continue;
            var shift = FindUnique(template, b, window, radius, parameters.Move);
            if (shift is null || shift is { Dx: 0, Dy: 0 }) continue;
            var destination = Translate(window, shift);
            if (!Safe(destination) || !EqualAt(a, window, b, destination, validation)) continue;
            using var target = new Mat(b, destination);
            var inverse = FindUnique(target, a, destination, radius, parameters.Move);
            if (inverse is null || inverse.Dx != -shift.Dx || inverse.Dy != -shift.Dy) continue;

            // 外接矩形だけで関連付けず、各成分の生差分が領域に入るかを調べる。
            var related = new List<int>();
            var partial = false;
            for (var index = 0; index < clusters.Length; index++)
            {
                var bounds = clusters[index].Bounds;
                if (!Intersects(bounds, window) && !Intersects(bounds, destination)) continue;
                var covered = 0;
                for (var y = bounds.Top; y < bounds.Bottom; y++)
                for (var x = bounds.Left; x < bounds.Right; x++)
                {
                    var pixel = y * width + x;
                    if (raw[pixel] != 0 && labels[pixel] == acceptedLabels[index]
                        && (window.Contains(x, y) || destination.Contains(x, y))) covered++;
                }
                if (covered == 0) continue;
                if (covered != clusters[index].Pixels) { partial = true; break; }
                related.Add(index);
            }
            if (partial || !related.Contains(source)) continue;
            var explainsAll = true;
            foreach (var index in related)
            {
                var area = windows[index];
                var forward = Translate(area, shift);
                var backward = Translate(area, new(-shift.Dx, -shift.Dy));
                if (!Safe(area) || !Safe(forward) || !Safe(backward)
                    || !EqualAt(a, area, b, forward, validation)
                    || !EqualAt(a, backward, b, area, validation))
                { explainsAll = false; break; }
            }
            if (!explainsAll) continue;
            var key = $"{shift.Dx},{shift.Dy}:{string.Join(',', related)}";
            if (seen.Add(key)) proposals.Add(new(shift, related.ToArray()));
        }

        // 同じクラスタに異なる対応が提案された場合、順序で勝者を決めず保留する。
        var uses = new int[clusters.Length];
        foreach (var candidate in proposals) foreach (var index in candidate.Indices) uses[index]++;
        var result = (DifferenceCluster[])clusters.Clone();
        foreach (var candidate in proposals)
        {
            if (candidate.Indices.Any(index => uses[index] != 1)) continue;
            foreach (var index in candidate.Indices)
                result[index] = clusters[index] with
                {
                    Kind = "moved", ShiftPx = candidate.Shift,
                    RelatedClusterIds = Array.AsReadOnly(candidate.Indices.Where(other => other != index)
                        .Select(other => clusters[other].Id).Order().ToArray())
                };
        }
        return result;

        bool Safe(Rect region)
        {
            if (region.Left < 0 || region.Top < 0 || region.Right > width || region.Bottom > height) return false;
            foreach (var exclusion in exclusions) if (Intersects(region, exclusion)) return false;
            for (var y = region.Top; y < region.Bottom; y++)
            for (var x = region.Left; x < region.Right; x++)
            {
                var pixel = y * width + x;
                if (raw[pixel] != 0 && !kept.Contains(labels[pixel])) return false;
            }
            return true;
        }
    }

    private static bool CompleteTemplate(Mat template, int dpi)
    {
        Cv2.MeanStdDev(template, out _, out var deviation);
        if (deviation.Val0 == 0 && deviation.Val1 == 0 && deviation.Val2 == 0) return false;
        using var lab = ImageInk.ToLab(template);
        using var ink = ImageInk.FromLab(lab, dpi);
        if (Cv2.CountNonZero(ink) == 0) return false;
        var width = ink.Width; var height = ink.Height;
        using var top = ink.Row(0); using var bottom = ink.Row(height - 1);
        using var left = ink.Col(0); using var right = ink.Col(width - 1);
        return Cv2.CountNonZero(top) + Cv2.CountNonZero(bottom) + Cv2.CountNonZero(left) + Cv2.CountNonZero(right) == 0;
    }

    private static MovementShift? FindUnique(Mat template, Mat image, Rect origin, int radius, MoveOptions options)
    {
        var left = Math.Max(0, origin.Left - radius); var top = Math.Max(0, origin.Top - radius);
        var right = Math.Min(image.Width, origin.Right + radius); var bottom = Math.Min(image.Height, origin.Bottom + radius);
        using var search = new Mat(image, new Rect(left, top, right - left, bottom - top));
        using var scores = new Mat();
        Cv2.MatchTemplate(search, template, scores, TemplateMatchModes.CCoeffNormed);
        var values = MatBuffers.Floats(scores);
        var columns = scores.Width;
        var best = -1; var bestScore = double.NegativeInfinity;
        for (var i = 0; i < values.Length; i++)
            if (float.IsFinite(values[i]) && values[i] > bestScore) { bestScore = values[i]; best = i; }
        if (best < 0 || bestScore < options.MinScore) return null;
        var bestX = best % columns; var bestY = best / columns;
        var second = double.NegativeInfinity;
        for (var i = 0; i < values.Length; i++)
        {
            if (Math.Abs(i % columns - bestX) <= 1 && Math.Abs(i / columns - bestY) <= 1) continue;
            if (float.IsFinite(values[i])) second = Math.Max(second, values[i]);
        }
        if (bestScore - second < options.MinScoreGap) return null;
        return new(left + bestX - origin.Left, top + bestY - origin.Top);
    }

    private static bool EqualAt(Mat a, Rect areaA, Mat b, Rect areaB, ComparisonParameters parameters)
    {
        using var cropA = new Mat(a, areaA); using var cropB = new Mat(b, areaB);
        using var difference = TolerantDifference.Calculate(cropA, cropB, parameters);
        return Cv2.CountNonZero(difference.RawMask) == 0;
    }

    private static Rect Translate(Rect area, MovementShift shift) => new(area.X + shift.Dx, area.Y + shift.Dy, area.Width, area.Height);
    private static bool Intersects(Rect a, Rect b) => a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0
        && a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
