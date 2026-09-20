using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ReportDiff.Core;

internal static class MatBuffers
{
    public static byte[] Bytes(Mat mat)
    {
        var rows = mat.Rows;
        var data = new byte[checked(rows * mat.Cols * mat.Channels())];
        var rowSize = mat.Cols * mat.Channels();
        for (var y = 0; y < rows; y++) Marshal.Copy(mat.Ptr(y), data, y * rowSize, rowSize);
        return data;
    }

    public static float[] Floats(Mat mat)
    {
        var rows = mat.Rows;
        var data = new float[checked(rows * mat.Cols * mat.Channels())];
        var rowSize = mat.Cols * mat.Channels();
        for (var y = 0; y < rows; y++) Marshal.Copy(mat.Ptr(y), data, y * rowSize, rowSize);
        return data;
    }

    public static int[] Integers(Mat mat)
    {
        var rows = mat.Rows;
        var cols = mat.Cols;
        var data = new int[checked(rows * cols)];
        for (var y = 0; y < rows; y++) Marshal.Copy(mat.Ptr(y), data, y * cols, cols);
        return data;
    }

    public static Mat Mask(byte[] pixels, int width, int height)
    {
        var mat = new Mat(height, width, MatType.CV_8UC1);
        Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
        return mat;
    }
}
