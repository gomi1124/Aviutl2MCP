using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace AviUtl2MCP.Server.Schema;

public static class McpToolRegistrationExtensions
{
    public static IMcpServerBuilder WithToolsUsingSchema<TToolType>(
        this IMcpServerBuilder builder,
        JsonSerializerOptions serializerOptions,
        AIJsonSchemaCreateOptions schemaCreateOptions)
        where TToolType : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(schemaCreateOptions);

        MethodInfo[] methods = typeof(TToolType).GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (MethodInfo method in methods)
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>(inherit: false) is null)
            {
                continue;
            }

            builder.Services.AddSingleton<McpServerTool>(services =>
            {
                object? target = method.IsStatic
                    ? null
                    : ActivatorUtilities.CreateInstance<TToolType>(services);
                McpServerTool tool = McpServerTool.Create(
                    method,
                    target,
                    new McpServerToolCreateOptions
                    {
                        Services = services,
                        SerializerOptions = serializerOptions,
                        SchemaCreateOptions = schemaCreateOptions,
                    });
                tool.ProtocolTool.InputSchema = ToolSchemaOptions.ApplyEffectItemValueSchemas(
                    method,
                    tool.ProtocolTool.InputSchema,
                    serializerOptions);
                return tool;
            });
        }

        return builder;
    }
}
