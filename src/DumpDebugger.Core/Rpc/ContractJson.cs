using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDebugger.Core.Rpc;

/// <summary>
/// The single serializer configuration for every contract that crosses a process or bridge
/// boundary, and for workspace files. camelCase members and camelCase string enums, matching
/// the generated TypeScript in <c>web/src/contracts.gen.ts</c>.
/// </summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);

    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } e
            ? e.Deserialize<T>(Options)
            : default;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
