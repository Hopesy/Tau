using System.Text.Json.Serialization;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(WebChatStoreDocument))]
[JsonSerializable(typeof(WebChatSessionDto[]))]
[JsonSerializable(typeof(WebChatSessionDto))]
internal sealed partial class WebUiJsonContext : JsonSerializerContext
{
}

internal sealed record WebChatStoreDocument(IReadOnlyList<WebChatSessionDto> Sessions);
