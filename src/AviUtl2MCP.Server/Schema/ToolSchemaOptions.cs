using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using Microsoft.Extensions.AI;

namespace AviUtl2MCP.Server.Schema;

public static class ToolSchemaOptions
{
    public static AIJsonSchemaCreateOptions Create()
    {
        return new AIJsonSchemaCreateOptions();
    }

    public static JsonElement ApplyEffectItemValueSchemas(
        MethodInfo method,
        JsonElement inputSchema,
        JsonSerializerOptions serializerOptions)
    {
        ParameterInfo[] parameters = method.GetParameters()
            .Where(parameter =>
                parameter.GetCustomAttribute<EffectItemValueAttribute>(inherit: false) is not null)
            .ToArray();
        if (parameters.Length == 0)
        {
            return inputSchema;
        }

        JsonObject schema = JsonNode.Parse(inputSchema.GetRawText())!.AsObject();
        JsonObject properties = schema["properties"]!.AsObject();
        foreach (ParameterInfo parameter in parameters)
        {
            string parameterName = parameter.Name
                ?? throw new InvalidOperationException("Tool parameter name is unavailable.");
            string jsonName = serializerOptions.PropertyNamingPolicy?.ConvertName(parameterName)
                ?? parameterName;
            properties[jsonName] = CreateEffectItemValueSchema();
        }
        return JsonSerializer.SerializeToElement(schema, serializerOptions);
    }

    private static JsonObject CreateEffectItemValueSchema()
    {
        return new JsonObject
        {
            ["oneOf"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "boolean",
                },
                new JsonObject
                {
                    ["type"] = "integer",
                },
                new JsonObject
                {
                    ["type"] = "number",
                    ["not"] = new JsonObject
                    {
                        ["type"] = "integer",
                    },
                },
                new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = 65_536,
                },
            },
        };
    }
}
