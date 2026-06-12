using System.Text;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentModelListFormatter
{
    public static string Format(IReadOnlyList<Model> models, string? searchPattern = null)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (models.Count == 0)
        {
            return "No models available. Set API keys in environment variables.";
        }

        var filtered = string.IsNullOrWhiteSpace(searchPattern)
            ? models
            : models
                .Where(model => IsFuzzyMatch(searchPattern, $"{model.Provider} {model.Id} {model.Name}"))
                .ToArray();
        if (filtered.Count == 0)
        {
            return $"No models matching \"{searchPattern}\"";
        }

        var rows = filtered
            .OrderBy(static model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static model => new ModelRow(
                model.Provider,
                model.Id,
                FormatTokenCount(model.ContextWindow),
                FormatTokenCount(model.MaxOutputTokens),
                model.Reasoning ? "yes" : "no",
                model.InputModalities.Any(static modality => modality.Equals("image", StringComparison.OrdinalIgnoreCase))
                    ? "yes"
                    : "no"))
            .ToArray();

        var providerWidth = Math.Max("provider".Length, rows.Max(static row => row.Provider.Length));
        var modelWidth = Math.Max("model".Length, rows.Max(static row => row.Model.Length));
        var contextWidth = Math.Max("context".Length, rows.Max(static row => row.Context.Length));
        var maxOutputWidth = Math.Max("max-out".Length, rows.Max(static row => row.MaxOutput.Length));
        var thinkingWidth = Math.Max("thinking".Length, rows.Max(static row => row.Thinking.Length));
        var imagesWidth = Math.Max("images".Length, rows.Max(static row => row.Images.Length));

        var builder = new StringBuilder();
        builder.AppendLine(FormatRow(
            "provider",
            "model",
            "context",
            "max-out",
            "thinking",
            "images",
            providerWidth,
            modelWidth,
            contextWidth,
            maxOutputWidth,
            thinkingWidth,
            imagesWidth));
        foreach (var row in rows)
        {
            builder.AppendLine(FormatRow(
                row.Provider,
                row.Model,
                row.Context,
                row.MaxOutput,
                row.Thinking,
                row.Images,
                providerWidth,
                modelWidth,
                contextWidth,
                maxOutputWidth,
                thinkingWidth,
                imagesWidth));
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static string FormatRow(
        string provider,
        string model,
        string context,
        string maxOutput,
        string thinking,
        string images,
        int providerWidth,
        int modelWidth,
        int contextWidth,
        int maxOutputWidth,
        int thinkingWidth,
        int imagesWidth) =>
        string.Join(
            "  ",
            provider.PadRight(providerWidth),
            model.PadRight(modelWidth),
            context.PadRight(contextWidth),
            maxOutput.PadRight(maxOutputWidth),
            thinking.PadRight(thinkingWidth),
            images.PadRight(imagesWidth));

    private static string FormatTokenCount(int? count)
    {
        if (count is null)
        {
            return "-";
        }

        if (count >= 1_000_000)
        {
            var millions = count.Value / 1_000_000d;
            return count.Value % 1_000_000 == 0 ? $"{(int)millions}M" : $"{millions:0.#}M";
        }

        if (count >= 1_000)
        {
            var thousands = count.Value / 1_000d;
            return count.Value % 1_000 == 0 ? $"{(int)thousands}K" : $"{thousands:0.#}K";
        }

        return count.Value.ToString();
    }

    private static bool IsFuzzyMatch(string? query, string? text)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var normalizedText = (text ?? string.Empty).ToLowerInvariant();
        foreach (var token in query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsSubsequence(token.ToLowerInvariant(), normalizedText))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSubsequence(string query, string text)
    {
        var index = 0;
        foreach (var ch in text)
        {
            if (index < query.Length && ch == query[index])
            {
                index++;
            }
        }

        return index == query.Length;
    }

    private readonly record struct ModelRow(
        string Provider,
        string Model,
        string Context,
        string MaxOutput,
        string Thinking,
        string Images);
}
