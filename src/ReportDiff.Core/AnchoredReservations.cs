namespace ReportDiff.Core;

public sealed class AnchoredContentResourceLimitException() : Exception("内容面の追加予約が64MiBの残量を超えました。");

/// <summary>一段階の予約をまとめて回収する。実際の確保例外は見送りへ変換しない。</summary>
internal sealed class AnchoredReservations(AnchoredContentBudget budget) : IDisposable
{
    private sealed record Link(AnchoredContentBudget.Reservation Value, Link? Next);
    private Link? head;
    internal void Add(AnchoredAllocation kind, long capacity)
    {
        if (!budget.TryReserve(kind, capacity, out var token)) throw new AnchoredContentResourceLimitException();
        try { head = new(token!, head); }
        catch { token!.Dispose(); throw; }
    }
    public void Dispose()
    { while (head is { } link) { link.Value.Dispose(); head = link.Next; } }
}

/// <summary>容量の増設前に新旧両方を予約する。返却まで割当容量を保持する。</summary>
internal sealed class AnchoredBuffer<T>(AnchoredContentBudget budget, AnchoredAllocation kind) : IReadOnlyList<T>, IDisposable
{
    private T[] items = [];
    private AnchoredContentBudget.Reservation? reservation;
    public int Count { get; private set; }
    public T this[int index] => (uint)index < (uint)Count ? items[index] : throw new ArgumentOutOfRangeException(nameof(index));
    internal void Add(T item)
    {
        if (Count == items.Length)
        {
            var capacity = checked(Math.Max(8, items.Length * 2));
            if (!budget.TryReserve(kind, capacity, out var next)) throw new AnchoredContentResourceLimitException();
            try
            {
                var expanded = new T[capacity]; Array.Copy(items, expanded, Count); items = expanded;
                reservation?.Dispose(); reservation = next; next = null;
            }
            finally { next?.Dispose(); }
        }
        items[Count++] = item;
    }
    public IEnumerator<T> GetEnumerator() { for (var i = 0; i < Count; i++) yield return items[i]; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { items = []; Count = 0; reservation?.Dispose(); reservation = null; }
}
