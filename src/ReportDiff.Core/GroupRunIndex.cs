namespace ReportDiff.Core;

internal readonly record struct GroupRun(int Y, int Left, int Right);

/// <summary>グループの全画素を行・列順の区間で保持する、比較中だけの索引。</summary>
internal sealed class GroupRunIndex(GroupRun[] runs, int[] offsets, int[] initialCounts, int[] pixelCounts)
{
    internal const long DefaultMemoryBudget = 64L * 1024 * 1024;
    public int GroupCount => initialCounts.Length;
    public int RunCount => runs.Length;
    public int InitialCount(int group) => initialCounts[group];
    public int PixelCount(int group) => pixelCounts[group];
    public ReadOnlySpan<GroupRun> Runs(int group) => runs.AsSpan(offsets[group], offsets[group + 1] - offsets[group]);

    public static GroupRunIndex? TryCreate(int[] labels, byte[] candidates, int width, int height, int count,
        long memoryBudget = DefaultMemoryBudget)
    {
        // 構築中のカーソル配列も含めて予算化する。既存のラベル1枚分よりは増やさない。
        var budget = Math.Min(memoryBudget, labels.LongLength * sizeof(int));
        var metadataBytes = (long)count * 4 * sizeof(int) + sizeof(int);
        if (metadataBytes > budget) return null;
        var cursors = new int[count];
        var initial = new int[count];
        var pixels = new int[count];
        var firstX = width; var lastX = 0; var firstY = height; var lastY = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width;)
            {
                var group = labels[row + x];
                if (group == 0) { x++; continue; }
                var left = x;
                var candidateCount = 0;
                do { if (candidates[row + x] != 0) candidateCount++; x++; }
                while (x < width && labels[row + x] == group);
                cursors[group]++;
                initial[group] += candidateCount;
                pixels[group] += x - left;
                firstX = Math.Min(firstX, left); lastX = Math.Max(lastX, x);
                firstY = Math.Min(firstY, y); lastY = y + 1;
            }
        }
        var offsets = new int[count + 1];
        for (var group = 0; group < count; group++)
            offsets[group + 1] = checked(offsets[group] + (initial[group] == 0 ? 0 : cursors[group]));
        if (metadataBytes + (long)offsets[count] * 3 * sizeof(int) > budget) return null;

        // 正確な長さを一度だけ確保する。Listの拡張・全区間の複製・画素別オブジェクトを避ける。
        var runs = new GroupRun[offsets[count]];
        Array.Copy(offsets, cursors, count);
        // 局所変更だけのページでは、2回目に広い背景を再走査しない。
        for (var y = firstY; y < lastY; y++)
        {
            var row = y * width;
            for (var x = firstX; x < lastX;)
            {
                var group = labels[row + x];
                var left = x;
                do { x++; } while (x < lastX && labels[row + x] == group);
                if (group != 0 && initial[group] != 0) runs[cursors[group]++] = new(y, left, x);
            }
        }
        return new(runs, offsets, initial, pixels);
    }
}
