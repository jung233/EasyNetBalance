using System.Text.Json.Serialization;

namespace EasyBalance.Shared;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(RoutingPolicy))]
[JsonSerializable(typeof(ApplicationRule))]
[JsonSerializable(typeof(ProbeEndpoint))]
[JsonSerializable(typeof(NetworkAdapterInfo))]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(PipeResponse))]
[JsonSerializable(typeof(List<RoutingPolicy>))]
[JsonSerializable(typeof(List<ApplicationRule>))]
[JsonSerializable(typeof(List<ProbeEndpoint>))]
[JsonSerializable(typeof(List<NetworkAdapterInfo>))]
public partial class EasyBalanceJsonContext : JsonSerializerContext
{
}
