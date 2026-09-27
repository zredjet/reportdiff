using ReportDiff.Core;
using Xunit;

namespace ReportDiff.Tests;

public sealed class AnchoredContentBudgetTests
{
    public static IEnumerable<object[]> Units() => new (AnchoredAllocation Kind, int Bytes)[] {
        (AnchoredAllocation.Cluster, 512), (AnchoredAllocation.DisplayPart, 256), (AnchoredAllocation.MaskRun, 48),
        (AnchoredAllocation.MovementWindow, 192), (AnchoredAllocation.PixelId, 4), (AnchoredAllocation.LabelId, 4),
        (AnchoredAllocation.RowBuffer, 8), (AnchoredAllocation.TextCharacter, 2), (AnchoredAllocation.Reference, 8),
        (AnchoredAllocation.DictionaryEntry, 64) }.Select(x => new object[] { x.Kind, x.Bytes });

    [Theory]
    [MemberData(nameof(Units))]
    public void Every_capacity_reserves_before_allocation_and_returns_to_existing_usage(AnchoredAllocation kind, int unit)
    {
        var required = 64 + 3 * unit;
        using var budget = new AnchoredContentBudget(PageFlowLimits.MaximumDescriptorBytes - required);
        Assert.True(budget.TryReserve(kind, 3, out var exact)); Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, budget.UsedBytes);
        Assert.False(budget.TryReserve(kind, 0, out var missing)); Assert.Null(missing);
        exact!.Dispose(); exact.Dispose(); Assert.Equal(budget.ExistingBytes, budget.UsedBytes);
        using var insufficient = new AnchoredContentBudget(budget.ExistingBytes + 1);
        Assert.False(insufficient.TryReserve(kind, 3, out _)); Assert.Equal(insufficient.ExistingBytes, insufficient.UsedBytes);
        Assert.False(budget.TryReserve(kind, long.MaxValue, out _)); Assert.False(budget.TryReserve(kind, -1, out _));
        Assert.Equal(budget.ExistingBytes, budget.UsedBytes);
    }

    [Fact]
    public void Growing_a_buffer_accounts_for_old_and_new_capacities_together()
    {
        using var budget = new AnchoredContentBudget(PageFlowLimits.MaximumDescriptorBytes - (64 + 48 * 3) - (64 + 48 * 6));
        Assert.True(budget.TryReserve(AnchoredAllocation.MaskRun, 3, out var old));
        Assert.True(budget.TryReserve(AnchoredAllocation.MaskRun, 6, out var next));
        Assert.Equal(PageFlowLimits.MaximumDescriptorBytes, budget.UsedBytes);
        old!.Dispose(); Assert.Equal(budget.ExistingBytes + 64 + 48 * 6, budget.UsedBytes);
        budget.Dispose(); next!.Dispose(); Assert.Equal(budget.ExistingBytes, budget.UsedBytes);
    }

    [Fact]
    public void Pixel_reservations_check_whole_pairs_and_overflow_before_any_allocation()
    {
        Assert.True(AnchoredContentBudget.TrySurfacePixels(PageFlowLimits.MaximumPixels - 6, 1, 2, out var exact));
        Assert.Equal(PageFlowLimits.MaximumPixels, exact);
        Assert.False(AnchoredContentBudget.TrySurfacePixels(PageFlowLimits.MaximumPixels - 5, 1, 2, out _));
        Assert.False(AnchoredContentBudget.TrySurfacePixels(1, long.MaxValue, 0, out _));
        Assert.False(AnchoredContentBudget.TrySurfacePixels(long.MaxValue, 1, 1, out _));
        Assert.False(AnchoredContentBudget.TrySurfacePixels(-1, 0, 0, out _));
    }
}
