using System.Reflection;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
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
        var text = "```\nSome code:\n```inner```\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("Some code:");
    }

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

        result.Should().Contain("10 days");
    }

    [Fact]
    public void BuildRetrospectivePrompt_CountsOnlyTopLevelHabits()
    {
        var parent = CreateDailyHabit("Workout");
        var child = CreateDailyHabit("Push-ups", parentId: parent.Id);
        var habits = new List<Habit> { parent, child };

        var result = InvokeBuildRetrospectivePrompt(habits, DateFrom, DateTo, "weekly", "en");

        result.Should().Contain("Total habits tracked: 1");
    }

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

    [Fact]
    public void AppendParentHabitLine_OverLogged_CapsCompletionAt100Percent()
    {
        var lines = new List<string>();
        var habit = CreateDailyHabit("Meds");
        var badHabitSlips = 0;

        InvokeAppendParentHabitLine(lines, habit, 1, 7, 7, ref badHabitSlips);

        lines.Should().ContainSingle();
        lines[0].Should().Contain("1/1");
        lines[0].Should().Contain("100%");
        lines[0].Should().NotContain("700");
    }

    [Fact]
    public void BuildHabitBreakdown_NoActivity_ReturnsNoHabitActivity()
    {
        var futureHabit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Future", FrequencyUnit.Day, 1,
            DueDate: DateTo.AddDays(10))).Value;

        var result = InvokeBuildHabitBreakdown([futureHabit], DateFrom, DateTo, 7);

        result.HabitSection.Should().Contain("(no habit activity)");
        result.TotalMet.Should().Be(0);
        result.TotalScheduled.Should().Be(0);
    }

    [Fact]
    public void BuildHabitBreakdown_OnlyChildHabits_AreSkippedAtTopLevel()
    {
        var parent = CreateDailyHabit("Workout");
        var child = CreateDailyHabit("Push-ups", parentId: parent.Id);

        var result = InvokeBuildHabitBreakdown([child], DateFrom, DateTo, 7);

        result.HabitSection.Should().Contain("(no habit activity)");
    }

    [Fact]
    public void BuildHabitBreakdown_BadHabitSlipsTrackedSeparately()
    {
        var badHabit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Smoking", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: DateFrom)).Value;

        badHabit.Log(DateFrom);
        badHabit.Log(DateFrom.AddDays(1));

        var result = InvokeBuildHabitBreakdown([badHabit], DateFrom, DateTo, 7);

        result.BadHabitSlips.Should().Be(2);
        result.HabitSection.Should().Contain("bad habit");
        result.HabitSection.Should().Contain("2 slips");
    }

    [Fact]
    public void BuildHabitBreakdown_OverLoggedHabit_CapsAt100AndDoesNotInflateTotals()
    {
        var habit = Habit.Create(new HabitCreateParams(
            ValidUserId, "Meds", FrequencyUnit.Week, 1, DueDate: DateFrom)).Value;
        for (var i = 0; i < 7; i++)
            habit.Log(DateFrom.AddDays(i), advanceDueDate: false);

        var result = InvokeBuildHabitBreakdown([habit], DateFrom, DateTo, 7);

        result.HabitSection.Should().Contain("1/1");
        result.HabitSection.Should().NotContain("700");
        result.TotalMet.Should().Be(1);
        result.TotalScheduled.Should().Be(1);
    }

    [Fact]
    public void ParseNarrative_English_SplitsAllFourSections()
    {
        var text =
            "**Highlights**\nYou nailed Exercise at 100%.\n\n" +
            "**Missed Opportunities**\nReading slipped to 20%.\n\n" +
            "**Trends**\nMornings are your strongest window.\n\n" +
            "**Suggestion**\nSchedule Reading right after lunch.";

        var result = InvokeParseNarrative(text, "en");

        result.Highlights.Should().Be("You nailed Exercise at 100%.");
        result.Missed.Should().Be("Reading slipped to 20%.");
        result.Trends.Should().Be("Mornings are your strongest window.");
        result.Suggestion.Should().Be("Schedule Reading right after lunch.");
    }

    [Fact]
    public void ParseNarrative_NumberedHeadings_StripsNumberAndTrailingDescription()
    {
        var text =
            "1. **Highlights** -- what went well\nGreat consistency.\n" +
            "2. **Missed Opportunities** -- gaps\nSkipped weekends.\n" +
            "3. **Trends** -- patterns\nImproving steadily.\n" +
            "4. **Suggestion** -- next step\nKeep the streak alive.";

        var result = InvokeParseNarrative(text, "en");

        result.Highlights.Should().Be("Great consistency.");
        result.Missed.Should().Be("Skipped weekends.");
        result.Trends.Should().Be("Improving steadily.");
        result.Suggestion.Should().Be("Keep the streak alive.");
    }

    [Fact]
    public void ParseNarrative_Portuguese_SplitsAllFourSections()
    {
        var text =
            "**Destaques**\nVoce mandou bem.\n\n" +
            "**Oportunidades Perdidas**\nFaltou leitura.\n\n" +
            "**Tendências**\nMelhorando.\n\n" +
            "**Sugestão**\nContinue assim.";

        var result = InvokeParseNarrative(text, "pt-br");

        result.Highlights.Should().Be("Voce mandou bem.");
        result.Missed.Should().Be("Faltou leitura.");
        result.Trends.Should().Be("Melhorando.");
        result.Suggestion.Should().Be("Continue assim.");
    }

    [Fact]
    public void ParseNarrative_UnparseableText_FallsBackToHighlights()
    {
        var text = "Just a single block of prose with no headings at all.";

        var result = InvokeParseNarrative(text, "en");

        result.Highlights.Should().Be(text);
        result.Missed.Should().BeEmpty();
        result.Trends.Should().BeEmpty();
        result.Suggestion.Should().BeEmpty();
    }

    [Fact]
    public void ParseNarrative_PartialHeadings_FallsBackToHighlights()
    {
        var text = "**Highlights**\nGood week.\n**Trends**\nUp and to the right.";

        var result = InvokeParseNarrative(text, "en");

        result.Highlights.Should().Be(text);
        result.Missed.Should().BeEmpty();
    }

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

    private static (string HabitSection, int TotalMet, int TotalScheduled, int BadHabitSlips)
        InvokeBuildHabitBreakdown(List<Habit> habits, DateOnly dateFrom, DateOnly dateTo, int totalDays)
    {
        var method = typeof(AiRetrospectiveService)
            .GetMethod("BuildHabitBreakdown", PrivateStatic)!;
        return ((string, int, int, int))method.Invoke(null, [habits, dateFrom, dateTo, totalDays])!;
    }

    private static RetrospectiveNarrative InvokeParseNarrative(string text, string language)
    {
        var method = typeof(AiRetrospectiveService)
            .GetMethod("ParseNarrative", PrivateStatic)!;
        return (RetrospectiveNarrative)method.Invoke(null, [text, language])!;
    }
}
