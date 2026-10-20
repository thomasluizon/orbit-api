using System.Text.Json;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Api.OpenApi;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class HabitEnumContractTests
{
    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void LegacyDocuments_ExplicitlyRetainKnownInaccuracies(string file)
    {
        using var document = ReadDocument(file);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var type in new[] { typeof(FrequencyUnit), typeof(DayOfWeek), typeof(InstanceStatus) })
        {
            var schema = schemas.GetProperty(type.Name);
            schema.EnumerateObject().Select(p => p.Name).Should().Equal("type");
            schema.GetProperty("type").GetString().Should().Be("integer");
            foreach (var value in Enum.GetValues(type))
                Allows(schema, JsonSerializer.SerializeToElement(value, type, Mvc), document.RootElement).Should().BeFalse();
        }
        var reminder = schemas.GetProperty("ScheduledReminderWhen");
        reminder.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("enum", "description");
        reminder.GetProperty("enum").EnumerateArray().Select(v => v.GetString()).Should().Equal("same_day", "day_before");
        foreach (var when in Enum.GetValues<ScheduledReminderWhen>())
            Allows(reminder, JsonSerializer.SerializeToElement(when, Mvc), document.RootElement).Should().BeFalse();
    }

    public static IEnumerable<object[]> ReminderForms()
    {
        foreach (var type in new[] { typeof(ScheduledReminderWhen) })
        {
            foreach (var name in Enum.GetNames(type))
                foreach (var form in new[] { name, name.ToLowerInvariant(), name.ToUpperInvariant(), name + ",", name + ", SameDay" })
                    yield return [JsonSerializer.Serialize(form), true];
        }
        foreach (var whitespace in "\t\n\v\f\r \u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000")
            yield return [JsonSerializer.Serialize(whitespace + "SameDay" + whitespace), true];
        foreach (var token in new[] { "0", "1", "99", "-1", "2147483647", "-2147483648", "\"+0002147483647\"", "\"-0002147483648\"", "\"-000\"", "\"same_day\"", "\"day_before\"" })
            yield return [token, true];
        foreach (var token in new[] { "\"SAME_DAY\"", "\"DAY_BEFORE\"", "\" same_day \"", "\"same_day,DayBefore\"", "\"unknown\"", "\"SameDay,,DayBefore\"", "\"1,2\"", "\"2147483648\"", "\"-2147483649\"", "2147483648", "-2147483649", "true", "{}", "1.5", "\"\"", "\"0x1\"", "\"1.0\"" })
            yield return [token, false];
    }

    [Theory]
    [MemberData(nameof(ReminderForms))]
    public void CorrectedReminderSchemas_MatchActualRequestRecords(string token, bool accepted)
    {
        var json = $$"""{"title":"Demo","scheduledReminders":[{"when":{{token}},"time":"08:00"}],"relativeReminders":[{"when":{{token}},"time":"08:00"},{"minutesBefore":15,"when":null,"time":null}]}""";
        foreach (var type in RequestTypes.Where(t => t.GetProperty("ScheduledReminders") is not null))
        {
            Action bind = () => JsonSerializer.Deserialize(json, type, Mvc);
            if (accepted) bind.Should().NotThrow();
            else bind.Should().Throw<JsonException>();
            foreach (var artifact in OpenApiArtifact.All.Where(a => a.Revision == "r2"))
            {
                using var document = ReadDocument(Path.GetFileName(artifact.Path));
                var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
                var input = schemas.GetProperty("ScheduledReminderWhenInput");
                Allows(input, JsonSerializer.Deserialize<JsonElement>(token), document.RootElement).Should().Be(accepted);
                if (accepted)
                    AssertEnumProperties(JsonSerializer.Deserialize<JsonElement>(json), schemas.GetProperty(type.Name), document.RootElement);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ReminderInputContractTests.OutputContracts), MemberType = typeof(ReminderInputContractTests))]
    public void CorrectedSchemas_CoverActualNestedOutputAndNullReminders(Type type, bool optionalNulls)
    {
        var value = ReminderInputContractTests.OutputValue(type, optionalNulls);
        var actual = JsonSerializer.Serialize(value, type, Mvc);
        actual.Should().Be(JsonSerializer.Serialize(value, type, ReminderInputContractTests.BaselineOptions(Mvc)));
        foreach (var artifact in OpenApiArtifact.All.Where(a => a.Revision == "r2"))
        {
            using var document = ReadDocument(Path.GetFileName(artifact.Path));
            var schemaName = type.IsGenericType ? "PaginatedResponseOfHabitScheduleItem" : type.Name;
            AssertEnumProperties(JsonSerializer.Deserialize<JsonElement>(actual),
                document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schemaName), document.RootElement);
        }
    }

    [Fact]
    public void SchemaIntegerEvaluation_UsesValuesAndExplicitBounds()
    {
        using var unbounded = JsonDocument.Parse("""{"type":"integer"}""");
        using var bounded = JsonDocument.Parse("""{"type":"integer","minimum":-2147483648,"maximum":2147483647}""");
        foreach (var token in new[] { "2147483648", "-2147483649", "1e100", "1e1000000", "1e2147483648" })
        {
            var value = JsonSerializer.Deserialize<JsonElement>(token);
            Allows(unbounded.RootElement, value, unbounded.RootElement).Should().BeTrue();
            Allows(bounded.RootElement, value, bounded.RootElement).Should().BeFalse();
        }
        foreach (var token in new[] { "1.0", "1e0", "2147483647.000", "-2147483648e0", "0e-2147483649" })
            Allows(bounded.RootElement, JsonSerializer.Deserialize<JsonElement>(token), bounded.RootElement).Should().BeTrue();
        foreach (var token in new[] { "1.5", "2147483647.0000000001", "-2147483648.0000000001" })
            Allows(bounded.RootElement, JsonSerializer.Deserialize<JsonElement>(token), bounded.RootElement).Should().BeFalse();
    }
    [Fact]
    public void ProducerReceipt_RecordsActualMvcDtosAndRequestResults()
    {
        foreach (var type in new[] { typeof(Microsoft.OpenApi.OpenApiSchema), typeof(Microsoft.AspNetCore.OpenApi.OpenApiSchemaTransformerContext), typeof(Microsoft.AspNetCore.OpenApi.OpenApiOptions) })
            foreach (var property in type.GetProperties())
                output.WriteLine($"INTERFACE {type.FullName}.{property.Name}: {property.PropertyType}");
        foreach (var type in new[] { typeof(FrequencyUnit), typeof(DayOfWeek), typeof(InstanceStatus), typeof(ScheduledReminderWhen) })
            foreach (var value in Enum.GetValues(type).Cast<object>().Concat([Enum.ToObject(type, int.MinValue), Enum.ToObject(type, int.MaxValue)]))
                output.WriteLine($"RECEIPT WIRE {type.Name} {Convert.ToInt32(value)} {JsonSerializer.Serialize(value, type, Mvc)}");
        foreach (var type in ResponseTypes)
        {
            var value = CreateResponse(type, 1);
            var actual = JsonSerializer.Serialize(value, type, Mvc);
            actual.Should().Be(JsonSerializer.Serialize(value, type, ReminderInputContractTests.BaselineOptions(Mvc)));
            output.WriteLine($"RECEIPT DTO {type.FullName} {actual}");
        }
        foreach (var (type, optionalNulls) in ReminderInputContractTests.OutputContracts().Select(row => ((Type)row[0], (bool)row[1])))
            output.WriteLine($"RECEIPT REMINDER {type.FullName} {optionalNulls} {JsonSerializer.Serialize(ReminderInputContractTests.OutputValue(type, optionalNulls), type, Mvc)}");
        foreach (var row in AcceptedInputs())
            foreach (var type in RequestTypes)
            {
                var bound = JsonSerializer.Deserialize(RequestJson((string)row[0], (string)row[1]), type, Mvc)!;
                output.WriteLine($"RECEIPT REQUEST {type.Name} {row[0]} {row[1]} {JsonSerializer.Serialize(bound, type, Mvc)}");
            }
        foreach (var row in ReminderForms())
        {
            var token = (string)row[0];
            try
            {
                var value = JsonSerializer.Deserialize<ScheduledReminderTime>($$"""{"when":{{token}},"time":"08:00"}""", Mvc);
                output.WriteLine($"RECEIPT REMINDER_REQUEST {token} {JsonSerializer.Serialize(value, Mvc)}");
            }
            catch (JsonException)
            {
                output.WriteLine($"RECEIPT REMINDER_REQUEST {token} JsonException");
            }
        }
    }

}
