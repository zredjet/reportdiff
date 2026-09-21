namespace ReportDiff.Pdf;

public sealed record TextOptions
{
    public int MaxLettersPerPage { get; init; } = 100_000;
    public int MaxWordsPerPage { get; init; } = 20_000;
    public int MaxRunesPerCluster { get; init; } = 2000;
    public double MinLineOverlap { get; init; } = 0.5;

    internal TextOptions Validated()
    {
        if (MaxLettersPerPage is < 1 or > 1_000_000 || MaxWordsPerPage is < 1 or > 200_000
            || MaxRunesPerCluster is < 1 or > 100_000 || !double.IsFinite(MinLineOverlap)
            || MinLineOverlap is <= 0 or > 1)
            throw new ArgumentException("PDF テキスト注釈の上限・行の重なり率が不正です。");
        return this;
    }
}
