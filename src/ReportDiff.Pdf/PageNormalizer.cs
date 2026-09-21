using OpenCvSharp;
using ReportDiff.Core;

namespace ReportDiff.Pdf;

/// <summary>正規化後の A・B を所有する。元画像のサイズと白埋めの有無を出力処理へ渡す。</summary>
public sealed class NormalizedPagePair : IDisposable
{
    internal NormalizedPagePair(Mat a, Mat b, Size originalSizeA, Size originalSizeB)
    {
        A = a;
        B = b;
        OriginalSizeA = originalSizeA;
        OriginalSizeB = originalSizeB;
        SizeMismatch = originalSizeA != originalSizeB;
        Warnings = Array.AsReadOnly<string>(SizeMismatch ? ["SIZE_MISMATCH"] : []);
    }

    public Mat A { get; }
    public Mat B { get; }
    public Size OriginalSizeA { get; }
    public Size OriginalSizeB { get; }
    public bool SizeMismatch { get; }
    public IReadOnlyList<string> Warnings { get; }
    public void Dispose() { A.Dispose(); B.Dispose(); }
}

public static class PageNormalizer
{
    /// <summary>入力を変更せず、左上を合わせて右・下だけを白で埋めた独立した画像を返す。</summary>
    public static NormalizedPagePair Normalize(Mat a, Mat b)
    {
        Validate(a, nameof(a));
        Validate(b, nameof(b));
        var sizeA = a.Size();
        var sizeB = b.Size();
        var map = PageMap.Unaligned(sizeA, sizeB);
        var outputA = map.Render(a, PageSpace.A);
        try
        {
            var outputB = map.Render(b, PageSpace.B);
            try
            {
                return new NormalizedPagePair(outputA, outputB, sizeA, sizeB);
            }
            catch { outputB.Dispose(); throw; }
        }
        catch { outputA.Dispose(); throw; }
    }

    private static void Validate(Mat image, string name)
    {
        if (image is null || image.IsDisposed || image.Empty() || image.Dims != 2 || image.Type() != MatType.CV_8UC3)
            throw new ArgumentException("正規化には空でない BGR 8bit・3 チャンネルの画像を指定してください。", name);
    }
}
