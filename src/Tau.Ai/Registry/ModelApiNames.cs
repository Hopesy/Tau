namespace Tau.Ai.Registry;

internal static class ModelApiNames
{
    public const string OpenAiChatCompletions = "openai-chat-completions";
    public const string GoogleGenerativeLanguage = "google-generative-language";

    public static string? Normalize(string? api)
    {
        if (string.IsNullOrWhiteSpace(api))
        {
            return null;
        }

        return api.Trim() switch
        {
            "openai-completions" => OpenAiChatCompletions,
            "openai-compatible" => OpenAiChatCompletions,
            "google-generative-ai" => GoogleGenerativeLanguage,
            var value => value
        };
    }
}
