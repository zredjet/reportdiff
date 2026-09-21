using OpenCvSharp;

namespace ReportDiff.Core;

/// <summary>1 回のページ比較で生成したインクだけを、分類まで引き継ぐ所有者。</summary>
internal sealed class ComparisonInk : IDisposable
{
    private Mat? inkA;
    private Mat? inkB;
    private Mat? sourceA;
    private Mat? sourceB;
    private int dpi;
    private InkOptions? options;
    private bool disposed;

    public void Capture(Mat a, Mat b, ComparisonParameters parameters, Mat preparedA, Mat preparedB)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (inkA is not null) throw new InvalidOperationException("インク画像は比較ごとに一度だけ引き継いでください。");
        ObjectDisposedException.ThrowIf(preparedA.IsDisposed, preparedA);
        // 画素のコピーではなく参照カウント付きのヘッダーを所有する。
        // 大きな平滑化・コントラストの Mat は保持しない。
        var retainedA = new Mat(preparedA, new Rect(0, 0, preparedA.Cols, preparedA.Rows));
        try
        {
            ObjectDisposedException.ThrowIf(preparedB.IsDisposed, preparedB);
            inkB = new Mat(preparedB, new Rect(0, 0, preparedB.Cols, preparedB.Rows));
            inkA = retainedA;
            sourceA = a; sourceB = b;
            dpi = parameters.Dpi; options = parameters.Ink;
        }
        catch { retainedA.Dispose(); throw; }
    }

    public (Mat? A, Mat? B) ReadFor(Mat a, Mat b, ComparisonParameters parameters)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // 同じ寸法でも別の画像や設定へは流用しない。未生成・不一致なら分類側で計算する。
        return ReferenceEquals(a, sourceA) && ReferenceEquals(b, sourceB)
            && parameters.Dpi == dpi && parameters.Ink == options
            ? (inkA, inkB) : (null, null);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        inkB?.Dispose(); inkA?.Dispose();
        inkB = inkA = sourceB = sourceA = null;
    }
}
