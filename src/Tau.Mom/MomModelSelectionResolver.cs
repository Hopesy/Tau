using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;

namespace Tau.Mom;

internal sealed class MomModelSelectionResolver
{
    private readonly MomOptions _options;
    private readonly ModelCatalog _catalog = new();

    public MomModelSelectionResolver(MomOptions options)
    {
        _options = options;
    }

    public ResolvedModelSelection Resolve(string? provider, string? model, string? workingDirectory = null)
    {
        var requestedProvider = NormalizeOptional(provider);
        var requestedModel = NormalizeOptional(model);
        if (requestedProvider is null && requestedModel is null)
        {
            var session = LoadSessionMetadata(workingDirectory);
            if (session.HasModelSelection)
            {
                return ResolveExplicit(session.Provider, session.Model);
            }
        }

        return ResolveExplicit(requestedProvider, requestedModel);
    }

    private ResolvedModelSelection ResolveExplicit(string? provider, string? model)
    {
        var defaultProvider = string.IsNullOrWhiteSpace(_options.DefaultProvider)
            ? RuntimeCodingAgentRunner.GetDefaultProviderId()
            : _options.DefaultProvider.Trim();
        var defaultModel = string.IsNullOrWhiteSpace(_options.DefaultModel)
            ? null
            : _options.DefaultModel.Trim();

        if (string.IsNullOrWhiteSpace(provider) &&
            string.IsNullOrWhiteSpace(model) &&
            !string.IsNullOrWhiteSpace(defaultModel))
        {
            return _catalog.ResolveSelection(defaultProvider, defaultModel, defaultProvider);
        }

        if (string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(model))
        {
            return _catalog.ResolveSelection(defaultProvider, null, defaultProvider);
        }

        if (!string.IsNullOrWhiteSpace(provider) && provider.Trim().Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedModel = string.IsNullOrWhiteSpace(model)
                ? ModelCatalog.GetDefaultModelId("google-gemini-cli")
                : model.Trim();
            return _catalog.ResolveSelection("google-gemini-cli", normalizedModel, defaultProvider);
        }

        return _catalog.ResolveSelection(provider, model, defaultProvider);
    }

    private static ChannelSessionMetadata LoadSessionMetadata(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return default;
        }

        return new ChannelSessionStore(workingDirectory).LoadMetadata();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
