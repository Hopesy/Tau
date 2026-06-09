using System.Text.Json.Serialization;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(WebChatStoreDocument))]
[JsonSerializable(typeof(WebChatSessionDto[]))]
[JsonSerializable(typeof(WebChatSessionDto))]
[JsonSerializable(typeof(WebChatSessionSourceMetadataDto))]
[JsonSerializable(typeof(WebChatMessageDto))]
[JsonSerializable(typeof(WebChatAttachmentDto))]
[JsonSerializable(typeof(WebChatAttachmentDto[]))]
[JsonSerializable(typeof(WebArtifactStoreDocument))]
[JsonSerializable(typeof(WebArtifactSessionDocument))]
[JsonSerializable(typeof(WebArtifactDto))]
[JsonSerializable(typeof(WebArtifactDto[]))]
[JsonSerializable(typeof(WebArtifactSummaryDto))]
[JsonSerializable(typeof(WebArtifactSummaryDto[]))]
[JsonSerializable(typeof(UpsertWebArtifactRequest))]
[JsonSerializable(typeof(WebRuntimeMessageRequest))]
[JsonSerializable(typeof(WebJavaScriptReplRequestDto))]
[JsonSerializable(typeof(WebJavaScriptReplResultRequest))]
[JsonSerializable(typeof(WebJavaScriptReplResultDto))]
[JsonSerializable(typeof(WebJavaScriptReplFileDto))]
[JsonSerializable(typeof(WebJavaScriptReplFileDto[]))]
[JsonSerializable(typeof(IReadOnlyList<WebJavaScriptReplFileDto>))]
[JsonSerializable(typeof(WebJavaScriptReplToolDetails))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(WebChatToolCallDto))]
[JsonSerializable(typeof(WebChatToolCallDto[]))]
[JsonSerializable(typeof(CodingAgentJsonlSessionPreviewDto))]
[JsonSerializable(typeof(CodingAgentJsonlPreviewFilterDto))]
[JsonSerializable(typeof(CodingAgentJsonlTreeMetadataDto))]
[JsonSerializable(typeof(CodingAgentJsonlEntryMetadataDto))]
[JsonSerializable(typeof(CodingAgentJsonlImportAuditDto))]
[JsonSerializable(typeof(CodingAgentJsonlBranchTimelineEntryDto))]
[JsonSerializable(typeof(CodingAgentJsonlBranchLabelDto))]
[JsonSerializable(typeof(CodingAgentJsonlAuditWarningDto))]
[JsonSerializable(typeof(CodingAgentJsonlImportStrategyDto))]
[JsonSerializable(typeof(IReadOnlyList<CodingAgentJsonlAuditWarningDto>))]
[JsonSerializable(typeof(CodingAgentJsonlImportResultDto))]
[JsonSerializable(typeof(CodingAgentJsonlImportResultSummaryDto))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, int>))]
[JsonSerializable(typeof(CodingAgentJsonlTimelineMessageDto))]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(WebChatStreamEventDto))]
[JsonSerializable(typeof(WebUiAuthStatusDto))]
internal sealed partial class WebUiJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebChatStreamEventDto))]
[JsonSerializable(typeof(WebChatToolCallDto))]
internal sealed partial class WebUiNdjsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebChatJsonlSessionHeader))]
[JsonSerializable(typeof(WebChatSessionSourceMetadataDto))]
[JsonSerializable(typeof(WebChatJsonlMessageEntry))]
internal sealed partial class WebUiJsonlContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodingAgentJsonlSessionHeader))]
[JsonSerializable(typeof(CodingAgentJsonlSessionEntry))]
[JsonSerializable(typeof(CodingAgentJsonlSessionMessage))]
[JsonSerializable(typeof(CodingAgentJsonlSessionContent))]
internal sealed partial class WebUiCodingAgentJsonlContext : JsonSerializerContext
{
}

internal sealed record WebChatStoreDocument(IReadOnlyList<WebChatSessionDto> Sessions);
