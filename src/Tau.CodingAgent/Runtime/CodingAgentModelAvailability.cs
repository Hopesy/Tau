using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentModelAvailability
{
    public static IReadOnlyList<Model> GetRegisteredModels(ICodingAgentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var models = new List<Model>();
        foreach (var provider in runner.GetProviders())
        {
            models.AddRange(runner.GetModels(provider));
        }

        return models;
    }

    public static IReadOnlyList<Model> GetAuthConfiguredModels(
        ICodingAgentRunner runner,
        IReadOnlyList<Model>? registeredModels = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var source = registeredModels ?? GetRegisteredModels(runner);
        var providerStatuses = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var models = new List<Model>();
        foreach (var model in source)
        {
            if (!providerStatuses.TryGetValue(model.Provider, out var isConfigured))
            {
                isConfigured = runner.GetAuthStatus(model.Provider).IsConfigured;
                providerStatuses[model.Provider] = isConfigured;
            }

            if (isConfigured)
            {
                models.Add(model);
            }
        }

        return models;
    }

    public static string FormatModelId(Model model) => $"{model.Provider}/{model.Id}";
}
