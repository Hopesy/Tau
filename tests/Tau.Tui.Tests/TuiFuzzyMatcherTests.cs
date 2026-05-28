using Tau.Tui.Components;

namespace Tau.Tui.Tests;

public sealed class TuiFuzzyMatcherTests
{
    [Fact]
    public void Match_EmptyQueryMatchesEverythingWithZeroScore()
    {
        var result = TuiFuzzyMatcher.Match(string.Empty, "anything");

        Assert.True(result.Matches);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Match_RequiresCharactersInOrder()
    {
        Assert.True(TuiFuzzyMatcher.Match("abc", "aXbXc").Matches);
        Assert.False(TuiFuzzyMatcher.Match("abc", "cba").Matches);
    }

    [Fact]
    public void Match_IsCaseInsensitiveAndRewardsConsecutiveMatches()
    {
        var consecutive = TuiFuzzyMatcher.Match("ABC", "abc");
        var scattered = TuiFuzzyMatcher.Match("abc", "a_b_c");

        Assert.True(consecutive.Matches);
        Assert.True(scattered.Matches);
        Assert.True(consecutive.Score < scattered.Score);
    }

    [Fact]
    public void Match_RewardsWordBoundaryMatches()
    {
        var atBoundary = TuiFuzzyMatcher.Match("fb", "foo-bar");
        var notAtBoundary = TuiFuzzyMatcher.Match("fb", "afbx");

        Assert.True(atBoundary.Matches);
        Assert.True(notAtBoundary.Matches);
        Assert.True(atBoundary.Score < notAtBoundary.Score);
    }

    [Fact]
    public void Match_SupportsSwappedAlphaNumericTokens()
    {
        var result = TuiFuzzyMatcher.Match("codex52", "gpt-5.2-codex");

        Assert.True(result.Matches);
    }

    [Fact]
    public void Filter_EmptyQueryPreservesOriginalOrder()
    {
        var items = new[] { "apple", "banana", "cherry" };

        var result = TuiFuzzyMatcher.Filter(items, " ", static item => item);

        Assert.Equal(items, result);
    }

    [Fact]
    public void Filter_RequiresEveryTokenAndSortsByMatchQuality()
    {
        var items = new[] { "foo xa xb", "foo ab", "foo zz" };

        var result = TuiFuzzyMatcher.Filter(items, "foo ab", static item => item);

        Assert.Equal(["foo ab", "foo xa xb"], result);
    }
}
