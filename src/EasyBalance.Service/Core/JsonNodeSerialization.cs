using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EasyBalance.Service.Core;

internal static class JsonNodeSerialization
{
    // JsonNode.ToJsonString marks the options read-only. Published single-file builds
    // require an explicit resolver before that happens.
    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static readonly JsonSerializerOptions Compact = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}
