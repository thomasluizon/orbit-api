using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Orbit.Domain.Enums;

namespace Orbit.Api.OpenApi;

internal sealed class HabitEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (type != typeof(FrequencyUnit) && type != typeof(DayOfWeek) && type != typeof(InstanceStatus))
            return Task.CompletedTask;

        var names = Enum.GetNames(type);
        schema.Type = null;
        schema.AnyOf =
        [
            new OpenApiSchema { Type = JsonSchemaType.String, Enum = names.Select(name => (JsonNode)JsonValue.Create(name)).ToList() },
            new OpenApiSchema { Type = JsonSchemaType.Integer }
        ];

        if (context.ParameterDescription is not null && type != typeof(InstanceStatus))
        {
            // https://github.com/dotnet/aspnetcore/blob/v10.0.13/src/OpenApi/src/Extensions/OpenApiSchemaExtensions.cs uses this metadata to distinguish component identities before resolving references.
            schema.Metadata ??= new Dictionary<string, object>();
            schema.Metadata["x-schema-id"] = type.Name + "Input";
            schema.AnyOf.Add(new OpenApiSchema { Type = JsonSchemaType.String, Pattern = InputPattern(names) });
            schema.Description = "MVC accepts case-insensitive names, surrounding whitespace, comma-separated names and signed 32-bit integers as numbers or numeric strings. Binding does not bypass habit validation.";
        }

        return Task.CompletedTask;
    }

    private static string InputPattern(string[] names)
    {
        // https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Converters/Value/EnumConverter.cs trims Unicode whitespace and also accepts a trailing comma after a name.
        const string whitespace = @"[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]*";
        var name = "(?:" + string.Join('|', names.Select(value => string.Concat(value.Select(
            character => $"[{char.ToUpperInvariant(character)}{char.ToLowerInvariant(character)}]")))) + ")";
        return $"^{whitespace}(?:[+-]?[0-9]+|{name}(?:{whitespace},{whitespace}{name})*(?:{whitespace},)?){whitespace}$";
    }
}
