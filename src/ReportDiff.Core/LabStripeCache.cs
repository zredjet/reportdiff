using OpenCvSharp;

namespace ReportDiff.Core;

// 同じ画像の帯を上から順に処理するworker専用。画像やworkerをまたいで共有しない。
internal sealed class LabStripeCache : IDisposable
{
    private readonly Mat storage;
    private readonly int width;
    private int previousTop;
    private int previousBottom;

    public LabStripeCache(int width, int rows)
    {
        this.width = width;
        storage = new Mat(rows, width, MatType.CV_32FC3);
    }

    // 戻り値は借用ヘッダー。次のGetより前に破棄し、storageは全ヘッダーより長く保持する。
    public Mat Get(Mat image, int top, int bottom)
    {
        ObjectDisposedException.ThrowIf(storage.IsDisposed, this);
        var overlap = Math.Max(0, Math.Min(previousBottom, bottom) - top);
        if (overlap > 0)
        {
            var data = storage.AsSpan<Vec3f>();
            // Span.CopyToは重なる区間にも対応する。Mat.CopyToによる重複領域のコピーは使わない。
            data.Slice((top - previousTop) * width, overlap * width).CopyTo(data);
        }
        if (top + overlap < bottom)
        {
            using var source = new Mat(image, new Rect(0, top + overlap, width, bottom - top - overlap));
            using var normalized = new Mat();
            using var target = new Mat(storage, new Rect(0, overlap, width, bottom - top - overlap));
            source.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
            Cv2.CvtColor(normalized, target, ColorConversionCodes.BGR2Lab);
        }
        previousTop = top;
        previousBottom = bottom;
        // 有効行だけを親画像とする。末尾の余った容量をフィルターの近傍に含めない。
        return Mat.FromPixelData(bottom - top, width, MatType.CV_32FC3, storage.Data, storage.Step());
    }

    public void Dispose() => storage.Dispose();
}
