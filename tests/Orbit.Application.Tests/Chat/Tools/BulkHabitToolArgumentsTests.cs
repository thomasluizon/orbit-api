using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Enums;
using Orbit.Domain.Entities;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class BulkHabitToolArgumentsTests
{
    [Theory]
    [MemberData(nameof(OptionalParameterCases.Fields), typeof(BulkUpdateHabitsTool), "updates", MemberType = typeof(OptionalParameterCases))]
    public void OptionalNull_BulkUpdatesPreserveDomainLikeAbsent(string field)
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Keep", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2027, 1, 1), Description: "Keep description", Emoji: "📖",
            Days: [DayOfWeek.Monday], DueTime: new TimeOnly(8, 0), EndDate: new DateOnly(2027, 2, 1))).Value;
        var absent = BulkHabitToolArguments.ParseChanges(Parse("""{"updates":{}}"""));
        var explicitNull = BulkHabitToolArguments.ParseChanges(Parse("{\"updates\":{\"" + field + "\":null}}"));
        absent.Error.Should().BeNull();
        explicitNull.Error.Should().BeNull();
        explicitNull.Changes.Should().BeEquivalentTo(absent.Changes);
        var before = OptionalParameterCases.HabitState(habit);
        habit.Update(BulkUpdateHabitsCommandHandler.ResolveUpdate(habit, explicitNull.Changes!, new DateOnly(2027, 1, 1)))
            .IsSuccess.Should().BeTrue();
        OptionalParameterCases.HabitState(habit).Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(OptionalParameterCases.Fields), typeof(BulkUpdateHabitsTool), "filter", MemberType = typeof(OptionalParameterCases))]
    public void OptionalNull_FilterSelectsSameHabitsAsAbsent(string field)
    {
        var absent = BulkHabitToolArguments.ParseRequiredFilter(Parse("""{"filter":{"all":true}}"""));
        var withNull = field == "all" ? "{\"filter\":{\"all\":null,\"tag\":\"Work\"}}"
            : "{\"filter\":{\"all\":true,\"" + field + "\":null}}";
        if (field == "all")
            absent = BulkHabitToolArguments.ParseRequiredFilter(Parse("""{"filter":{"tag":"Work"}}"""));
        var explicitNull = BulkHabitToolArguments.ParseRequiredFilter(Parse(withNull));
        explicitNull.Error.Should().BeNull();
        explicitNull.Filter.Should().BeEquivalentTo(absent.Filter);
    }

    [Fact]
    public void ParseActionFilter_NullFilterWithHabitIds_EqualsAbsentFilter()
    {
        const string ids = "[\"11111111-1111-1111-1111-111111111111\"]";
        var absent = BulkHabitToolArguments.ParseActionFilter(Parse("{\"habit_ids\":" + ids + "}"));
        var explicitNull = BulkHabitToolArguments.ParseActionFilter(Parse("{\"habit_ids\":" + ids + ",\"filter\":null}"));
        explicitNull.Error.Should().BeNull();
        explicitNull.Filter.Should().BeEquivalentTo(absent.Filter);
    }

    [Fact]
    public void CheckHabitFields_DoesNotValidateUnsupportedDeadline()
    {
        BulkHabitToolArguments.CheckHabitFields(Parse("""{"due_end_time":"25:00"}"""))
            .Should().BeNull();
    }

    [Fact]
    public void ParseChanges_AllSupportedFields_MapsEveryPresenceFlagAndValue()
    {
        var args = Parse("""
            {
              "updates": {
                "title": "Updated",
                "clear_fields": ["description", "end_date", "due_time"],
                "emoji": "✅",
                "frequency_unit": "Day",
                "frequency_quantity": 1,
                "interval_weeks": 2,
                "days": ["Monday"],
                "due_date": "2026-09-12",


                "is_bad_habit": true,
                "is_flexible": false,
                "reminder_enabled": true,
                "reminder_times": [15],
                "checklist_items": [{"text":"Step","is_checked":true}],
                "scheduled_reminders": [{"when":"same_day","time":"08:00"}]
              }
            }
            """);

        var (changes, error) = BulkHabitToolArguments.ParseChanges(args);

        error.Should().BeNull();
        changes.Should().NotBeNull();
        changes!.HasAnyChange.Should().BeTrue();
        changes.Title.Should().Be("Updated");
        changes.HasDescription.Should().BeTrue();
        changes.Description.Should().BeNull();
        changes.Emoji.Should().Be("✅");
        changes.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        changes.FrequencyQuantity.Should().Be(1);
        changes.IntervalWeeks.Should().Be(2);
        changes.Days.Should().Equal(DayOfWeek.Monday);
        changes.DueDate.Should().Be(new DateOnly(2026, 9, 12));
        changes.HasEndDate.Should().BeTrue();
        changes.EndDate.Should().BeNull();
        changes.HasDueTime.Should().BeTrue();
        changes.DueTime.Should().BeNull();
        changes.IsBadHabit.Should().BeTrue();
        changes.IsFlexible.Should().BeFalse();
        changes.ReminderEnabled.Should().BeTrue();
        changes.ReminderTimes.Should().Equal(15);
        changes.ChecklistItems.Should().ContainSingle();
        changes.ScheduledReminders.Should().ContainSingle();
    }

    [Theory]
    [InlineData("{}", "updates is required")]
    [InlineData("{\"updates\":[]}", "updates is required")]
    [InlineData("{\"updates\":{\"unknown\":true}}", "unsupported field")]
    [InlineData("""{"updates":{"due_end_time":"15:00"}}""", "unsupported field")]
    [InlineData("""{"updates":{"due_end_time":"25:00"}}""", "unsupported field")]
    [InlineData("{\"updates\":{\"title\":1}}", "title must be a string")]
    [InlineData("{\"updates\":{\"description\":true}}", "description must be a string or null")]
    [InlineData("{\"updates\":{\"frequency_quantity\":\"one\"}}", "frequency_quantity must be an integer or null")]
    [InlineData("{\"updates\":{\"interval_weeks\":1.5}}", "interval_weeks must be an integer or null")]
    [InlineData("{\"updates\":{\"days\":true}}", "days must be an array")]
    [InlineData("{\"updates\":{\"days\":[\"Funday\"]}}", "days must contain valid weekday names")]
    [InlineData("{\"updates\":{\"reminder_times\":[\"soon\"]}}", "reminder_times must contain only integers")]
    [InlineData("{\"updates\":{\"checklist_items\":[{}]}}", "checklist_items contains an invalid item")]
    [InlineData("{\"updates\":{\"scheduled_reminders\":[{\"when\":\"later\",\"time\":\"08:00\"}]}}", "scheduled_reminders contains an invalid item")]
    [InlineData("{\"updates\":{\"end_date\":\"tomorrow\"}}", "end_date must be a date")]
    [InlineData("{\"updates\":{\"due_time\":\"9am\"}}", "due_time must use HH:mm")]
    [InlineData("{\"updates\":{\"frequency_unit\":\"Hour\"}}", "frequency_unit must be")]
    public void ParseChanges_InvalidInput_ReturnsSpecificError(string json, string expectedError)
    {
        var (changes, error) = BulkHabitToolArguments.ParseChanges(Parse(json));

        changes.Should().BeNull();
        error.Should().Contain(expectedError);
    }

    [Fact]
    public void ParseChanges_MinimalAndTimedUpdates_PreserveOmittedAndParsedStates()
    {
        var (minimal, minimalError) = BulkHabitToolArguments.ParseChanges(Parse("""{"updates":{"title":"Only"}}"""));
        var (timed, timedError) = BulkHabitToolArguments.ParseChanges(Parse("""{"updates":{"due_time":"09:30","clear_fields":["frequency_unit"]}}"""));

        minimalError.Should().BeNull();
        minimal!.HasDueDate.Should().BeFalse();
        minimal.HasEndDate.Should().BeFalse();
        minimal.HasDueTime.Should().BeFalse();
        minimal.HasFrequencyUnit.Should().BeFalse();
        timedError.Should().BeNull();
        timed!.DueTime.Should().Be(new TimeOnly(9, 30));
        timed.HasFrequencyUnit.Should().BeTrue();
        timed.FrequencyUnit.Should().BeNull();
    }

    [Fact]
    public void ParseRequiredFilter_FullPredicateFilter_MapsValues()
    {
        var args = Parse("""
            {"filter":{"tag":"Work","search":"read","is_completed":true,"is_general":false,"is_bad_habit":true,"frequency":"Week"}}
            """);

        var (filter, error) = BulkHabitToolArguments.ParseRequiredFilter(args);

        error.Should().BeNull();
        filter!.Tag.Should().Be("Work");
        filter.Search.Should().Be("read");
        filter.IsCompleted.Should().BeTrue();
        filter.IsGeneral.Should().BeFalse();
        filter.IsBadHabit.Should().BeTrue();
        filter.Frequency.Should().Be(FrequencyUnit.Week);
    }

    [Theory]
    [InlineData("{}", "filter is required")]
    [InlineData("{\"filter\":[]}", "filter is required")]
    [InlineData("{\"filter\":{\"unknown\":true}}", "unsupported field")]
    [InlineData("{\"filter\":{\"all\":\"yes\"}}", "filter.all must be a boolean")]
    [InlineData("{\"filter\":{\"tag\":true}}", "filter.tag must be a string")]
    [InlineData("{\"filter\":{\"habit_ids\":true}}", "filter.habit_ids must be an array")]
    [InlineData("{\"filter\":{\"habit_ids\":[\"bad\"]}}", "valid habit IDs")]
    [InlineData("{\"filter\":{\"frequency\":true}}", "filter.frequency must be")]
    [InlineData("{\"filter\":{\"frequency\":\"Hour\"}}", "filter.frequency must be")]
    [InlineData("{\"filter\":{}}", "filter must explicitly select")]
    public void ParseRequiredFilter_InvalidInput_ReturnsSpecificError(string json, string expectedError)
    {
        var (filter, error) = BulkHabitToolArguments.ParseRequiredFilter(Parse(json));

        filter.Should().BeNull();
        error.Should().Contain(expectedError);
    }

    [Fact]
    public void ParseRequiredFilter_AllAndIds_ReturnsConflict()
    {
        var id = Guid.NewGuid();
        var (filter, error) = BulkHabitToolArguments.ParseRequiredFilter(
            Parse($$$"""{"filter":{"all":true,"habit_ids":["{{{id}}}"]}}"""));

        filter.Should().BeNull();
        error.Should().Contain("cannot combine");
    }

    [Fact]
    public void ParseRequiredFilter_OneTimeFrequency_MapsDedicatedSelector()
    {
        var (filter, error) = BulkHabitToolArguments.ParseRequiredFilter(
            Parse("""{"filter":{"frequency":"OneTime"}}"""));

        error.Should().BeNull();
        filter!.OneTime.Should().BeTrue();
        filter.Frequency.Should().BeNull();
    }

    [Fact]
    public void ParseEmojiFilter_FilterObject_UsesServerSideParser()
    {
        var (filter, error) = BulkHabitToolArguments.ParseEmojiFilter(
            Parse("""{"filter":{"is_completed":true}}"""));

        error.Should().BeNull();
        filter!.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void ParseEmojiFilter_NonObjectFilter_ReturnsError()
    {
        var (filter, error) = BulkHabitToolArguments.ParseEmojiFilter(Parse("""{"filter":true}"""));

        filter.Should().BeNull();
        error.Should().Contain("filter must be an object");
    }

    [Fact]
    public void ParseActionFilter_NullUnusedSelector_AcceptsFilter()
    {
        var (filter, error) = BulkHabitToolArguments.ParseActionFilter(
            Parse("""{"habit_ids":null,"filter":{"all":true}}"""));

        error.Should().BeNull();
        filter!.All.Should().BeTrue();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
