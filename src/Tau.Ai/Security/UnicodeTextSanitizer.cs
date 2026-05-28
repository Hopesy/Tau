using System.Text;

namespace Tau.Ai;

internal static class UnicodeTextSanitizer
{
    public static string RemoveUnpairedSurrogates(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (char.IsHighSurrogate(current))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder?.Append(current);
                    builder?.Append(text[++i]);
                    continue;
                }

                builder ??= new StringBuilder(text.Length).Append(text, 0, i);
                continue;
            }

            if (char.IsLowSurrogate(current))
            {
                builder ??= new StringBuilder(text.Length).Append(text, 0, i);
                continue;
            }

            builder?.Append(current);
        }

        return builder?.ToString() ?? text;
    }
}
