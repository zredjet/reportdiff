using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>比較中だけ保持する特徴量。ページ全体の Lab やマネージド配列を重複して持たない。</summary>
internal sealed class ComparisonFeatures : IDisposable
{
    private const int StripeRows = 128;
    private readonly Mat values = new();
    private readonly Mat contrast = new();
    private bool disposed;
    public Mat Ink { get; } = new();

    private ComparisonFeatures() { }

    // Create で確保した連続 Mat だけを参照する。呼び出し側の using の範囲内でのみ使用する。
    public ComparisonFeatureData Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new(MemoryMarshal.Cast<Vec3f, float>(values.AsSpan<Vec3f>()),
            MemoryMarshal.Cast<Vec3f, float>(contrast.AsSpan<Vec3f>()));
    }

    public static ComparisonFeatures Create(Mat image, ComparisonParameters parameters, bool includeInk)
    {
        var features = new ComparisonFeatures();
        try
        {
            features.Fill(image, parameters, includeInk);
            return features;
        }
        catch { features.Dispose(); throw; }
    }

    private void Fill(Mat image, ComparisonParameters parameters, bool includeInk)
    {
        var rows = image.Rows;
        var cols = image.Cols;
        // Span の長さを超える画像では、領域を確保する前に失敗させる。
        _ = checked(rows * cols * 3);
        var tolerant = parameters.Diff.EdgeTolerance != 0;
        values.Create(rows, cols, MatType.CV_32FC3);
        if (tolerant) contrast.Create(rows, cols, MatType.CV_32FC3);
        if (includeInk) Ink.Create(rows, cols, MatType.CV_8UC1);

        var margin = tolerant ? 2 : 0;
        if (includeInk)
            margin = Math.Max(margin, Math.Max(1, Units.RoundPixels(parameters.Ink.BackgroundRadiusMm, parameters.Dpi)));
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var blur = new Mat();
        using var maximum = new Mat();
        using var minimum = new Mat();
        for (var y = 0; y < rows; y += StripeRows)
        {
            var end = Math.Min(rows, y + StripeRows);
            var top = Math.Max(0, y - margin);
            var bottom = (int)Math.Min(rows, (long)end + margin);
            // 必要な近傍を含めてから Lab にする。帯の内部に偽の画像端を作らず、
            // 元画像の端だけに OpenCV 既定の境界処理を適用する。ROI の親画像も混ぜない。
            using var source = new Mat(image, new Rect(0, top, cols, bottom - top));
            using var lab = ImageInk.ToLab(source);
            if (tolerant)
            {
                // インクの局所背景に必要な広い余白では、特徴量フィルターを繰り返さない。
                // 5×5 の半径 2px を残せば、保存する帯の画素値は全ページ演算と一致する。
                var featureTop = Math.Max(0, y - 2);
                var featureBottom = (int)Math.Min(rows, (long)end + 2);
                using var featureLab = new Mat(lab, new Rect(0, featureTop - top, cols, featureBottom - featureTop));
                Cv2.Blur(featureLab, blur, new Size(3, 3));
                Cv2.Dilate(featureLab, maximum, kernel);
                Cv2.Erode(featureLab, minimum, kernel);
                Cv2.Subtract(maximum, minimum, maximum);
                CopyRows(blur, values, y - featureTop, y, end - y);
                CopyRows(maximum, contrast, y - featureTop, y, end - y);
            }
            else
                CopyRows(lab, values, y - top, y, end - y);

            if (includeInk)
            {
                using var ink = ImageInk.FromLab(lab, parameters.Dpi, parameters.Ink);
                CopyRows(ink, Ink, y - top, y, end - y);
            }
        }
    }

    private static void CopyRows(Mat source, Mat destination, int sourceTop, int destinationTop, int count)
    {
        using var from = new Mat(source, new Rect(0, sourceTop, source.Cols, count));
        using var to = new Mat(destination, new Rect(0, destinationTop, destination.Cols, count));
        from.CopyTo(to);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Ink.Dispose();
        contrast.Dispose();
        values.Dispose();
    }
}

// ヒープに保存できない参照ビュー。画素ループ内で Mat のインデクサを呼ばない。
internal readonly ref struct ComparisonFeatureData(ReadOnlySpan<float> values, ReadOnlySpan<float> contrast)
{
    public ReadOnlySpan<float> Values { get; } = values;
    public ReadOnlySpan<float> Contrast { get; } = contrast;
}
