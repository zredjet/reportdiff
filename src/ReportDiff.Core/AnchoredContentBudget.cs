namespace ReportDiff.Core;

public enum AnchoredAllocation
{
    Cluster, DisplayPart, MaskRun, MovementWindow, PixelId, LabelId, RowBuffer, TextCharacter,
    Reference, DictionaryEntry
}

/// <summary>既存記述と同じ64MiBを使う追加予約。逐次処理専用。実RSSの保証値ではない。</summary>
public sealed class AnchoredContentBudget : IDisposable
{
    public long ExistingBytes { get; }
    public long UsedBytes { get; private set; }
    public long PeakBytes { get; private set; }
    public long RemainingBytes => PageFlowLimits.MaximumDescriptorBytes - UsedBytes;
    public bool IsDisposed { get; private set; }

    internal AnchoredContentBudget(long existingBytes)
    {
        if (existingBytes < 0 || existingBytes > PageFlowLimits.MaximumDescriptorBytes)
            throw new ArgumentOutOfRangeException(nameof(existingBytes));
        ExistingBytes = UsedBytes = PeakBytes = existingBytes;
    }

    /// <summary>配列・辞書の固定費64Bを含む。容量拡張では新容量を予約してから旧予約を解放する。</summary>
    public bool TryReserve(AnchoredAllocation allocation, long capacity, out Reservation? reservation)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var unit = allocation switch
        {
            AnchoredAllocation.Cluster => 512, AnchoredAllocation.DisplayPart => 256,
            AnchoredAllocation.MaskRun => 48, AnchoredAllocation.MovementWindow => 192,
            AnchoredAllocation.PixelId or AnchoredAllocation.LabelId => 4,
            AnchoredAllocation.RowBuffer or AnchoredAllocation.Reference => 8,
            AnchoredAllocation.TextCharacter => 2, AnchoredAllocation.DictionaryEntry => 64,
            _ => throw new ArgumentOutOfRangeException(nameof(allocation))
        };
        reservation = null;
        if (capacity < 0) return false;
        try { return TryReserveBytes(checked(64 + unit * capacity), out reservation); }
        catch (OverflowException) { return false; }
    }

    internal bool TryReserveBytes(long bytes, out Reservation? reservation)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        reservation = null;
        if (bytes < 0 || bytes > RemainingBytes) return false;
        // Reservation自体の固定領域も各計上単位に含む。確保失敗時に使用量を増やさない。
        reservation = new(this, bytes);
        UsedBytes += bytes; PeakBytes = Math.Max(PeakBytes, UsedBytes);
        return true;
    }

    internal static bool TrySurfacePixels(long existing, long content, long display, out long total)
    {
        total = 0;
        if (existing < 0 || content < 0 || display < 0) return false;
        try { total = checked(existing + 2 * content + 2 * display); }
        catch (OverflowException) { return false; }
        return total <= PageFlowLimits.MaximumPixels;
    }

    public void Dispose() { IsDisposed = true; UsedBytes = ExistingBytes; }

    public sealed class Reservation : IDisposable
    {
        private AnchoredContentBudget? owner;
        public long Bytes { get; }
        internal Reservation(AnchoredContentBudget owner, long bytes) { this.owner = owner; Bytes = bytes; }
        public void Dispose()
        {
            var budget = owner; owner = null;
            if (budget is not null && !budget.IsDisposed) budget.UsedBytes -= Bytes;
        }
    }
}
