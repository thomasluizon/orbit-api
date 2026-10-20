using System.Text.Json;
using FluentAssertions;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ReminderInputContractTests
{
    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void OpenApi_DescribesCurrentOutputAndInputAliases(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ScheduledReminderWhen");
        schema.GetProperty("description").GetString().Should().Contain("SameDay").And.Contain("DayBefore")
            .And.Contain("same_day").And.Contain("day_before").And.Contain("domain snake_case annotation");
    }

    public static TheoryData<Type, bool> OutputContracts()
    {
        var cases = new TheoryData<Type, bool>();
        foreach (var type in new[]
        {
            typeof(HabitDetailResponse), typeof(HabitChildResponse), typeof(HabitScheduleItem),
            typeof(HabitScheduleChildItem), typeof(PaginatedResponse<HabitScheduleItem>),
            typeof(HabitFullDetailResponse), typeof(CalendarMonthResponse),
            typeof(SyncController.SyncHabitDto), typeof(SyncController.SyncChangesV2Response)
        })
        {
            cases.Add(type, false);
            cases.Add(type, true);
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(OutputContracts))]
    public void ProducedContracts_PreserveCompleteOutput(Type type, bool optionalNulls)
    {
        var value = OutputValue(type, optionalNulls);
        var current = MvcOptions();
        var json = JsonSerializer.Serialize(value, type, current);
        json.Should().Be(JsonSerializer.Serialize(value, type, BaselineOptions(current)),
            "all object keys, nulls, enum values and time formats must match the installed baseline converter");
        using var document = JsonDocument.Parse(json);
        AssertReminderObjects(document.RootElement);
        json.Should().Contain("\"when\":\"SameDay\"").And.Contain("\"when\":\"DayBefore\"");
    }

    private static void AssertReminderObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "scheduledReminders" or "relativeReminders" && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var reminder in property.Value.EnumerateArray())
                    {
                        reminder.EnumerateObject().Select(p => p.Name).Should().Equal(
                            property.Name == "scheduledReminders" ? ["when", "time"] : ["minutesBefore", "when", "time"]);
                        if (reminder.GetProperty("when").ValueKind == JsonValueKind.Null)
                        {
                            reminder.GetRawText().Should().Be("""{"minutesBefore":15,"when":null,"time":null}""");
                        }
                        else
                        {
                            reminder.GetProperty("when").GetString().Should().BeOneOf("SameDay", "DayBefore");
                            reminder.GetProperty("time").GetString().Should().Be("08:00:00.1230000");
                            if (property.Name == "relativeReminders")
                                reminder.GetProperty("minutesBefore").ValueKind.Should().Be(JsonValueKind.Null);
                        }
                    }
                }
                AssertReminderObjects(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AssertReminderObjects(item);
        }
    }

    internal static object? OutputValue(Type type, bool optionalNulls, int depth = 0)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return optionalNulls ? null : OutputValue(underlying, optionalNulls, depth);
        if (type == typeof(string)) return "Reminder";
        if (type == typeof(Guid)) return Guid.Empty;
        if (type == typeof(bool)) return false;
        if (type == typeof(int)) return 1;
        if (type == typeof(decimal)) return 1m;
        if (type == typeof(double)) return 1d;
        if (type == typeof(DateOnly)) return new DateOnly(2026, 4, 3);
        if (type == typeof(TimeOnly)) return new TimeOnly(8, 0, 0, 123);
        if (type == typeof(DateTime)) return new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return OutputList(type.GetGenericArguments()[0], optionalNulls, depth);
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            return Activator.CreateInstance(type);
        var constructor = type.GetConstructors().Single();
        return constructor.Invoke(constructor.GetParameters().Select(parameter =>
            optionalNulls && parameter.HasDefaultValue && parameter.DefaultValue is null
                && parameter.ParameterType != typeof(IReadOnlyList<RelativeReminderTime>)
                ? null : OutputValue(parameter.ParameterType, optionalNulls, depth)).ToArray());
    }
    private static object OutputList(Type item, bool optionalNulls, int depth)
    {
        var time = new TimeOnly(8, 0, 0, 123);
        if (item == typeof(ScheduledReminderTime))
            return Enum.GetValues<ScheduledReminderWhen>().Select(when => new ScheduledReminderTime(when, time)).ToArray();
        if (item == typeof(RelativeReminderTime))
            return Enum.GetValues<ScheduledReminderWhen>().Select(when => new RelativeReminderTime(When: when, Time: time))
                .Append(new RelativeReminderTime(15)).ToArray();
        var nested = item == typeof(HabitScheduleItem) || item == typeof(SyncController.SyncHabitDto)
            || (depth < 2 && (item == typeof(HabitChildResponse) || item == typeof(HabitScheduleChildItem)));
        var values = Array.CreateInstance(item, nested ? 1 : 0);
        if (nested) values.SetValue(OutputValue(item, optionalNulls, depth + 1), 0);
        return values;
    }

}
