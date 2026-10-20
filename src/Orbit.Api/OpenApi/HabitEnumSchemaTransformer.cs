using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Api.OpenApi;

internal sealed class HabitEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (context.ParameterDescription is not null
            && (type == typeof(ScheduledReminderTime) || type == typeof(RelativeReminderTime)))
        {
            SetInputIdentity(schema, type);
            return Task.CompletedTask;
        }
        if (type != typeof(FrequencyUnit) && type != typeof(DayOfWeek) && type != typeof(InstanceStatus) && type != typeof(ScheduledReminderWhen))
            return Task.CompletedTask;

        var names = Enum.GetNames(type);
        schema.Type = null;
        schema.Enum = null;
        schema.AnyOf =
        [
            new OpenApiSchema { Type = JsonSchemaType.String, Enum = names.Select(name => (JsonNode)JsonValue.Create(name)).ToList() },
            new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "-2147483648", Maximum = "2147483647" }
        ];

        if (context.ParameterDescription is not null && type != typeof(InstanceStatus))
        {
            SetInputIdentity(schema, type);
            schema.AnyOf.Add(new OpenApiSchema { Type = JsonSchemaType.String, Pattern = InputPattern(names, type == typeof(ScheduledReminderWhen)) });
            schema.Description = "MVC accepts case-insensitive names, surrounding whitespace, comma-separated names and signed 32-bit integers as numbers or numeric strings. Binding does not bypass habit validation.";
        }

        return Task.CompletedTask;
    }

    private static void SetInputIdentity(OpenApiSchema schema, Type type)
    {
        // https://github.com/dotnet/aspnetcore/blob/v10.0.13/src/OpenApi/src/Extensions/OpenApiSchemaExtensions.cs resolves component identities from this metadata.
        schema.Metadata ??= new Dictionary<string, object>();
        schema.Metadata["x-schema-id"] = type.Name + "Input";
    }

    private static string InputPattern(string[] names, bool reminder)
    {
        // https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Converters/Value/EnumConverter.cs trims Unicode whitespace and also accepts a trailing comma after a name.
        const string whitespace = @"[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]*";
        var name = "(?:" + string.Join('|', names.Select(value => string.Concat(value.Select(
            character => $"[{char.ToUpperInvariant(character)}{char.ToLowerInvariant(character)}]")))) + ")";
        const string magnitude = @"(?:[0-9]{1,9}|1[0-9]{9}|20[0-9]{8}|21[0-3][0-9]{7}|214[0-6][0-9]{6}|2147[0-3][0-9]{5}|21474[0-7][0-9]{4}|214748[0-2][0-9]{3}|2147483[0-5][0-9]{2}|21474836[0-3][0-9]|214748364[0-7])";
        var numeric = $"(?:[+]?0*{magnitude}|-0*(?:{magnitude}|2147483648))";
        var aliases = reminder ? "same_day|day_before|" : "";
        return $"^(?:{aliases}{whitespace}(?:{numeric}|{name}(?:{whitespace},{whitespace}{name})*(?:{whitespace},)?){whitespace})(?![\\s\\S])";
    }
}
