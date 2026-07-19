using System.Text.Json;
using System.Text.Json.Serialization;

namespace AviUtl2MCP.Application.Serialization;

public static class ContractJsonSerializer
{
    private static readonly JsonSerializerOptions SERIALIZER_OPTIONS = CreateOptions();

    public static string SerializeContract<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, SERIALIZER_OPTIONS);
    }

    public static string SerializeContract(object value, Type inputType)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(inputType);
        return JsonSerializer.Serialize(value, inputType, SERIALIZER_OPTIONS);
    }

    public static T DeserializeContract<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, SERIALIZER_OPTIONS)
            ?? throw new JsonException($"JSON did not contain a {typeof(T).Name} value.");
    }

    public static object DeserializeContract(string json, Type returnType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(returnType);
        return JsonSerializer.Deserialize(json, returnType, SERIALIZER_OPTIONS)
            ?? throw new JsonException($"JSON did not contain a {returnType.Name} value.");
    }

    public static JsonSerializerOptions CreateSerializerOptions() =>
        new(SERIALIZER_OPTIONS);

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new RevisionJsonConverter());
        return options;
    }
}
