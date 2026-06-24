namespace Tau.Ai;

public static class ProviderEnvironment
{
    public static string? GetValue(string name, IReadOnlyDictionary<string, string>? env = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (TryGetScopedValue(name, env, out var scoped))
        {
            return scoped;
        }

        var ambient = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(ambient) ? null : ambient;
    }

    public static IReadOnlyDictionary<string, string>? Merge(
        IReadOnlyDictionary<string, string>? configured,
        IReadOnlyDictionary<string, string>? explicitValues)
    {
        if (configured is null || configured.Count == 0)
        {
            return explicitValues is null || explicitValues.Count == 0
                ? null
                : new Dictionary<string, string>(explicitValues, StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
        if (explicitValues is not null)
        {
            foreach (var (key, value) in explicitValues)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static bool TryGetScopedValue(
        string name,
        IReadOnlyDictionary<string, string>? env,
        out string? value)
    {
        value = null;
        if (env is null)
        {
            return false;
        }

        if (!env.TryGetValue(name, out value))
        {
            foreach (var (key, candidate) in env)
            {
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = null;
            return false;
        }

        return true;
    }
}
