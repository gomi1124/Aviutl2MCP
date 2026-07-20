using System.Text.Json;
using System.Text.Json.Serialization;
using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Serialization;

public sealed class RevisionJsonConverter : JsonConverter<Revision>
{
    public override Revision Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        if (value is null)
        {
            throw new JsonException("Revision must be a string.");
        }

        try
        {
            return new Revision(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Revision is invalid.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, Revision value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
