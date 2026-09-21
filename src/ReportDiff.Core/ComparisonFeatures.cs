using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>比較中だけ保持する特徴量。ページ全体の Lab やマネージド配列を重複して持たない。</summary>
internal sealed class ComparisonFeatures : IDisposable
{
    private readonly Mat values = new();
    private readonly Mat contrast = new();
    private bool disposed;
    public Mat Ink { get; } = new();
    internal int WorkerCount { get; private set; }
    internal long WorkerTemporaryBytes { get; private set; }

    private ComparisonFeatures() { }

    // Create で確保した連続 Mat だけを参照する。呼び出し側の using の範囲内でのみ使用する。
    public ComparisonFeatureData Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new(MemoryMarshal.Cast<Vec3f, float>(values.AsSpan<Vec3f>()),
            MemoryMarshal.Cast<Vec3f, float>(contrast.AsSpan<Vec3f>()));
    }

    public static ComparisonFeatures Create(Mat image, ComparisonParameters parameters, bool includeInk) =>
        Create(image, parameters, includeInk, new());

    internal static ComparisonFeatures Create(Mat image, ComparisonParameters parameters, bool includeInk, FeatureExecution execution)
    {
        var features = new ComparisonFeatures();
        try
        {
            features.Fill(image, parameters, includeInk, execution);
            return features;
        }
        catch { features.Dispose(); throw; }
    }

    private void Fill(Mat image, ComparisonParameters parameters, bool includeInk, FeatureExecution execution)
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
        var schedule = FeatureStripeSchedule.Create(cols, rows, margin, tolerant, includeInk, execution);
        WorkerCount = schedule.Degree;
        WorkerTemporaryBytes = schedule.WorkerTemporaryBytes;
        schedule.Run((firstStripe, lastStripe) =>
            FillStripes(image, parameters, includeInk, tolerant, margin, firstStripe, lastStripe));
    }

    private void FillStripes(Mat image, ComparisonParameters parameters, bool includeInk, bool tolerant, int margin,
        int firstStripe, int lastStripe)
    {
        var rows = image.Rows;
        var cols = image.Cols;
        // カーネルと再利用する一時Matはworker専用。全ページの出力Matは事前確保済み。
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var minimum = new Mat();
        for (var stripe = firstStripe; stripe < lastStripe; stripe++)
        {
            var y = stripe * FeatureStripeSchedule.StripeRows;
            var end = Math.Min(rows, y + FeatureStripeSchedule.StripeRows);
            var top = Math.Max(0, y - margin);
            var bottom = (int)Math.Min(rows, (long)end + margin);
            // 必要な近傍を含めてから Lab にする。帯の内部に偽の画像端を作らず、
            // 元画像の端だけに OpenCV 既定の境界処理を適用する。呼び出し元の ROI の外側は Lab に含めない。
            using var source = new Mat(image, new Rect(0, top, cols, bottom - top));
            using var lab = ImageInk.ToLab(source);
            if (tolerant)
            {
                // 親Labに必要な余白を保持し、中央ROIの外側もフィルターの近傍として参照する。
                // 出力は担当行だけ。コントラスト出力をdilateの一時結果にも使う。
                using var center = new Mat(lab, new Rect(0, y - top, cols, end - y));
                using var outputValues = new Mat(values, new Rect(0, y, cols, end - y));
                using var outputContrast = new Mat(contrast, new Rect(0, y, cols, end - y));
                Cv2.Blur(center, outputValues, new Size(3, 3));
                Cv2.Dilate(center, outputContrast, kernel);
                Cv2.Erode(center, minimum, kernel);
                Cv2.Subtract(outputContrast, minimum, outputContrast);
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
