using Tau.Ai;

namespace Tau.Ai.Tests;

// Direct unit coverage for the Tau-native port of upstream `packages/ai/src/utils/sanitize-unicode.ts`.
// Upstream removes unpaired UTF-16 surrogates because they cause JSON serialization errors in many
// API providers, while preserving properly paired surrogates (valid astral-plane characters such as
// emoji). These tests pin that contract directly; provider-level integration coverage lives in
// ProviderRequestTextSanitizerTests.
//
// IMPORTANT: every surrogate code unit is built from raw `char` casts (never from literal emoji or
// char.ConvertFromUtf32), so the test source stays pure ASCII and is completely immune to editor /
// file-encoding / console round-trips. A valid pair is High+Low; anything else is unpaired.
public sealed class UnicodeTextSanitizerTests
{
    private const char High = (char)0xD83D;     // a high surrogate (0xD800-0xDBFF)
    private const char High2 = (char)0xD83C;    // a second, distinct high surrogate
    private const char Low = (char)0xDE48;      // a low surrogate (0xDC00-0xDFFF)
    private const char Low2 = (char)0xDE00;     // a second, distinct low surrogate

    // Well-formed astral-plane characters (valid surrogate pairs), assembled from raw code units.
    private static readonly string Pair = new([High, Low]);
    private static readonly string Pair2 = new([High2, Low2]);

    [Fact]
    public void PreservesPairedSurrogate()
    {
        // Helper sanity: prove the pair is well-formed at runtime, independent of console rendering.
        Assert.Equal(2, Pair.Length);
        Assert.Equal(0xD83D, Pair[0]);
        Assert.Equal(0xDE48, Pair[1]);
        Assert.True(char.IsSurrogatePair(Pair[0], Pair[1]));

        var input = $"Hello {Pair} World"; // 6 + 2 + 6 = 14 code units
        Assert.Equal(14, input.Length);

        var result = UnicodeTextSanitizer.RemoveUnpairedSurrogates(input);
        // Compare on raw code units so the assertion never depends on console rendering.
        Assert.Equal(input.Length, result.Length);
        Assert.Equal(input, result);
    }

    [Fact]
    public void RemovesUnpairedHighSurrogate()
    {
        var input = $"Text {High} here";
        Assert.Equal("Text  here", UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void RemovesUnpairedLowSurrogate()
    {
        var input = $"Text {Low} here";
        Assert.Equal("Text  here", UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void RemovesHighSurrogateAtEndOfString()
    {
        var input = $"trailing{High}";
        Assert.Equal("trailing", UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void RemovesLowSurrogateAtStartOfString()
    {
        var input = $"{Low}leading";
        Assert.Equal("leading", UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void RemovesConsecutiveHighSurrogatesAsAllUnpaired()
    {
        // Two high surrogates in a row: neither is followed by a low surrogate, so both are unpaired.
        var input = $"a{High}{High}b";
        Assert.Equal("ab", UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void RemovesReversedSurrogatePairAsBothUnpaired()
    {
        // Low followed by high is not a valid pair; both characters are unpaired and removed.
        var input = $"x{Low}{High}y";
        Assert.Equal("xy", UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void PreservesValidPairAdjacentToUnpairedSurrogate()
    {
        // A lone high surrogate immediately before a valid pair must be stripped without consuming
        // the following well-formed pair.
        var input = $"{High}{Pair}";
        Assert.Equal(Pair, UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void PreservesMultipleDistinctPairs()
    {
        var input = $"{Pair2} mid {Pair} end {Pair2}";
        Assert.Equal(input, UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void ReturnsSameInstanceWhenNoSurrogatesPresent()
    {
        const string input = "plain ascii and BMP text only";
        // No surrogate code units at all, so the sanitizer should short-circuit and return the
        // original reference rather than allocating a copy.
        Assert.Same(input, UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void ReturnsSameInstanceWhenOnlyPairedSurrogatesPresent()
    {
        var input = $"only {Pair} paired";
        // Well-formed pairs never trigger the rewrite buffer, so the original reference is returned.
        Assert.Same(input, UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ReturnsInputUnchangedForNullOrEmpty(string? input)
    {
        Assert.Equal(input, UnicodeTextSanitizer.RemoveUnpairedSurrogates(input!));
    }

    [Fact]
    public void RemovesMultipleUnpairedSurrogatesWhilePreservingValidPairs()
    {
        var input = $"{High}a{Pair}b{Low}c{High}";
        Assert.Equal($"a{Pair}bc", UnicodeTextSanitizer.RemoveUnpairedSurrogates(input));
    }

    [Fact]
    public void StrippedResultContainsNoUnpairedSurrogates()
    {
        // End-to-end invariant: after sanitizing, every surrogate code unit must belong to a pair.
        var input = $"{High}a{Pair}{Low}{High2}b{Pair2}{Low2}";
        var result = UnicodeTextSanitizer.RemoveUnpairedSurrogates(input);

        for (var i = 0; i < result.Length; i++)
        {
            if (char.IsHighSurrogate(result[i]))
            {
                Assert.True(i + 1 < result.Length && char.IsLowSurrogate(result[i + 1]),
                    $"High surrogate at {i} is not followed by a low surrogate.");
                i++;
                continue;
            }

            Assert.False(char.IsLowSurrogate(result[i]), $"Found a lone low surrogate at {i}.");
        }

        // Both well-formed pairs survive intact.
        Assert.Contains(Pair, result, StringComparison.Ordinal);
        Assert.Contains(Pair2, result, StringComparison.Ordinal);
    }
}
