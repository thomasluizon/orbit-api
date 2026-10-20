using System.Reflection;
using System.Numerics;
using System.Globalization;
using Xunit.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class HabitEnumContractTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Mvc = ReminderInputContractTests.MvcOptions();
    private static readonly Type[] RequestTypes =
    [
        typeof(HabitsController.CreateHabitRequest), typeof(HabitsController.UpdateHabitRequest),
        typeof(HabitsController.BulkHabitItemRequest), typeof(HabitsController.CreateSubHabitRequest),
        typeof(ApplyHabitInput)
    ];
    private static readonly Type[] ResponseTypes =
    [
        typeof(AiAction), typeof(HabitDetailResponse), typeof(HabitChildResponse),
        typeof(HabitScheduleItem), typeof(HabitScheduleChildItem), typeof(SyncController.SyncHabitDto),
        typeof(HabitInstanceItem)
    ];

    [Theory]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void OpenApi_ResponseEnumsMatchActualMvcIncludingNumericFallback(string file)
    {
        using var document = ReadDocument(file);
        foreach (var type in new[] { typeof(FrequencyUnit), typeof(DayOfWeek), typeof(InstanceStatus), typeof(ScheduledReminderWhen) })
        {
            var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(type.Name);
            var values = Enum.GetValues(type).Cast<object>().Concat([Enum.ToObject(type, -1), Enum.ToObject(type, 99), Enum.ToObject(type, int.MinValue), Enum.ToObject(type, int.MaxValue)]);
            foreach (var value in values)
                Allows(schema, JsonSerializer.SerializeToElement(value, type, Mvc), document.RootElement)
                    .Should().BeTrue($"{file}/{type.Name} must accept actual MVC output {value}");
            var names = schema.GetProperty("anyOf").EnumerateArray()
                .Single(branch => branch.TryGetProperty("enum", out _)).GetProperty("enum");
            names.EnumerateArray().Select(name => name.GetString()).Should().Equal(
                Enum.GetValues(type).Cast<object>().Select(value => JsonSerializer.SerializeToElement(value, type, Mvc).GetString()));
            foreach (var token in new[] { "\"unknown\"", "\"day\"", "\" Monday \"", "\"0\"", "true", "null", "1.5" })
                Allows(schema, JsonSerializer.Deserialize<JsonElement>(token), document.RootElement).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void OpenApi_ActualResponseDtosAndNestedEnumsMatchEveryPublishedReference(string file)
    {
        using var document = ReadDocument(file);
        foreach (var number in Enumerable.Range(0, 7).Concat([-1, 99]))
            foreach (var type in ResponseTypes)
            {
                var response = CreateResponse(type, number);
                var json = JsonSerializer.SerializeToElement(response, type, Mvc);
                AssertEnumProperties(json, document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(type.Name), document.RootElement);
            }
    }

    [Theory]
    [MemberData(nameof(AcceptedInputs))]
    public void OpenApi_RequestSchemasAcceptActualBoundForms(string frequency, string days)
    {
        var json = RequestJson(frequency, days);
        foreach (var file in new[] { "openapi_v1-r2.json", "openapi_v2-r2.json" })
        {
            using var document = ReadDocument(file);
            foreach (var type in RequestTypes)
            {
                JsonSerializer.Deserialize(json, type, Mvc).Should().NotBeNull();
                AssertEnumProperties(JsonSerializer.Deserialize<JsonElement>(json),
                    document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(type.Name), document.RootElement);
            }
            foreach (var (name, envelope) in new[]
            {
                ("BulkCreateHabitsRequest", $$"""{"habits":[{{json[..^1]}},"subHabits":[{{json}}]}]}"""),
                ("ApplyOnboardingRequest", $$"""{"habits":[{{json}}]}""")
            })
                AssertEnumProperties(JsonSerializer.Deserialize<JsonElement>(envelope),
                    document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name), document.RootElement);
        }
    }

    private static JsonDocument ReadDocument(string file) => JsonDocument.Parse(File.ReadAllText(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", file))));

    private static JsonElement Resolve(JsonElement schema, JsonElement document) => schema.TryGetProperty("$ref", out var reference)
        ? document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1])
        : schema;

    private static bool Allows(JsonElement schema, JsonElement value, JsonElement document)
    {
        schema = Resolve(schema, document);
        if (schema.TryGetProperty("anyOf", out var any) && !any.EnumerateArray().Any(part => Allows(part, value, document))) return false;
        if (schema.TryGetProperty("oneOf", out var one) && one.EnumerateArray().Count(part => Allows(part, value, document)) != 1) return false;
        if (schema.TryGetProperty("enum", out var names) && !names.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value))) return false;
        if (schema.TryGetProperty("pattern", out var pattern) && value.ValueKind == JsonValueKind.String
            && !Regex.IsMatch(value.GetString()!, pattern.GetString()!, RegexOptions.ECMAScript)) return false;
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = Number(value);
            if (schema.TryGetProperty("minimum", out var minimum) && number.CompareTo(Number(minimum)) < 0) return false;
            if (schema.TryGetProperty("maximum", out var maximum) && number.CompareTo(Number(maximum)) > 0) return false;
        }
        if (!schema.TryGetProperty("type", out var kind)) return true;
        var actual = value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => IsInteger(value) ? "integer" : "number",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => "boolean"
        };
        bool Matches(JsonElement item) => item.ValueEquals(actual) || actual == "integer" && item.ValueEquals("number");
        return kind.ValueKind == JsonValueKind.Array ? kind.EnumerateArray().Any(Matches) : Matches(kind);
    }

    private readonly record struct SchemaNumber(string Digits, BigInteger Exponent, bool Negative)
    {
        private int Sign => Digits == "0" ? 0 : Negative ? -1 : 1;
        internal bool IsInteger => Sign == 0 || Exponent.Sign >= 0;

        internal int CompareTo(SchemaNumber other)
        {
            if (Sign != other.Sign) return Sign.CompareTo(other.Sign);
            if (Sign == 0) return 0;
            var magnitude = (Exponent + Digits.Length).CompareTo(other.Exponent + other.Digits.Length);
            if (magnitude != 0) return Sign * magnitude;
            var width = Math.Max(Digits.Length, other.Digits.Length);
            return Sign * string.CompareOrdinal(Digits.PadRight(width, '0'), other.Digits.PadRight(width, '0'));
        }
    }

    private static SchemaNumber Number(JsonElement value)
    {
        var parts = value.GetRawText().Split(['e', 'E']);
        var fraction = parts[0].IndexOf('.');
        var exponent = (parts.Length == 2 ? BigInteger.Parse(parts[1], CultureInfo.InvariantCulture) : BigInteger.Zero)
            - (fraction < 0 ? 0 : parts[0].Length - fraction - 1);
        var digits = parts[0].Replace(".", "", StringComparison.Ordinal).TrimStart('-').TrimStart('0');
        if (digits.Length == 0) return new SchemaNumber("0", BigInteger.Zero, false);
        var significant = digits.TrimEnd('0');
        return new SchemaNumber(significant, exponent + digits.Length - significant.Length, parts[0][0] == '-');
    }

    private static bool IsInteger(JsonElement value) => Number(value).IsInteger;

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("\"2147483648\"")]
    [InlineData("\"-2147483649\"")]
    [InlineData("\"+0002147483648\"")]
    [InlineData("\"-0002147483649\"")]
    public void OpenApi_NumericOverflowIsRejectedBySchemaAndMvcSeparately(string token)
    {
        foreach (var file in new[] { "openapi_v1-r2.json", "openapi_v2-r2.json" })
        {
            using var document = ReadDocument(file);
            foreach (var name in new[] { "FrequencyUnitInput", "DayOfWeekInput", "ScheduledReminderWhenInput" })
                Allows(document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name),
                    JsonSerializer.Deserialize<JsonElement>(token), document.RootElement).Should().BeFalse($"{file}/{name} must reject {token}");
        }
        foreach (var type in RequestTypes)
        {
            Action frequency = () => JsonSerializer.Deserialize(RequestJson(token, "[]"), type, Mvc);
            Action days = () => JsonSerializer.Deserialize(RequestJson("null", $"[{token}]"), type, Mvc);
            frequency.Should().Throw<JsonException>();
            days.Should().Throw<JsonException>();
        }
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    public void JsonSchema_IntegerMeansNumericValueWhileMvcRequiresIntegerToken(string token)
    {
        using var document = ReadDocument("openapi_v1-r2.json");
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("FrequencyUnitInput");
        Allows(schema, JsonSerializer.Deserialize<JsonElement>(token), document.RootElement).Should().BeTrue();
        Action bind = () => JsonSerializer.Deserialize(RequestJson(token, "[]"), typeof(HabitsController.CreateHabitRequest), Mvc);
        bind.Should().Throw<JsonException>();
    }

    private static void AssertEnumProperties(JsonElement value, JsonElement schema, JsonElement document)
    {
        schema = Resolve(schema, document);
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) AssertEnumProperties(item, schema.GetProperty("items"), document);
        }
        if (value.ValueKind != JsonValueKind.Object) return;
        if (!value.EnumerateObject().Any()) return;
        var properties = schema.GetProperty("properties");
        foreach (var property in value.EnumerateObject())
        {
            var childSchema = properties.GetProperty(property.Name);
            if (property.Name is "frequencyUnit" or "status" or "when")
                Allows(childSchema, property.Value, document).Should().BeTrue($"{property.Name}={property.Value.GetRawText()} must match its published schema");
            if (property.Name == "days" && property.Value.ValueKind == JsonValueKind.Array)
                foreach (var day in property.Value.EnumerateArray())
                    Allows(childSchema.GetProperty("items"), day, document).Should().BeTrue($"days item {day} must match its published schema");
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                AssertEnumProperties(property.Value, childSchema, document);
        }
    }

    private static object CreateResponse(Type type, int number, int depth = 0)
    {
        if (type == typeof(AiAction)) return new AiAction { FrequencyUnit = (FrequencyUnit)number, Days = [(DayOfWeek)number] };
        var constructor = type.GetConstructors().Single();
        return constructor.Invoke(constructor.GetParameters().Select(parameter => ResponseArgument(parameter, number, depth)).ToArray());
    }

    private static object? ResponseArgument(ParameterInfo parameter, int number, int depth)
    {
        var type = parameter.ParameterType;
        if (parameter.Name == "FrequencyUnit") return (FrequencyUnit)number;
        if (parameter.Name == "Status") return (InstanceStatus)number;
        if (parameter.Name == "Days") return new[] { (DayOfWeek)number };
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = type.GetGenericArguments()[0];
            var nested = parameter.Name == "Instances" || parameter.Name == "Children" && depth == 0;
            var array = Array.CreateInstance(element, nested ? 1 : 0);
            if (nested) array.SetValue(CreateResponse(element, number, depth + 1), 0);
            return array;
        }
        return parameter.HasDefaultValue ? parameter.DefaultValue : type == typeof(string) ? "Demo"
            : type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
