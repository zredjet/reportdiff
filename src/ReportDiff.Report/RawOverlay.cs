using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ReportDiff.Report;

/// <summary>補正前の BGR 入力だけから生成する。判定結果や設定には依存しない。</summary>
internal static class RawOverlay
{
    internal const string Method = "grayscale_red_blue_v1";

    internal static Mat Create(Mat a, Mat b)
    {
        if (a.Empty() || b.Empty() || a.Dims != 2 || b.Dims != 2 || a.Type() != MatType.CV_8UC3
            || b.Type() != MatType.CV_8UC3 || a.Size() != b.Size())
            throw new ArgumentException("確認用オーバーレイには同じサイズの空でない BGR 8bit 画像を指定してください。");
        var output = new Mat(a.Size(), MatType.CV_8UC3);
        try
        {
            var length = checked(a.Width * 3);
            var height = a.Height;
            var rowA = new byte[length]; var rowB = new byte[length]; var rowOut = new byte[length];
            for (var y = 0; y < height; y++)
            {
                // 行単位のアクセスにより、ROI と連続画像で同じ演算を行う。
                Marshal.Copy(a.Ptr(y), rowA, 0, length);
                Marshal.Copy(b.Ptr(y), rowB, 0, length);
                for (var x = 0; x < length; x += 3)
                {
                    var ga = Gray(rowA, x); var gb = Gray(rowB, x);
                    rowOut[x] = ga; rowOut[x + 1] = Math.Min(ga, gb); rowOut[x + 2] = gb;
                }
                Marshal.Copy(rowOut, 0, output.Ptr(y), length);
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private static byte Gray(byte[] row, int x) =>
        (byte)((114 * row[x] + 587 * row[x + 1] + 299 * row[x + 2] + 500) / 1000);
}
