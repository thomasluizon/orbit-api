using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orbit.Api.Controllers;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;
using JsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ReminderInputContractTests
{
    internal static JsonSerializerOptions MvcOptions()
    {
        var builder = WebApplication.CreateBuilder();
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddApiPipeline(builder);
        using var provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
    }

    internal static JsonSerializerOptions BaselineOptions(JsonSerializerOptions current)
    {
        var baseline = new JsonSerializerOptions(current);
        baseline.Converters.Clear();
        baseline.Converters.Add(new JsonStringEnumConverter());
        return baseline;
    }

    [Theory]
    [InlineData("same_day", ScheduledReminderWhen.SameDay, false)]
    [InlineData("day_before", ScheduledReminderWhen.DayBefore, false)]
    [InlineData("SameDay", ScheduledReminderWhen.SameDay, false)]
    [InlineData("DayBefore", ScheduledReminderWhen.DayBefore, false)]
    [InlineData("same_day", ScheduledReminderWhen.SameDay, true)]
    [InlineData("day_before", ScheduledReminderWhen.DayBefore, true)]
    [InlineData("SameDay", ScheduledReminderWhen.SameDay, true)]
    [InlineData("DayBefore", ScheduledReminderWhen.DayBefore, true)]
    public void MvcOptions_AcceptShippedAliasesAndExistingNames(string name, ScheduledReminderWhen expected, bool relative)
    {
        var json = $$"""{"when":"{{name}}","time":"08:00"}""";
        var options = MvcOptions();
        var actual = relative
            ? JsonSerializer.Deserialize<RelativeReminderTime>(json, options)!.When
            : JsonSerializer.Deserialize<ScheduledReminderTime>(json, options)!.When;
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("\"sameday\"", 0)]
    [InlineData("\"daybefore\"", 1)]
    [InlineData("\"SAMEDAY\"", 0)]
    [InlineData("\"dAyBeFoRe\"", 1)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("-1", -1)]
    [InlineData("\"0\"", 0)]
    [InlineData("\"1\"", 1)]
    public void MvcOptions_PreserveBaselineCasingAndNumbers(string token, int expected)
    {
        var current = MvcOptions();
        var baseline = BaselineOptions(current);
        var json = $$"""{"when":{{token}},"time":"08:00"}""";
        foreach (var options in new[] { baseline, current })
        {
            JsonSerializer.Deserialize<ScheduledReminderTime>(json, options)!.When.Should().Be((ScheduledReminderWhen)expected);
            JsonSerializer.Deserialize<RelativeReminderTime>(json, options)!.When.Should().Be((ScheduledReminderWhen)expected);
        }
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("\"SAME_DAY\"")]
    [InlineData("\"DAY_BEFORE\"")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("1.5")]
    [InlineData("\"same_day")]
    public void MvcOptions_KeepRejectingMalformedAndUnknownEnumInputs(string token)
    {
        var current = MvcOptions();
        var json = $$"""{"when":{{token}},"time":"08:00"}""";
        foreach (var options in new[] { BaselineOptions(current), current })
        {
            Action scheduled = () => JsonSerializer.Deserialize<ScheduledReminderTime>(json, options);
            Action relative = () => JsonSerializer.Deserialize<RelativeReminderTime>(json, options);
            scheduled.Should().Throw<JsonException>();
            relative.Should().Throw<JsonException>();
        }
    }

    [Fact]
    public void MvcOptions_PreserveNullTimeAndUnrelatedEnumContracts()
    {
        var current = MvcOptions();
        foreach (var options in new[] { BaselineOptions(current), current })
        {
            Action requiredNull = () => JsonSerializer.Deserialize<ScheduledReminderTime>("""{"when":null,"time":"08:00"}""", options);
            requiredNull.Should().Throw<JsonException>();
            JsonSerializer.Deserialize<RelativeReminderTime>("""{"minutesBefore":15,"when":null,"time":null}""", options)
                .Should().Be(new RelativeReminderTime(MinutesBefore: 15));
            Action invalidTime = () => JsonSerializer.Deserialize<ScheduledReminderTime>("""{"when":"SameDay","time":"25:00"}""", options);
            invalidTime.Should().Throw<JsonException>();
            JsonSerializer.Serialize(new RelativeReminderTime(MinutesBefore: 15), options)
                .Should().Be("""{"minutesBefore":15,"when":null,"time":null}""");
            JsonSerializer.Serialize(new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(8, 0)), options)
                .Should().Be("""{"when":"DayBefore","time":"08:00:00"}""");
            JsonSerializer.Serialize(FrequencyUnit.Day, options).Should().Be("\"Day\"");
            Action unrelated = () => JsonSerializer.Deserialize<FrequencyUnit>("\"same_day\"", options);
            unrelated.Should().Throw<JsonException>();
        }
    }

    [Theory]
    [InlineData("""{"minutesBefore":15,"when":"SameDay","time":"08:00"}""")]
    [InlineData("""{"when":"DayBefore"}""")]
    [InlineData("""{"when":2,"time":"08:00"}""")]
    [InlineData("""{"minutesBefore":-1440}""")]
    public void ParsedInvalidCombinations_StillFailValidatorAndDomain(string reminder)
    {
        var request = JsonSerializer.Deserialize<HabitsController.CreateHabitRequest>(
            $$"""{"title":"Reminder","dueTime":"09:00","relativeReminders":[{{reminder}}]}""", MvcOptions())!;
        var command = new CreateHabitCommand(Guid.NewGuid(), request.Title, null, null, null,
            Options: new HabitCommandOptions(DueTime: request.DueTime, RelativeReminders: request.RelativeReminders));
        new CreateHabitCommandValidator().Validate(command).IsValid.Should().BeFalse();
        Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, command.UserId, command.Title, null, null, new DateOnly(2026, 4, 3),
            DueTime: request.DueTime, RelativeReminders: request.RelativeReminders)).IsFailure.Should().BeTrue();
    }

}
