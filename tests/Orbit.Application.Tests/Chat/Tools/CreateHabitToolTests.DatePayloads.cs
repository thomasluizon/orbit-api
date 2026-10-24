using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Chat.Tools;

public partial class CreateHabitToolTests
{
    [Theory]
    [InlineData("""{"title":"Varrer a garagem","frequency_unit":"Week","frequency_quantity":1,"days":["Thursday"]}""")]
    [InlineData("""{"title":"Varrer a garagem","frequency_unit":"Day","frequency_quantity":1,"days":["Thursday"]}""")]
    public async Task RecordedThursdayPayload_CreatesWeeklyRecurrence(string json)
    {
        using var args = JsonDocument.Parse(json);
        var check = await _tool.CheckArgumentsAsync(args.RootElement, UserId, CancellationToken.None);
        check.IsSuccess.Should().BeTrue();

        var result = await Execute(json);

        result.Success.Should().BeTrue();
        var habit = AddedHabits().Should().ContainSingle().Subject;
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        habit.FrequencyQuantity.Should().Be(1);
        habit.Days.Should().Equal(DayOfWeek.Thursday);
        HabitScheduleService.GetScheduledDates(habit, Today, Today.AddDays(20), weekStartDay: 1)
            .Should().Equal(new DateOnly(2026, 4, 9), new DateOnly(2026, 4, 16), new DateOnly(2026, 4, 23));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DatedOneTimePayload_DropsWeekdayFields(bool explicitNull, bool extraCadence)
    {
        var frequency = explicitNull ? "\"frequency_unit\":null," : string.Empty;
        var extraFields = extraCadence
            ? "\"days\":[\"Wednesday\"],\"frequency_quantity\":1,\"interval_weeks\":2,"
            : "\"days\":[\"Wednesday\"],";
        var json = $$"""{"title":"Aula extra","due_date":"2026-08-26",{{frequency}}{{extraFields}}"due_time":"11:15","due_end_time":"12:05"}""";
        using var args = JsonDocument.Parse(json);
        (await _tool.CheckArgumentsAsync(args.RootElement, UserId, CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        (await Execute(json)).Success.Should().BeTrue();

        AssertDatedClass(AddedHabits().Single(), new DateOnly(2026, 8, 26));
    }

    [Fact]
    public async Task MixedClassPayloads_CreateRecurringClassAndDatedChild()
    {
        var result = await Execute("""
        {
            "title":"Aula semanal",
            "frequency_unit":"Week",
            "frequency_quantity":1,
            "days":["Wednesday"],
            "due_time":"08:25",
            "due_end_time":"11:15",
            "sub_habits":[{
                "title":"Aula extra",
                "frequency_unit":"OneTime",
                "due_date":"2026-08-26"
            }]
        }
        """);

        result.Success.Should().BeTrue();
        var habits = AddedHabits();
        habits.Should().HaveCount(2);
        var parent = habits.Single(habit => habit.ParentHabitId is null);
        parent.Days.Should().Equal(DayOfWeek.Wednesday);
        parent.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        parent.DueTime.Should().Be(new TimeOnly(8, 25));
        parent.DueEndTime.Should().Be(new TimeOnly(11, 15));
        var child = habits.Single(habit => habit.ParentHabitId == parent.Id);
        child.Schedule.Should().Be(HabitSchedule.OneTime);
        child.Days.Should().BeEmpty();
        child.NextDueDate.Should().Be(new DateOnly(2026, 8, 26));
    }

    [Fact]
    public async Task SevenDatedClassPayloads_CreateSevenOneTimeTasks()
    {
        string[] dates = ["2026-09-21", "2026-08-26", "2026-09-23", "2026-10-21", "2026-08-21", "2026-09-18", "2026-10-23"];
        foreach (var date in dates)
        {
            var result = await Execute($$"""{"title":"Aula extra","frequency_unit":null,"due_date":"{{date}}","due_time":"11:15","due_end_time":"12:05"}""");
            result.Success.Should().BeTrue();
        }

        var habits = AddedHabits();
        habits.Should().HaveCount(7);
        for (var i = 0; i < dates.Length; i++)
            AssertDatedClass(habits[i], DateOnly.Parse(dates[i]));
    }

    [Fact]
    public async Task BeforeDeadline_PreservesEndTimeWithoutStartTime()
    {
        (await Execute("""{"title":"Varrer a garagem","frequency_unit":"Day","frequency_quantity":1,"days":["Thursday"],"due_time":null,"due_end_time":"15:00"}"""))
            .Success.Should().BeTrue();

        var habit = AddedHabits().Single();
        habit.DueTime.Should().BeNull();
        habit.DueEndTime.Should().Be(new TimeOnly(15, 0));
        var schema = JsonSerializer.SerializeToElement(_tool.GetParameterSchema());
        schema.GetProperty("properties").TryGetProperty("due_end_time", out _).Should().BeTrue();
    }

    [Fact]
    public async Task EndTimeBeforeStart_IsRejectedByPreflightAndExecution()
    {
        const string json = """{"title":"Aula extra","due_date":"2026-08-26","due_time":"12:05","due_end_time":"11:15"}""";
        using var args = JsonDocument.Parse(json);
        (await _tool.CheckArgumentsAsync(args.RootElement, UserId, CancellationToken.None))
            .IsFailure.Should().BeTrue();
        (await Execute(json)).Success.Should().BeFalse();
        AddedHabits().Should().BeEmpty();
    }

    [Fact]
    public async Task DatedClarification_OneTimeActionPreservesRequestedDate()
    {
        var result = await Execute("""{"title":"Hábito de aula extra","due_date":"2026-08-26"}""");
        var clarification = result.Payload.Should().BeOfType<NeedsClarificationPayload>().Subject;
        var action = clarification.QuickActions!.Single(action => action.Label == "habits.clarification.quickAction.oneTime");
        using var patch = JsonDocument.Parse(action.Value);
        patch.RootElement.GetProperty("frequency_unit").GetString().Should().Be("OneTime");
        patch.RootElement.GetProperty("due_date").GetString().Should().Be("2026-08-26");
    }

    [Fact]
    public async Task RecurringClassWithStartDate_PreservesExistingScheduleFields()
    {
        const string json = """{"title":"Aula semanal","frequency_unit":"Day","frequency_quantity":1,"interval_weeks":2,"days":["Wednesday","Friday"],"due_date":"2026-08-26","due_time":"08:25","description":"Aulas"}""";
        (await Execute(json)).Success.Should().BeTrue();
        var expected = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Aula semanal", FrequencyUnit.Day, 1,
            new DateOnly(2026, 8, 26), "Aulas", Days: [DayOfWeek.Wednesday, DayOfWeek.Friday],
            DueTime: new TimeOnly(8, 25), IntervalWeeks: 2, Position: 0)).Value;

        AddedHabits().Single().Should().BeEquivalentTo(expected, options => options.ComparingByMembers<Habit>()
            .Excluding(habit => habit.Id).Excluding(habit => habit.CreatedAtUtc).Excluding(habit => habit.UpdatedAtUtc));
    }

    [Theory]
    [InlineData("Day")]
    [InlineData("Week")]
    public async Task DatedChildWithExplicitRecurrence_KeepsItsOwnWeekdays(string unit)
    {
        var json = $$"""
        {
            "title":"Aulas",
            "frequency_unit":"Week",
            "days":["Wednesday"],
            "sub_habits":[{
                "title":"Aula recorrente",
                "frequency_unit":"{{unit}}",
                "days":["Thursday"],
                "due_date":"2026-08-27"
            },{"title":"Aula herdada"}]
        }
        """;
        using var args = JsonDocument.Parse(json);
        (await _tool.CheckArgumentsAsync(args.RootElement, UserId, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await Execute(json)).Success.Should().BeTrue();

        var child = AddedHabits().Single(habit => habit.Title == "Aula recorrente");
        child.Schedule.Should().Be(HabitSchedule.Recurring);
        child.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        child.Days.Should().Equal(DayOfWeek.Thursday);
        child.NextDueDate.Should().Be(new DateOnly(2026, 8, 27));
        var inherited = AddedHabits().Single(habit => habit.Title == "Aula herdada");
        inherited.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        inherited.Days.Should().Equal(DayOfWeek.Wednesday);
    }

    [Fact]
    public async Task MalformedDeadline_IsRejectedDuringArgumentCheck()
    {
        const string json = """{"title":"Aula extra","due_end_time":"25:00"}""";
        using var args = JsonDocument.Parse(json);

        var result = await _tool.CheckArgumentsAsync(args.RootElement, UserId, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("due_end_time").And.Contain("HH:mm");
        AddedHabits().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Day", 1)]
    [InlineData("Day", 3)]
    [InlineData("Week", 2)]
    public async Task DatedChildWithoutExplicitRecurrence_InheritsCadenceWithoutWeekdays(string unit, int quantity)
    {
        var parentDays = unit == "Day" && quantity == 1 ? "\"days\":[\"Wednesday\"]," : string.Empty;
        var json = $$"""
        {
            "title":"Aulas",
            "frequency_unit":"{{unit}}",
            "frequency_quantity":{{quantity}},
            "interval_weeks":2,
            {{parentDays}}
            "sub_habits":[{"title":"Aula com início próprio","due_date":"2026-08-27"}]
        }
        """;
        using var args = JsonDocument.Parse(json);
        (await _tool.CheckArgumentsAsync(args.RootElement, UserId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await Execute(json)).Success.Should().BeTrue();

        var child = AddedHabits().Single(habit => habit.ParentHabitId is not null);
        child.Schedule.Should().Be(HabitSchedule.Recurring);
        child.FrequencyUnit.Should().Be(Enum.Parse<FrequencyUnit>(unit));
        child.FrequencyQuantity.Should().Be(quantity);
        child.IntervalWeeks.Should().Be(2);
        child.Days.Should().BeEmpty();
        child.NextDueDate.Should().Be(new DateOnly(2026, 8, 27));
    }

    [Fact]
    public async Task RecurringChild_WithTimingFields_PreservesChildTiming()
    {
        (await Execute("""
        {"title":"Aulas","frequency_unit":"Day","days":["Wednesday"],
         "sub_habits":[{"title":"Aula herdada","due_time":"08:25","due_end_time":"11:15"}]}
        """)).Success.Should().BeTrue();

        var parent = AddedHabits().Single(habit => habit.ParentHabitId is null);
        var expected = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Aula herdada", FrequencyUnit.Day, 1,
            parent.NextDueDate, Days: [DayOfWeek.Wednesday], ParentHabitId: parent.Id, Position: 0,
            DueTime: new TimeOnly(8, 25), DueEndTime: new TimeOnly(11, 15))).Value;
        AddedHabits().Single(habit => habit.ParentHabitId is not null).Should().BeEquivalentTo(expected,
            options => options.ComparingByMembers<Habit>()
                .Excluding(habit => habit.Id).Excluding(habit => habit.CreatedAtUtc).Excluding(habit => habit.UpdatedAtUtc));
    }

    private List<Habit> AddedHabits() => _habitRepo.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == "AddAsync")
        .Select(call => (Habit)call.GetArguments()[0]!).ToList();

    private static void AssertDatedClass(Habit habit, DateOnly date)
    {
        habit.Schedule.Should().Be(HabitSchedule.OneTime);
        habit.FrequencyUnit.Should().BeNull();
        habit.FrequencyQuantity.Should().BeNull();
        habit.IntervalWeeks.Should().BeNull();
        habit.Days.Should().BeEmpty();
        habit.NextDueDate.Should().Be(date);
        habit.DueTime.Should().Be(new TimeOnly(11, 15));
        habit.DueEndTime.Should().Be(new TimeOnly(12, 5));
    }
}
