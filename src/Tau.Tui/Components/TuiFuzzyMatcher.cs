namespace Tau.Tui.Components;

internal readonly record struct TuiFuzzyMatch(bool Matches, double Score);

internal static class TuiFuzzyMatcher
{
    private static readonly char[] TokenSeparators = [' ', '\t', '\r', '\n'];

    public static TuiFuzzyMatch Match(string? query, string? text)
    {
        var normalizedQuery = (query ?? string.Empty).ToLowerInvariant();
        var normalizedText = (text ?? string.Empty).ToLowerInvariant();

        var primary = MatchNormalized(normalizedQuery, normalizedText);
        if (primary.Matches)
        {
            return primary;
        }

        var swappedQuery = TrySwapAlphaNumericToken(normalizedQuery);
        if (swappedQuery.Length == 0)
        {
            return primary;
        }

        var swapped = MatchNormalized(swappedQuery, normalizedText);
        return swapped.Matches ? swapped with { Score = swapped.Score + 5 } : primary;
    }

    public static IReadOnlyList<T> Filter<T>(IEnumerable<T> items, string? query, Func<T, string> getText)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(getText);

        if (string.IsNullOrWhiteSpace(query))
        {
            return items.ToArray();
        }

        var tokens = query.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return items.ToArray();
        }

        var results = new List<(T Item, double Score, int Index)>();
        var index = 0;
        foreach (var item in items)
        {
            var text = getText(item);
            double totalScore = 0;
            var allMatch = true;

            foreach (var token in tokens)
            {
                var match = Match(token, text);
                if (!match.Matches)
                {
                    allMatch = false;
                    break;
                }

                totalScore += match.Score;
            }

            if (allMatch)
            {
                results.Add((item, totalScore, index));
            }

            index++;
        }

        return results
            .OrderBy(static result => result.Score)
            .ThenBy(static result => result.Index)
            .Select(static result => result.Item)
            .ToArray();
    }

    private static TuiFuzzyMatch MatchNormalized(string query, string text)
    {
        if (query.Length == 0)
        {
            return new TuiFuzzyMatch(true, 0);
        }

        if (query.Length > text.Length)
        {
            return new TuiFuzzyMatch(false, 0);
        }

        var queryIndex = 0;
        double score = 0;
        var lastMatchIndex = -1;
        var consecutiveMatches = 0;

        for (var i = 0; i < text.Length && queryIndex < query.Length; i++)
        {
            if (text[i] != query[queryIndex])
            {
                continue;
            }

            var isWordBoundary = i == 0 || IsWordBoundarySeparator(text[i - 1]);
            if (lastMatchIndex == i - 1)
            {
                consecutiveMatches++;
                score -= consecutiveMatches * 5;
            }
            else
            {
                consecutiveMatches = 0;
                if (lastMatchIndex >= 0)
                {
                    score += (i - lastMatchIndex - 1) * 2;
                }
            }

            if (isWordBoundary)
            {
                score -= 10;
            }

            score += i * 0.1;
            lastMatchIndex = i;
            queryIndex++;
        }

        return queryIndex < query.Length
            ? new TuiFuzzyMatch(false, 0)
            : new TuiFuzzyMatch(true, score);
    }

    private static string TrySwapAlphaNumericToken(string query)
    {
        if (query.Length < 2)
        {
            return string.Empty;
        }

        var firstDigitIndex = -1;
        for (var i = 0; i < query.Length; i++)
        {
            if (IsAsciiDigit(query[i]))
            {
                firstDigitIndex = i;
                break;
            }
        }

        if (firstDigitIndex > 0 &&
            query[..firstDigitIndex].All(IsAsciiLowerLetter) &&
            query[firstDigitIndex..].All(IsAsciiDigit))
        {
            return query[firstDigitIndex..] + query[..firstDigitIndex];
        }

        var firstLetterIndex = -1;
        for (var i = 0; i < query.Length; i++)
        {
            if (IsAsciiLowerLetter(query[i]))
            {
                firstLetterIndex = i;
                break;
            }
        }

        if (firstLetterIndex > 0 &&
            query[..firstLetterIndex].All(IsAsciiDigit) &&
            query[firstLetterIndex..].All(IsAsciiLowerLetter))
        {
            return query[firstLetterIndex..] + query[..firstLetterIndex];
        }

        return string.Empty;
    }

    private static bool IsWordBoundarySeparator(char value) =>
        char.IsWhiteSpace(value) || value is '-' or '_' or '.' or '/' or ':';

    private static bool IsAsciiLowerLetter(char value) => value is >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}
