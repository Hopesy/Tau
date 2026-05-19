using System.Text;
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth.Providers;

internal static class GoogleProjectDiscovery
{
    private const string CodeAssistEndpoint = "https://cloudcode-pa.googleapis.com";

    public static async Task<string> DiscoverProjectAsync(
        string accessToken, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var envProjectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
            ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT_ID");

        using var client = new HttpClient();
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {accessToken}",
            ["Content-Type"] = "application/json",
            ["User-Agent"] = "google-api-nodejs-client/9.15.1",
            ["X-Goog-Api-Client"] = "gl-node/22.17.0"
        };

        onProgress?.Invoke("Checking for existing Cloud Code Assist project...");

        var loadBody = BuildLoadBody(envProjectId);
        var loadResponse = await PostJsonAsync(client, $"{CodeAssistEndpoint}/v1internal:loadCodeAssist", loadBody, headers, cancellationToken).ConfigureAwait(false);

        if (loadResponse.IsSuccess)
        {
            using var doc = JsonDocument.Parse(loadResponse.Body);
            var root = doc.RootElement;

            if (root.TryGetProperty("currentTier", out _))
            {
                if (root.TryGetProperty("cloudaicompanionProject", out var projectProp) &&
                    projectProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(projectProp.GetString()))
                {
                    return projectProp.GetString()!;
                }

                if (!string.IsNullOrEmpty(envProjectId)) return envProjectId;

                throw new InvalidOperationException(
                    "This account requires setting GOOGLE_CLOUD_PROJECT or GOOGLE_CLOUD_PROJECT_ID environment variable.");
            }

            onProgress?.Invoke("Provisioning Cloud Code Assist project...");
            return await OnboardUserAsync(client, headers, envProjectId, onProgress, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(envProjectId)) return envProjectId;

        throw new InvalidOperationException(
            $"loadCodeAssist failed: {loadResponse.StatusCode} {loadResponse.Body}");
    }

    public static async Task<string?> GetUserEmailAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v1/userinfo?alt=json");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> OnboardUserAsync(
        HttpClient client, Dictionary<string, string> headers, string? envProjectId,
        Action<string>? onProgress, CancellationToken cancellationToken)
    {
        var onboardBody = BuildOnboardBody(envProjectId);
        var onboardResponse = await PostJsonAsync(client, $"{CodeAssistEndpoint}/v1internal:onboardUser", onboardBody, headers, cancellationToken).ConfigureAwait(false);

        if (!onboardResponse.IsSuccess)
        {
            throw new InvalidOperationException($"onboardUser failed: {onboardResponse.StatusCode} {onboardResponse.Body}");
        }

        using var doc = JsonDocument.Parse(onboardResponse.Body);
        var root = doc.RootElement;

        if (root.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
        {
            return ExtractProjectId(root, envProjectId);
        }

        if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
        {
            var operationName = nameProp.GetString()!;
            return await PollOperationAsync(client, operationName, headers, envProjectId, onProgress, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(envProjectId)) return envProjectId;
        throw new InvalidOperationException("Could not discover or provision a Google Cloud project.");
    }

    private static async Task<string> PollOperationAsync(
        HttpClient client, string operationName, Dictionary<string, string> headers, string? envProjectId,
        Action<string>? onProgress, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (attempt > 0)
            {
                onProgress?.Invoke($"Waiting for project provisioning (attempt {attempt + 1})...");
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{CodeAssistEndpoint}/v1internal/{operationName}");
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Failed to poll operation: {response.StatusCode} {body}");
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean())
            {
                return ExtractProjectId(doc.RootElement, envProjectId);
            }
        }

        if (!string.IsNullOrEmpty(envProjectId)) return envProjectId;
        throw new TimeoutException("Project provisioning timed out.");
    }

    private static string ExtractProjectId(JsonElement root, string? envProjectId)
    {
        if (root.TryGetProperty("response", out var responseProp) &&
            responseProp.TryGetProperty("cloudaicompanionProject", out var projectProp) &&
            projectProp.TryGetProperty("id", out var idProp) &&
            idProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            return idProp.GetString()!;
        }

        if (!string.IsNullOrEmpty(envProjectId)) return envProjectId;
        throw new InvalidOperationException("Could not discover or provision a Google Cloud project.");
    }

    private static string BuildLoadBody(string? projectId)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        if (!string.IsNullOrEmpty(projectId))
        {
            writer.WriteString("cloudaicompanionProject", projectId);
        }
        writer.WriteStartObject("metadata");
        writer.WriteString("ideType", "IDE_UNSPECIFIED");
        writer.WriteString("platform", "PLATFORM_UNSPECIFIED");
        writer.WriteString("pluginType", "GEMINI");
        if (!string.IsNullOrEmpty(projectId))
        {
            writer.WriteString("duetProject", projectId);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildOnboardBody(string? projectId)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("tierId", "free-tier");
        writer.WriteStartObject("metadata");
        writer.WriteString("ideType", "IDE_UNSPECIFIED");
        writer.WriteString("platform", "PLATFORM_UNSPECIFIED");
        writer.WriteString("pluginType", "GEMINI");
        if (!string.IsNullOrEmpty(projectId))
        {
            writer.WriteString("duetProject", projectId);
        }
        writer.WriteEndObject();
        if (!string.IsNullOrEmpty(projectId))
        {
            writer.WriteString("cloudaicompanionProject", projectId);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<HttpResult> PostJsonAsync(
        HttpClient client, string url, string jsonBody, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        foreach (var header in headers)
        {
            if (!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResult(response.IsSuccessStatusCode, (int)response.StatusCode, body);
    }

    private sealed record HttpResult(bool IsSuccess, int StatusCode, string Body);
}
