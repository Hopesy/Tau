using System.Text.Json.Serialization;

namespace Tau.Mom;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DelegationRequest))]
[JsonSerializable(typeof(DelegationResult))]
internal sealed partial class MomJsonContext : JsonSerializerContext
{
}
