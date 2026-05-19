namespace Tau.Ai.Auth.OAuth;

internal static class OAuthInputParser
{
    public static (string? Code, string? State) ParseAuthorizationInput(string input)
    {
        var value = input.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return (null, null);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            return (query["code"], query["state"]);
        }

        if (value.Contains('#'))
        {
            var parts = value.Split('#', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : null);
        }

        if (value.Contains("code="))
        {
            var query = System.Web.HttpUtility.ParseQueryString(value);
            return (query["code"], query["state"]);
        }

        return (value, null);
    }
}
