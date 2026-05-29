using System.Net.Http.Headers;

namespace Tau.Ai;

public static class AiHeaderUtilities
{
    public static IReadOnlyDictionary<string, string> ToDictionary(HttpHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            result[header.Key] = string.Join(", ", header.Value);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> ToDictionary(
        HttpResponseMessage response,
        bool includeContentHeaders = true)
    {
        ArgumentNullException.ThrowIfNull(response);

        var result = new Dictionary<string, string>(ToDictionary(response.Headers), StringComparer.OrdinalIgnoreCase);
        if (includeContentHeaders && response.Content is not null)
        {
            foreach (var (key, value) in ToDictionary(response.Content.Headers))
            {
                result[key] = value;
            }
        }

        return result;
    }
}
