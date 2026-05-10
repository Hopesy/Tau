using System.Text.Json.Serialization;

namespace Tau.Mom;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DelegationRequest))]
[JsonSerializable(typeof(DelegationResult))]
[JsonSerializable(typeof(DelegationToolEvent))]
[JsonSerializable(typeof(DelegationUsage))]
[JsonSerializable(typeof(ChannelStatus))]
[JsonSerializable(typeof(MomEventFile))]
[JsonSerializable(typeof(ChannelPromptDebugContext))]
[JsonSerializable(typeof(ChannelPromptDebugMessage))]
[JsonSerializable(typeof(ChannelPromptDebugContent))]
internal sealed partial class MomJsonContext : JsonSerializerContext
{
}
