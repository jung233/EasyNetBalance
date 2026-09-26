using System.Text.Json.Serialization;

namespace EasyBalance.Shared;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(PipeResponse))]
public partial class IpcJsonContext : JsonSerializerContext { }
