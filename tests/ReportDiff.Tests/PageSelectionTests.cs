using ReportDiff.Pdf;
using Xunit;

namespace ReportDiff.Tests;

public sealed class PageSelectionTests
{
    [Theory]
    [InlineData(null, new[] { 1, 2, 3, 4, 5 })]
    [InlineData("1-2,5", new[] { 1, 2, 5 })]
    [InlineData(" 5, 1 - 2 ,2, 1-1 ", new[] { 1, 2, 5 })]
    [InlineData("3-5,1-4", new[] { 1, 2, 3, 4, 5 })]
    [InlineData("5", new[] { 5 })]
    public void PageListsAreOneBasedSortedAndUnique(string? value, int[] expected) =>
        Assert.Equal(expected, PageSelection.Parse(value, 5));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(",1")]
    [InlineData("1,")]
    [InlineData("1,,2")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1-")]
    [InlineData("3-1")]
    [InlineData("1-2-3")]
    [InlineData("+1")]
    [InlineData("1.5")]
    [InlineData("one")]
    [InlineData("１")]
    [InlineData("2147483648")]
    public void InvalidSyntaxHasJapaneseError(string value)
    {
        var error = Assert.Throws<PageSelectionException>(() => PageSelection.Parse(value, 5));
        Assert.Contains("--pages", error.Message);
        Assert.Contains("指定", error.Message);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("1-6")]
    [InlineData("6-7")]
    [InlineData("1-2147483647")]
    public void OutOfRangeIsRejectedBeforeExpansion(string value) =>
        Assert.Contains("1〜5", Assert.Throws<PageSelectionException>(() => PageSelection.Parse(value, 5)).Message);

    [Fact]
    public void MaximumIntegerSinglePageDoesNotOverflow() =>
        Assert.Equal(new[] { int.MaxValue }, PageSelection.Parse("2147483647", int.MaxValue));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidPageCountIsRejected(int count) =>
        Assert.Contains("ページ数", Assert.Throws<PageSelectionException>(() => PageSelection.Parse(null, count)).Message);

    [Theory]
    [InlineData(3, 3, null)]
    [InlineData(3, 1, "only_in_a")]
    [InlineData(1, 3, "only_in_b")]
    public void PagesArePairedByOriginalNumber(int countA, int countB, string? unpairedStatus)
    {
        var plan = PagePairing.Create(countA, countB);
        Assert.Equal(countA, plan.PageCountA);
        Assert.Equal(countB, plan.PageCountB);
        Assert.Equal(new[] { 1, 2, 3 }, plan.Pages.Select(p => p.PageNumber));
        foreach (var page in plan.Pages)
        {
            Assert.Equal(page.PageNumber <= countA, page.HasA);
            Assert.Equal(page.PageNumber <= countB, page.HasB);
            Assert.Equal(page.HasA && page.HasB, page.CanCompare);
            Assert.Equal(page.CanCompare ? null : unpairedStatus, page.UnpairedStatus);
        }
        Assert.Equal(countA == countB ? Array.Empty<string>() : ["PAGE_COUNT_MISMATCH"], plan.Warnings);
    }

    [Theory]
    [InlineData(2, 5, "only_in_b")]
    [InlineData(5, 2, "only_in_a")]
    public void SelectionCanIncludePagesThatExistOnOnlyOneSide(int countA, int countB, string expected)
    {
        var plan = PagePairing.Create(countA, countB, "1-2,5");
        Assert.Equal(new[] { 1, 2, 5 }, plan.Pages.Select(p => p.PageNumber));
        Assert.All(plan.Pages.Take(2), p => Assert.True(p.CanCompare));
        Assert.Equal(expected, plan.Pages[2].UnpairedStatus);
        Assert.Equal(new[] { "PAGE_COUNT_MISMATCH" }, plan.Warnings);
        Assert.Throws<PageSelectionException>(() => PagePairing.Create(countA, countB, "6"));
    }

    [Fact]
    public void PageCountWarningSurvivesSelectionOfCommonPages()
    {
        var plan = PagePairing.Create(2, 5, "2");
        Assert.Equal(2, Assert.Single(plan.Pages).PageNumber);
        Assert.True(plan.Pages[0].CanCompare);
        Assert.Equal(new[] { "PAGE_COUNT_MISMATCH" }, plan.Warnings);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void PairingRejectsInvalidDocumentCounts(int countA, int countB) =>
        Assert.Contains("ページ数", Assert.Throws<PageSelectionException>(() => PagePairing.Create(countA, countB)).Message);
}
