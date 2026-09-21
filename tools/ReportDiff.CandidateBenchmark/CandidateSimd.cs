using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ReportDiff.Core;

// 試測定専用。製品からは参照しない。
internal enum CandidateKernel { Scalar, Auto, Vector128, Vector256 }
internal static class CandidateSimd
{
    public static CandidateKernel Selected => Vector256.IsHardwareAccelerated ? CandidateKernel.Vector256
        : Vector128.IsHardwareAccelerated ? CandidateKernel.Vector128 : CandidateKernel.Scalar;

    public static void Fill(ComparisonFeatureData a, ComparisonFeatureData b, DiffOptions options,
        int firstPixel, Span<byte> output, CandidateKernel kernel = CandidateKernel.Auto)
    {
        if (kernel == CandidateKernel.Auto) kernel = Selected;
        if (kernel == CandidateKernel.Scalar) { InitialCandidates.Fill(a, b, options, firstPixel, output); return; }
        var count = kernel == CandidateKernel.Vector256
            ? Fill256(a, b, options, firstPixel, output) : Fill128(a, b, options, firstPixel, output);
        // 末尾の不完全なベクトルは既存のスカラー式で処理し、行区間の外を読まない。
        InitialCandidates.Fill(a, b, options, firstPixel + count, output[count..]);
    }

    private static int Fill128(ComparisonFeatureData a, ComparisonFeatureData b, DiffOptions options,
        int firstPixel, Span<byte> output)
    {
        // LoadUnsafeの前に必要な全区間を検証する。3ベクトルで4画素、余分な画像は作らない。
        var length = checked(output.Length * 3);
        var start = checked(firstPixel * 3);
        ref var av = ref MemoryMarshal.GetReference(a.Values.Slice(start, length));
        ref var bv = ref MemoryMarshal.GetReference(b.Values.Slice(start, length));
        var tolerance = (float)options.EdgeTolerance;
        var strict = tolerance == 0;
        ref var ac = ref MemoryMarshal.GetReference(strict ? ReadOnlySpan<float>.Empty : a.Contrast.Slice(start, length));
        ref var bc = ref MemoryMarshal.GetReference(strict ? ReadOnlySpan<float>.Empty : b.Contrast.Slice(start, length));
        var tv = Vector128.Create((float)options.ColorThreshold);
        var kv = Vector128.Create(tolerance);
        var pixel = 0;
        for (; pixel <= output.Length - 4; pixel += 4)
        {
            var offset = (nuint)(pixel * 3);
            var m0 = Compare128(ref av, ref bv, ref ac, ref bc, offset, tv, kv, strict).ExtractMostSignificantBits();
            var m1 = Compare128(ref av, ref bv, ref ac, ref bc, offset + 4, tv, kv, strict).ExtractMostSignificantBits();
            var m2 = Compare128(ref av, ref bv, ref ac, ref bc, offset + 8, tv, kv, strict).ExtractMostSignificantBits();
            var mask = m0 | (m1 << 4) | (m2 << 8);
            if (mask == 0) continue;
            for (var lane = 0; lane < 4; lane++, mask >>= 3)
                if ((mask & 7) != 0) output[pixel + lane] = 255;
        }
        return pixel;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Compare128(ref float a, ref float b, ref float ac, ref float bc,
        nuint offset, Vector128<float> threshold, Vector128<float> tolerance, bool strict)
    {
        var difference = Vector128.Abs(Vector128.LoadUnsafe(ref a, offset) - Vector128.LoadUnsafe(ref b, offset));
        // FMAを使わず、float32の乗算と加算を個別に評価する。
        var limit = strict ? threshold : threshold + tolerance *
            Vector128.Max(Vector128.LoadUnsafe(ref ac, offset), Vector128.LoadUnsafe(ref bc, offset));
        return Vector128.GreaterThan(difference, limit);
    }

    private static int Fill256(ComparisonFeatureData a, ComparisonFeatureData b, DiffOptions options,
        int firstPixel, Span<byte> output)
    {
        // LoadUnsafeの前に必要な全区間を検証する。3ベクトルで8画素、余分な画像は作らない。
        var length = checked(output.Length * 3);
        var start = checked(firstPixel * 3);
        ref var av = ref MemoryMarshal.GetReference(a.Values.Slice(start, length));
        ref var bv = ref MemoryMarshal.GetReference(b.Values.Slice(start, length));
        var tolerance = (float)options.EdgeTolerance;
        var strict = tolerance == 0;
        ref var ac = ref MemoryMarshal.GetReference(strict ? ReadOnlySpan<float>.Empty : a.Contrast.Slice(start, length));
        ref var bc = ref MemoryMarshal.GetReference(strict ? ReadOnlySpan<float>.Empty : b.Contrast.Slice(start, length));
        var tv = Vector256.Create((float)options.ColorThreshold);
        var kv = Vector256.Create(tolerance);
        var pixel = 0;
        for (; pixel <= output.Length - 8; pixel += 8)
        {
            var offset = (nuint)(pixel * 3);
            var m0 = Compare256(ref av, ref bv, ref ac, ref bc, offset, tv, kv, strict).ExtractMostSignificantBits();
            var m1 = Compare256(ref av, ref bv, ref ac, ref bc, offset + 8, tv, kv, strict).ExtractMostSignificantBits();
            var m2 = Compare256(ref av, ref bv, ref ac, ref bc, offset + 16, tv, kv, strict).ExtractMostSignificantBits();
            var mask = m0 | (m1 << 8) | (m2 << 16);
            if (mask == 0) continue;
            for (var lane = 0; lane < 8; lane++, mask >>= 3)
                if ((mask & 7) != 0) output[pixel + lane] = 255;
        }
        return pixel;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Compare256(ref float a, ref float b, ref float ac, ref float bc,
        nuint offset, Vector256<float> threshold, Vector256<float> tolerance, bool strict)
    {
        var difference = Vector256.Abs(Vector256.LoadUnsafe(ref a, offset) - Vector256.LoadUnsafe(ref b, offset));
        // FMAを使わず、float32の乗算と加算を個別に評価する。
        var limit = strict ? threshold : threshold + tolerance *
            Vector256.Max(Vector256.LoadUnsafe(ref ac, offset), Vector256.LoadUnsafe(ref bc, offset));
        return Vector256.GreaterThan(difference, limit);
    }
}
