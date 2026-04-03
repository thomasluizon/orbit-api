using System.Reflection;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure helper methods shared/used by AiRetrospectiveService.
/// The AI client orchestration is tested at the integration level.
/// StripMarkdownFences is the shared utility from AiSummaryService.
/// </summary>
public class AiRetrospectiveServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly DateFrom = new(2025, 1, 1);
    private static readonly DateOnly DateTo = new(2025, 1, 7);

    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    // ── StripMarkdownFences edge cases specific to retrospective output ──

    [Fact]
    public void StripMarkdownFences_BoldHeadings_PreservesBoldMarkdown()
    {
        var text = "**Highlights**\nGreat job!\n\n**Missed Opportunities**\nCould improve.";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("**Highlights**");
        result.Should().Contain("**Missed Opportunities**");
    }

    [Fact]
    public void StripMarkdownFences_FencedRetrospective_StripsCorrectly()
    {
        var text = "```\n**Highlights**\nYou nailed exercise.\n**Trends**\nGoing strong.\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("**Highlights**");
        result.Should().NotStartWith("```");
        result.Should().NotEndWith("```");
    }

    [Fact]
    public void StripMarkdownFences_EmptyInput_ReturnsEmpty()
    {
        AiSummaryService.StripMarkdownFences("").Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_OnlyFences_ReturnsEmpty()
    {
        var text = "```\n```";
        AiSummaryService.StripMarkdownFences(text).Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_NestedFences_HandlesGracefully()
    {
        // Should strip the outer fences
        var text = "```\nSome code:\n```inner```\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("Some code:");
    }

    // ── BuildRetrospectivePrompt via reflection ──

    [Fact]
    public void BuildRetrospectivePrompt_English_ContainsEnglishHeadings()
    {
        var habits = new List<Habit> { CreateDailyHabit("Exercise") };
        var result = InvokeBuildRetrospectivePrompt(habits, DateFrom, DateTo, "weekly", "en");

        result.Should().Contain("Highlights");
        result.Should().Contain("Missed Opportunities");
        result.Should().Contain("Trends");
        result.Should().Contain("Suggestion");
        result.Should().Contain("English");
    }

    [Fact]
    public void BuildRetrospectivePrompt_Portuguese_ContainsPortugueseHeadings()
    {
        var habits = new List<Habit> { CreateDailyHabit("Exercicio") };
        var result = InvokeBuildRetrospectivePrompt(habits, DateFrom, DateTo, "weekly", "pt-br");

        result.Should().Contain("Destaques");
        result.Should().Contain("Oportunidades Perdidas");
        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildRetrospectivePrompt_PtShort_AlsoMapsToPortuguese()
    {
        var habits = new List<Habit> { CreateDailyHabit("Read") };
        var result = InvokeBuildRetrospectivePrompt(habits, DateFrom, DateTo, "monthly", "pt");

        result.Should().Contain("Destaques");
        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildRetrospectivePrompt_IncludesPeriodAndDateRange()
    {
        var habits = new List<Habit> { CreateDailyHabit("Meditate") };
        var result = InvokeBuildRetrospectivePrompt(habits, DateFrom, DateTo, "monthly", "en");

        result.Should().Contain("monthly");
        result.Should().Contain("January");
    }

    [Fact]
    public void BuildRetrospectivePrompt_CalculatesCorrectTotalDays()
    {
        var from = new DateOnly(2025, 3, 1);
        var to = new DateOnly(2025, 3, 10);
        var habits = new List<Habit> { CreateDailyHabit("Walk") };

        var result = InvokeBuildRetrospectivePrompt(habits, from, to, "weekly", "en");

        result.Should().Contain("10 days"); // 10 - 1 + 1 = 10 (inclusive)
    }

    [Fact]
    public void BuildRetrospectivePrompt_CountsOnlyTopLevelHabits()
    {
        var parent = CreateDailyHabit("Workout");
        var child = CreateDailyHabit("Push-ups", parentId: parent.Id);
        var habits = new List<Habit> { parent, child };

        var result = InvokeBuildRetrospectivePrompt(habits, DateFrom, DateTo, "weekly", "en");

        // Should say "1" top-level habit, not 2
        result.Should().Contain("Total habits tracked: 1");
    }

    // ── AppendParentHabitLine via reflection ──

    [Fact]
    public void AppendParentHabitLine_BadHabit_ShowsSlipsFormat()
    {
        var lines = new List<string>();
        var habit = CreateDailyHabit("Smoking", isBadHabit: true);
        var badHabitSlips = 0;

        InvokeAppendParentHabitLine(lines, habit, 7, 3, 7, ref badHabitSlips);

        lines.Should().ContainSingle();
        lines[0].Should().Contain("bad habit");
        lines[0].Should().Contain("3 slips");
        badHabitSlips.Should().Be(3);
    }

    [Fact]
    public void AppendParentHabitLine_RegularHabit_ShowsCompletionRate()
    {
        var lines = new List<string>();
        var habit = CreateDailyHabit("Exercise");
        var badHabitSlips = 0;

        InvokeAppendParentHabitLine(lines, habit, 7, 5, 7, ref badHabitSlips);

        lines.Should().ContainSingle();
        lines[0].Should().Contain("5/7");
        lines[0].Should().Contain("71%");
        badHabitSlips.Should().Be(0);
    }

    [Fact]
    public void AppendParentHabitLine_ZeroScheduled_ShowsZeroPercent()
    {
        var lines = new List<string>();
        var habit = CreateDailyHabit("Read");
        var badHabitSlips = 0;

        InvokeAppendParentHabitLine(lines, habit, 0, 0, 7, ref badHabitSlips);

        lines.Should().ContainSingle();
        lines[0].Should().Contain("0%");
    }

    [Fact]
    public void AppendParentHabitLine_PerfectCompletion_Shows100Percent()
    {
        var lines = new List<string>();
        var habit = CreateDailyHabit("Meditate");
        var badHabitSlips = 0;

        InvokeAppendParentHabitLine(lines, habit, 7, 7, 7, ref badHabitSlips);

        lines[0].Should().Contain("100%");
    }

    // ── BuildHabitBreakdown via reflection ──

    [Fact]
    public void BuildHabitBreakdown_NoActivity_ReturnsNoHabitActivity()
    {
        // A habit with no logs and no scheduled dates in the range
        var futureHabit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Future", FrequencyUnit.Day, 1,
            DueDate: DateTo.AddDays(10))).Value;

        var result = InvokeBuildHabitBreakdown([futureHabit], DateFrom, DateTo, 7);

        result.HabitSection.Should().Contain("(no habit activity)");
        result.TotalCompletions.Should().Be(0);
        result.TotalScheduled.Should().Be(0);
    }

    [Fact]
    public void BuildHabitBreakdown_OnlyChildHabits_AreSkippedAtTopLevel()
    {
        var parent = CreateDailyHabit("Workout");
        var child = CreateDailyHabit("Push-ups", parentId: parent.Id);

        // Pass only the child (no parent) -- it should be skipped since it has a ParentHabitId
        var result = InvokeBuildHabitBreakdown([child], DateFrom, DateTo, 7);

        result.HabitSection.Should().Contain("(no habit activity)");
    }

    [Fact]
    public void BuildHabitBreakdown_BadHabitSlipsTrackedSeparately()
    {
        var badHabit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Smoking", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: DateFrom)).Value;

        // Log some slips
        badHabit.Log(DateFrom);
        badHabit.Log(DateFrom.AddDays(1));

        var result = InvokeBuildHabitBreakdown([badHabit], DateFrom, DateTo, 7);

        result.BadHabitSlips.Should().Be(2);
        result.HabitSection.Should().Contain("bad habit");
        result.HabitSection.Should().Contain("2 slips");
    }

    // ── Helpers ──

    private static Habit CreateDailyHabit(
        string title,
        bool isBadHabit = false,
        Guid? parentId = null)
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            title,
            FrequencyUnit.Day,
            1,
            IsBadHabit: isBadHabit,
            DueDate: DateFrom,
            ParentHabitId: parentId)).Value;
    }

    private static string InvokeBuildRetrospectivePrompt(
        List<Habit> habits, DateOnly dateFrom, DateOnly dateTo, string period, string language)
    {
        var method = typeof(AiRetrospectiveService)
            .GetMethod("BuildRetrospectivePrompt", PrivateStatic)!;
        return (string)method.Invoke(null, [habits, dateFrom, dateTo, period, language])!;
    }

    private static void InvokeAppendParentHabitLine(
        List<string> lines, Habit habit, int scheduledCount, int completedCount, int totalDays, ref int badHabitSlips)
    {
        var method = typeof(AiRetrospectiveService)
            .GetMethod("AppendParentHabitLine", PrivateStatic)!;
        var args = new object[] { lines, habit, scheduledCount, completedCount, totalDays, badHabitSlips };
        method.Invoke(null, args);
        badHabitSlips = (int)args[5];
    }

    private static (string HabitSection, int TotalCompletions, int TotalScheduled, int BadHabitSlips)
        InvokeBuildHabitBreakdown(List<Habit> habits, DateOnly dateFrom, DateOnly dateTo, int totalDays)
    {
        var method = typeof(AiRetrospectiveService)
            .GetMethod("BuildHabitBreakdown", PrivateStatic)!;
        return ((string, int, int, int))method.Invoke(null, [habits, dateFrom, dateTo, totalDays])!;
    }
}
