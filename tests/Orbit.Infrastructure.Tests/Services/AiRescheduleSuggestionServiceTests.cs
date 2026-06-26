using System.Reflection;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure boundary logic in AiRescheduleSuggestionService: the clamps that turn raw model
/// output into a schedule the habit-update path accepts, plus the prompt builder. The AI client call
/// itself is an integration concern tested elsewhere.
/// </summary>
public class AiRescheduleSuggestionServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 1);

    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void ResolveDueDate_FutureDate_ReturnsIt()
    {
        var result = AiRescheduleSuggestionService.ResolveDueDate("2026-04-10", null, Today);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new DateOnly(2026, 4, 10));
    }

    [Fact]
    public void ResolveDueDate_PastDate_ClampsToToday()
    {
        var result = AiRescheduleSuggestionService.ResolveDueDate("2026-03-01", null, Today);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Today);
    }

    [Fact]
    public void ResolveDueDate_AfterEndDate_ClampsToEndDate()
    {
        var endDate = new DateOnly(2026, 4, 5);
        var result = AiRescheduleSuggestionService.ResolveDueDate("2026-04-20", endDate, Today);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(endDate);
    }

    [Fact]
    public void ResolveDueDate_EndDateBeforeToday_Fails()
    {
        var endDate = Today.AddDays(-2);
        var result = AiRescheduleSuggestionService.ResolveDueDate("2026-04-20", endDate, Today);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ResolveDueDate_InvalidString_Fails()
    {
        AiRescheduleSuggestionService.ResolveDueDate("not-a-date", null, Today).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ResolveDueDate_NullOrEmpty_Fails()
    {
        AiRescheduleSuggestionService.ResolveDueDate(null, null, Today).IsFailure.Should().BeTrue();
        AiRescheduleSuggestionService.ResolveDueDate("  ", null, Today).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ResolveCadence_NullUnit_ReturnsNullPair()
    {
        AiRescheduleSuggestionService.ResolveCadence(null, 3).Should().Be(((FrequencyUnit?)null, (int?)null));
    }

    [Fact]
    public void ResolveCadence_InvalidUnit_ReturnsNullPair()
    {
        AiRescheduleSuggestionService.ResolveCadence("Fortnight", 2).Should().Be(((FrequencyUnit?)null, (int?)null));
    }

    [Fact]
    public void ResolveCadence_ValidUnitAndQuantity_ReturnsBoth()
    {
        AiRescheduleSuggestionService.ResolveCadence("Week", 2).Should().Be(((FrequencyUnit?)FrequencyUnit.Week, (int?)2));
    }

    [Fact]
    public void ResolveCadence_LowercaseUnit_MapsCaseInsensitively()
    {
        AiRescheduleSuggestionService.ResolveCadence("day", null).Should().Be(((FrequencyUnit?)FrequencyUnit.Day, (int?)1));
    }

    [Fact]
    public void ResolveCadence_NonPositiveQuantity_ClampsToOne()
    {
        AiRescheduleSuggestionService.ResolveCadence("Month", 0).Should().Be(((FrequencyUnit?)FrequencyUnit.Month, (int?)1));
        AiRescheduleSuggestionService.ResolveCadence("Month", -4).Should().Be(((FrequencyUnit?)FrequencyUnit.Month, (int?)1));
    }

    [Fact]
    public void ResolveDays_DailyQuantityOne_KeepsParsedDays()
    {
        var days = AiRescheduleSuggestionService.ResolveDays(["Monday", "Wednesday"], FrequencyUnit.Day, 1);

        days.Should().Equal(DayOfWeek.Monday, DayOfWeek.Wednesday);
    }

    [Fact]
    public void ResolveDays_WeeklyCadence_ClearsDays()
    {
        AiRescheduleSuggestionService.ResolveDays(["Monday"], FrequencyUnit.Week, 1).Should().BeEmpty();
    }

    [Fact]
    public void ResolveDays_DailyQuantityGreaterThanOne_ClearsDays()
    {
        AiRescheduleSuggestionService.ResolveDays(["Monday"], FrequencyUnit.Day, 2).Should().BeEmpty();
    }

    [Fact]
    public void ResolveDays_NullDays_ReturnsEmpty()
    {
        AiRescheduleSuggestionService.ResolveDays(null, FrequencyUnit.Day, 1).Should().BeEmpty();
    }

    [Fact]
    public void ResolveDays_InvalidAndDuplicateNames_AreIgnoredAndDeduped()
    {
        var days = AiRescheduleSuggestionService.ResolveDays(
            ["Monday", "Funday", "monday"], FrequencyUnit.Day, 1);

        days.Should().Equal(DayOfWeek.Monday);
    }

    [Fact]
    public void ResolveDueTime_ValidTime_Parses()
    {
        AiRescheduleSuggestionService.ResolveDueTime("08:30").Should().Be(new TimeOnly(8, 30));
    }

    [Fact]
    public void ResolveDueTime_NullOrGarbage_ReturnsNull()
    {
        AiRescheduleSuggestionService.ResolveDueTime(null).Should().BeNull();
        AiRescheduleSuggestionService.ResolveDueTime("later").Should().BeNull();
    }

    [Fact]
    public void BuildPrompt_English_ContainsTitleScheduleAndRules()
    {
        var habit = CreateRecurring("Evening run", FrequencyUnit.Day, dueDate: Today.AddDays(-5));

        var prompt = InvokeBuildPrompt(habit, Today, "en");

        prompt.Should().Contain("Evening run");
        prompt.Should().Contain("English");
        prompt.Should().Contain("\"dueDate\"");
        prompt.Should().Contain("MUST be 2026-04-01 or later");
    }

    [Fact]
    public void BuildPrompt_Portuguese_RequestsPortugueseRationale()
    {
        var habit = CreateOneTimeTask("Renovar passaporte", Today.AddDays(-3));

        var prompt = InvokeBuildPrompt(habit, Today, "pt-BR");

        prompt.Should().Contain("Brazilian Portuguese");
        prompt.Should().Contain("one-time task");
    }

    private static Habit CreateRecurring(string title, FrequencyUnit unit, int quantity = 1, DateOnly? dueDate = null) =>
        Habit.Create(new HabitCreateParams(
            ValidUserId, title, unit, quantity, DueDate: dueDate ?? Today)).Value;

    private static Habit CreateOneTimeTask(string title, DateOnly dueDate) =>
        Habit.Create(new HabitCreateParams(
            ValidUserId, title, null, null, DueDate: dueDate)).Value;

    private static string InvokeBuildPrompt(Habit habit, DateOnly userToday, string language)
    {
        var method = typeof(AiRescheduleSuggestionService).GetMethod("BuildPrompt", PrivateStatic)!;
        return (string)method.Invoke(null, [habit, userToday, language])!;
    }
}
