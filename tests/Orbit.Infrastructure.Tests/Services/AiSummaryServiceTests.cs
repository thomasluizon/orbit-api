#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure prompt/selection logic in AiSummaryService (StripMarkdownFences,
/// BuildSummaryPrompt, BuildHabitSection, CapToSentence, SelectScheduledHabits) plus the
/// structured-output mapping of GenerateSummaryAsync (summary parsing, with Insight always
/// returned empty) against a stubbed chat transport.
/// </summary>
public partial class AiSummaryServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2025, 4, 1);

    [Fact]
    public async Task GenerateSummaryAsync_ReservesOutputTokensForReasoningAndJson()
    {
        string? body = null;
        var service = BuildSummaryService("{\"summary\":\"You kept going.\"}", request => body = request);

        var result = await InvokeGenerateSummaryAsync(service);

        result.IsSuccess.Should().BeTrue();
        using var document = JsonDocument.Parse(body!);
        document.RootElement.GetProperty("model").GetString().Should().Be("gpt-6-luna");
        document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        document.RootElement.GetProperty("max_output_tokens").GetInt32().Should().Be(8192);
    }

    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    private static readonly string[] BannedInstantPhrases =
    [
        "right now",
        "just woke up",
        "now that the afternoon is here",
        "as the day begins",
        "earlier today",
        "upcoming later today",
    ];

    [Fact]
    public void StripMarkdownFences_PlainText_ReturnsUnchanged()
    {
        var text = "Just a plain summary.";
        AiSummaryService.StripMarkdownFences(text).Should().Be(text);
    }

    [Fact]
    public void StripMarkdownFences_WithFences_RemovesThem()
    {
        var text = "```\nHello world\n```";
        AiSummaryService.StripMarkdownFences(text).Should().Be("Hello world");
    }

    [Fact]
    public void StripMarkdownFences_WithLanguageFences_RemovesThem()
    {
        var text = "```markdown\nContent here\n```";
        AiSummaryService.StripMarkdownFences(text).Should().Be("Content here");
    }

    [Fact]
    public void StripMarkdownFences_FencesWithoutClosing_RemovesOpening()
    {
        var text = "```\nNo closing fence";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Be("No closing fence");
    }

    [Fact]
    public void StripMarkdownFences_WhitespaceOnly_ReturnsTrimmed()
    {
        var text = "   \n   ";
        AiSummaryService.StripMarkdownFences(text).Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_MultipleLines_PreservesContent()
    {
        var text = "```\nLine 1\nLine 2\nLine 3\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("Line 1");
        result.Should().Contain("Line 2");
        result.Should().Contain("Line 3");
    }

    [Fact]
    public void StripMarkdownFences_LeadingTrailingWhitespace_Trims()
    {
        var text = "  \n  Some text  \n  ";
        AiSummaryService.StripMarkdownFences(text).Should().Be("Some text");
    }

    [Fact]
    public void BuildSummaryPrompt_English_ContainsEnglishLanguageName()
    {
        var habits = new List<Habit> { CreateHabit("Yoga") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "en");

        result.Should().Contain("English");
        result.Should().Contain("Yoga");
    }

    [Fact]
    public void BuildSummaryPrompt_Portuguese_ContainsBrazilianPortuguese()
    {
        var habits = new List<Habit> { CreateHabit("Leitura") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "pt-br");

        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildSummaryPrompt_PtShort_AlsoMapsToPortuguese()
    {
        var habits = new List<Habit> { CreateHabit("Read") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "pt");

        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildSummaryPrompt_IncludesDateInOutput()
    {
        var habits = new List<Habit> { CreateHabit("Walk") };
        var date = new DateOnly(2025, 12, 25);

        var result = InvokeBuildSummaryPrompt(habits, date, "en");

        result.Should().Contain("December 25, 2025");
    }

    [Fact]
    public void BuildSummaryPrompt_ShowsProgressCount()
    {
        var habits = new List<Habit>
        {
            CreateHabit("A"),
            CreateHabit("B"),
            CreateHabit("C"),
        };

        var result = InvokeBuildSummaryPrompt(habits, Today, "en");

        result.Should().Contain("0/3 habits completed");
    }

    [Fact]
    public void BuildHabitSection_EmptyList_ReturnsNoHabitsScheduled()
    {
        var result = InvokeBuildHabitSection([]);

        result.Should().Be("(no habits scheduled)");
    }

    [Fact]
    public void BuildHabitSection_SinglePendingHabit_ShowsPending()
    {
        var habits = new List<Habit> { CreateHabit("Yoga") };

        var result = InvokeBuildHabitSection(habits);

        result.Should().Contain("Yoga (pending)");
    }

    [Fact]
    public void BuildHabitSection_ParentWithChildren_ShowsSubTaskCount()
    {
        var parent = CreateHabit("Workout");
        var child1 = CreateHabit("Push-ups", parentId: parent.Id);
        var child2 = CreateHabit("Sit-ups", parentId: parent.Id);

        var result = InvokeBuildHabitSection([parent, child1, child2]);

        result.Should().Contain("Workout (pending, 0/2 sub-tasks done)");
        result.Should().Contain("Push-ups (pending)");
        result.Should().Contain("Sit-ups (pending)");
    }

    [Fact]
    public void BuildHabitSection_ChildrenOnly_ReturnsNoHabitsScheduled()
    {
        var parent = CreateHabit("Workout");
        var child = CreateHabit("Push-ups", parentId: parent.Id);

        var result = InvokeBuildHabitSection([child]);

        result.Should().Be("(no habits scheduled)");
    }

    [Fact]
    public void BuildHabitSection_HabitWithDueTime_ShowsAbsoluteDueTime()
    {
        var habits = new List<Habit> { CreateHabit("Workout", dueTime: new TimeOnly(14, 0)) };

        var result = InvokeBuildHabitSection(habits);

        result.Should().Contain("due 14:00");
    }

    [Fact]
    public void BuildSummaryPrompt_UnknownLanguage_DefaultsToEnglish()
    {
        var habits = new List<Habit> { CreateHabit("Test") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "fr");

        result.Should().Contain("English");
    }

    [Fact]
    public void BuildSummaryPrompt_EmptyHabitList_ShowsNoHabitsScheduled()
    {
        var result = InvokeBuildSummaryPrompt([], Today, "en");

        result.Should().Contain("(no habits scheduled)");
        result.Should().Contain("0/0 habits completed");
    }

    [Fact]
    public void BuildSummaryPrompt_LeadsWithOneInsightInsteadOfARecap()
    {
        var habits = new List<Habit> { CreateHabit("Walk") };
        var result = InvokeBuildSummaryPrompt(habits, Today, "en");

        result.Should().Contain("Rules:");
        result.Should().Contain("EXACTLY ONE sentence");
        result.Should().Contain("Lead with the insight");
        result.Should().Contain("Name the specific habit");
        result.Should().Contain("A recap of completed activities is not an insight");
        result.Should().NotContain("LEAD with a specific, genuine acknowledgment");
        result.Should().NotContain("TWO short sentences");
        result.Should().Contain("friend who actually knows you");
        result.Should().Contain("Do NOT use markdown");
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("pt-BR", "Brazilian Portuguese")]
    public async Task GenerateSummaryAsync_RequestsOneNamedInsightWithTruthRules(string locale, string languageName)
    {
        var read = CreateHabit("Read");
        string? body = null;
        const string sentence = "Read is still open this evening; choose it as your next step.";
        var service = BuildSummaryService(JsonSerializer.Serialize(new { summary = sentence }), request => body = request);

        var result = await service.GenerateSummaryAsync([read],
            new DailySummaryContext(Today, Today, Today, locale, new TimeOnly(19, 0), 38, 0,
                new Dictionary<Guid, DateOnly>()));

        result.Value.Summary.Should().Be(sentence);
        using var document = JsonDocument.Parse(body!);
        var request = document.RootElement;
        var prompt = request.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        prompt.Should().Contain("EXACTLY ONE sentence").And.Contain("Lead with the insight");
        prompt.Should().Contain("Name the specific habit").And.Contain("Read (pending)");
        prompt.Should().Contain("A recap of completed activities is not an insight");
        prompt.Should().Contain("Never invent numbers");
        prompt.Should().Contain("Never praise consistency when skipped or missed is nonzero");
        prompt.Should().Contain("Never attribute the app-wide streak to a single habit");
        prompt.Should().Contain("never congratulate it").And.Contain("Never praise a skip or nudge it again");
        prompt.Should().Contain(languageName);
        request.GetProperty("model").GetString().Should().Be("gpt-6-luna");
        request.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        request.TryGetProperty("temperature", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildSummaryPrompt_ContextDoesNotSeedBannedInstantPhrases()
    {
        var habits = new List<Habit>
        {
            CreateHabit("Morning routine"),
            CreateHabit("Evening walk", dueTime: new TimeOnly(20, 0)),
        };

        var result = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(19, 0));

        var context = result[..result.IndexOf("Rules:", StringComparison.Ordinal)];

        foreach (var phrase in BannedInstantPhrases)
            context.Should().NotContain(phrase, $"the prompt context must not seed the instant-phrase \"{phrase}\"");
    }

    [Fact]
    public void BuildSummaryPrompt_RulesBanEveryInstantPhrase()
    {
        var habits = new List<Habit> { CreateHabit("Walk") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(14, 0));

        var rules = result[result.IndexOf("Rules:", StringComparison.Ordinal)..];
        foreach (var phrase in BannedInstantPhrases)
            rules.Should().Contain(phrase, $"the rules must explicitly ban \"{phrase}\"");
    }

    [Fact]
    public void BuildSummaryPrompt_PeriodRobustTimeContext_UsesRangeNotExactMinute()
    {
        var habits = new List<Habit> { CreateHabit("Evening walk") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(19, 0));

        result.Should().Contain("Current part of day: evening (~5pm-9pm)");
        result.Should().NotContain("19:00");
    }

    [Fact]
    public void BuildSummaryPrompt_PeriodRobustTimeContext_StartOfPeriodMatchesEnd()
    {
        var habits = new List<Habit> { CreateHabit("Afternoon focus") };

        var startOfAfternoon = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(11, 0));
        var endOfAfternoon = InvokeBuildSummaryPrompt(habits, Today, "en", new TimeOnly(16, 59));

        startOfAfternoon.Should().Contain("afternoon (~11am-5pm)");
        endOfAfternoon.Should().Contain("afternoon (~11am-5pm)");
    }

    [Fact]
    public void BuildSummaryPrompt_UpperCaseLanguage_StillMapsCorrectly()
    {
        var habits = new List<Habit> { CreateHabit("Test") };

        var result = InvokeBuildSummaryPrompt(habits, Today, "PT-BR");

        result.Should().Contain("Brazilian Portuguese");
    }

    [Fact]
    public void BuildSummaryPrompt_EmptyLanguageString_DefaultsToEnglish()
    {
        var habits = new List<Habit> { CreateHabit("Test") };
        var result = InvokeBuildSummaryPrompt(habits, Today, "");

        result.Should().Contain("English");
    }

    [Fact]
    public void BuildHabitSection_MultipleParentsNoChildren_ListsAllPending()
    {
        var habits = new List<Habit>
        {
            CreateHabit("Yoga"),
            CreateHabit("Read"),
            CreateHabit("Cook"),
        };

        var result = InvokeBuildHabitSection(habits);

        result.Should().Contain("Yoga (pending)");
        result.Should().Contain("Read (pending)");
        result.Should().Contain("Cook (pending)");
    }

    [Fact]
    public void BuildHabitSection_ParentWithMultipleChildrenGroups_ShowsAllHierarchies()
    {
        var parent1 = CreateHabit("Workout");
        var child1a = CreateHabit("Push-ups", parentId: parent1.Id);
        var child1b = CreateHabit("Sit-ups", parentId: parent1.Id);

        var parent2 = CreateHabit("Study");
        var child2a = CreateHabit("Math", parentId: parent2.Id);

        var result = InvokeBuildHabitSection([parent1, child1a, child1b, parent2, child2a]);

        result.Should().Contain("Workout (pending, 0/2 sub-tasks done)");
        result.Should().Contain("Study (pending, 0/1 sub-tasks done)");
        result.Should().Contain("Push-ups (pending)");
        result.Should().Contain("Sit-ups (pending)");
        result.Should().Contain("Math (pending)");
    }

    [Fact]
    public void StripMarkdownFences_OnlyFences_ReturnsEmpty()
    {
        var text = "```\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_FencesWithExtraBlankLines_StripsAndTrims()
    {
        var text = "```\n\nContent with blanks\n\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Contain("Content with blanks");
    }

    [Fact]
    public void StripMarkdownFences_InlineFences_NotStripped()
    {
        var text = "Use `code` for inline formatting";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Be(text);
    }

    [Fact]
    public void StripMarkdownFences_FencesWithJsonLanguage_Stripped()
    {
        var text = "```json\n{\"key\": \"value\"}\n```";
        var result = AiSummaryService.StripMarkdownFences(text);
        result.Should().Be("{\"key\": \"value\"}");
    }

    [Fact]
    public void CapToSentence_UnderLimit_ReturnsUnchanged()
    {
        var text = "Nice work getting your run in today.";

        AiSummaryService.CapToSentence(text, 200).Should().Be(text);
    }

    [Fact]
    public void CapToSentence_ExactlyAtLimit_ReturnsUnchanged()
    {
        var text = new string('a', 200);

        AiSummaryService.CapToSentence(text, 200).Should().Be(text);
    }

    [Fact]
    public void CapToSentence_OverLimit_CutsAtLastSentenceBoundary()
    {
        var text = "You crushed the morning run. The afternoon reading is still open. Maybe stretch before bed too, no rush at all here.";

        var result = AiSummaryService.CapToSentence(text, 70);

        result.Should().Be("You crushed the morning run. The afternoon reading is still open.");
        result.Length.Should().BeLessThanOrEqualTo(70);
    }

    [Fact]
    public void CapToSentence_HonorsQuestionAndExclamationBoundaries()
    {
        var text = "Did you finish the run yet? You logged plenty already so take it easy now.";

        var result = AiSummaryService.CapToSentence(text, 40);

        result.Should().Be("Did you finish the run yet?");
    }

    [Fact]
    public void CapToSentence_NoSentenceBoundary_CutsAtWordBoundaryWithoutSplittingWords()
    {
        var text = "great work today you really kept the momentum going strong across everything";

        var result = AiSummaryService.CapToSentence(text, 30);

        result.Length.Should().BeLessThanOrEqualTo(30);
        result.Should().Be("great work today you really");
        text.Should().StartWith(result);
        result.Should().NotEndWith(" ");
    }

    [Fact]
    public void CapToSentence_NeverExceedsTwoHundredChars()
    {
        var text = string.Join(' ', Enumerable.Repeat("habit", 100));

        var result = AiSummaryService.CapToSentence(text, 200);

        result.Length.Should().BeLessThanOrEqualTo(200);
        result.Should().NotEndWith(" ");
        result.Split(' ').Should().OnlyContain(word => word == "habit");
    }

    [Fact]
    public void BuildSummaryPrompt_NewYearsDay_FormatsDateCorrectly()
    {
        var habits = new List<Habit> { CreateHabit("Celebrate") };
        var date = new DateOnly(2026, 1, 1);

        var result = InvokeBuildSummaryPrompt(habits, date, "en");

        result.Should().Contain("January 1, 2026");
    }

    [Fact]
    public void BuildSummaryPrompt_AllHabitsPresent_ShowsCorrectTotal()
    {
        var habits = new List<Habit>
        {
            CreateHabit("A"),
            CreateHabit("B"),
            CreateHabit("C"),
            CreateHabit("D"),
            CreateHabit("E"),
        };

        var result = InvokeBuildSummaryPrompt(habits, Today, "en");

        result.Should().Contain("0/5 habits completed");
    }

    [Fact]
    public void BuildHabitSection_Children_IndentedWithTwoSpaces()
    {
        var parent = CreateHabit("Parent");
        var child = CreateHabit("Child", parentId: parent.Id);

        var result = InvokeBuildHabitSection([parent, child]);

        result.Should().Contain("  - Child (pending)");
    }

    [Fact]
    public void SelectScheduledHabits_DailyGoodHabitLoggedToday_KeptAndLabeledDoneNotPending()
    {
        var habit = CreateHabit("Café da manhã");
        habit.Log(Today);

        var selected = InvokeSelectScheduledHabits([habit], Today);

        selected.Should().ContainSingle();
        var section = InvokeBuildHabitSection(selected);
        section.Should().Contain("Café da manhã (done)");
        section.Should().NotContain("pending");
    }

    [Fact]
    public void SelectScheduledHabits_WeeklyGoodHabitLoggedToday_KeptAndLabeledDone()
    {
        var habit = CreateRecurring("Pesar na balança", FrequencyUnit.Week);
        habit.Log(Today);

        var selected = InvokeSelectScheduledHabits([habit], Today);

        selected.Should().ContainSingle();
        InvokeBuildHabitSection(selected).Should().Contain("Pesar na balança (done)");
    }

    [Fact]
    public void SelectScheduledHabits_WeeklyChildResolvedOnItsOccurrence_NotSelected()
    {
        var parent = CreateHabit("Morning routine");
        var weeklyChild = CreateRecurring(
            "Pesar na balança", FrequencyUnit.Week, dueDate: Today.AddDays(7), parentId: parent.Id);
        weeklyChild.Log(Today.AddDays(-7), advanceDueDate: false);

        var selected = InvokeSelectScheduledHabits([parent, weeklyChild], Today);

        selected.Should().ContainSingle(h => h.Id == parent.Id);
        selected.Should().NotContain(h => h.Id == weeklyChild.Id);
    }

    [Fact]
    public void SelectScheduledHabits_RoutineWithNonDueChild_DoesNotSurfaceChildAsPending()
    {
        var parent = CreateHabit("Morning routine");
        var monthlyChild = CreateRecurring(
            "Deep clean", FrequencyUnit.Month, dueDate: Today.AddDays(14), parentId: parent.Id);

        var selected = InvokeSelectScheduledHabits([parent, monthlyChild], Today);
        var section = InvokeBuildHabitSection(selected);

        section.Should().NotContain("Deep clean");
        section.Should().Contain("Morning routine (pending)");
    }

    [Fact]
    public void SelectScheduledHabits_RoutineChildrenAllLoggedToday_NoChildPending()
    {
        var parent = CreateHabit("Morning routine");
        var child1 = CreateHabit("Café da manhã", parentId: parent.Id);
        var child2 = CreateHabit("Stretch", parentId: parent.Id);
        child1.Log(Today);
        child2.Log(Today);

        var selected = InvokeSelectScheduledHabits([parent, child1, child2], Today);
        var section = InvokeBuildHabitSection(selected);

        section.Should().NotContain("pending)");
        section.Should().Contain("Café da manhã (done)");
        section.Should().Contain("Stretch (done)");
    }

    [Fact]
    public void SelectScheduledHabits_OverdueHabit_StillSelected()
    {
        var overdue = CreateRecurring("Take vitamins", FrequencyUnit.Day, dueDate: Today.AddDays(-2));

        var selected = InvokeSelectScheduledHabits([overdue], Today);

        selected.Should().ContainSingle();
        InvokeBuildHabitSection(selected).Should().Contain("Take vitamins (pending)");
    }

    [Fact]
    public void SelectScheduledHabits_OneTimeTaskCompletedOnPastDay_NotSelected()
    {
        var task = CreateOneTimeTask("Organizar viagem", dueDate: Today.AddDays(-5));
        task.Log(Today.AddDays(-5));
        var selected = InvokeSelectScheduledHabits([task], Today);

        selected.Should().BeEmpty();
    }

    [Fact]
    public void SelectScheduledHabits_OneTimeTaskDueTodayButCompletedEarlier_NotSelected()
    {
        var task = CreateOneTimeTask("Depilação a laser", dueDate: Today);
        task.Log(Today.AddDays(-3));
        var selected = InvokeSelectScheduledHabits([task], Today);

        selected.Should().BeEmpty();
    }

    [Fact]
    public void SelectScheduledHabits_OneTimeTaskDueInFuture_NotSelected()
    {
        var task = CreateOneTimeTask("Consulta médica", dueDate: Today.AddDays(4));

        var selected = InvokeSelectScheduledHabits([task], Today);

        selected.Should().BeEmpty();
    }

    [Fact]
    public void SelectScheduledHabits_RecurringHabitDueInFutureNotLogged_NotSelected()
    {
        var habit = CreateRecurring("Pesar na balança", FrequencyUnit.Week, dueDate: Today.AddDays(5));

        var selected = InvokeSelectScheduledHabits([habit], Today);

        selected.Should().BeEmpty();
    }

    [Fact]
    public void SelectScheduledHabits_OneTimeTaskCompletedToday_SelectedAndDone()
    {
        var task = CreateOneTimeTask("Pagar conta", dueDate: Today);
        task.Log(Today);

        var selected = InvokeSelectScheduledHabits([task], Today);

        selected.Should().ContainSingle();
        InvokeBuildHabitSection(selected).Should().Contain("Pagar conta (done)");
    }

    [Fact]
    public void SelectScheduledHabits_OneTimeTaskDueTodayNotDone_SelectedAndPending()
    {
        var task = CreateOneTimeTask("Ligar para o médico", dueDate: Today);

        var selected = InvokeSelectScheduledHabits([task], Today);

        selected.Should().ContainSingle();
        InvokeBuildHabitSection(selected).Should().Contain("Ligar para o médico (pending)");
    }

    [Fact]
    public void SelectScheduledHabits_OneTimeTaskOverdueNotDone_SelectedAsPending()
    {
        var task = CreateOneTimeTask("Renovar passaporte", dueDate: Today.AddDays(-3));

        var selected = InvokeSelectScheduledHabits([task], Today);

        selected.Should().ContainSingle();
        InvokeBuildHabitSection(selected, Today).Should().Contain("Renovar passaporte (pending, overdue)");
    }

    [Fact]
    public void BuildHabitSection_BadHabitLoggedToday_FramedAsSlipNotDone()
    {
        var badHabit = CreateBadHabit("Skip Gym");
        badHabit.Log(Today);

        var section = InvokeBuildHabitSection([badHabit]);

        section.Should().Contain("Skip Gym (bad habit, slipped)");
        section.Should().NotContain("(done)");
    }

    [Fact]
    public void BuildHabitSection_BadHabitCleanWithPriorSlip_FramedAsCleanWinWithDaysSinceSlip()
    {
        var badHabit = CreateBadHabit("Cheat diet");
        var lastSlips = new Dictionary<Guid, DateOnly> { [badHabit.Id] = Today.AddDays(-3) };

        var section = InvokeBuildHabitSection([badHabit], Today, lastSlips);

        section.Should().Contain("Cheat diet (bad habit, clean, 3 days since last slip)");
        section.Should().NotContain("(done)");
        section.Should().NotContain("slipped");
    }

    [Fact]
    public void BuildHabitSection_BadHabitCleanWithPriorSlip_IgnoresWindowedHabitLogsUsesSlipMap()
    {
        var badHabit = CreateBadHabit("Cheat diet");
        var lastSlips = new Dictionary<Guid, DateOnly> { [badHabit.Id] = Today.AddDays(-7) };

        var section = InvokeBuildHabitSection([badHabit], Today, lastSlips);

        section.Should().Contain("Cheat diet (bad habit, clean, 7 days since last slip)");
    }

    [Fact]
    public void BuildHabitSection_BadHabitNoEntryInSlipMap_FramedAsCleanNoSlipsOnRecord()
    {
        var badHabit = CreateBadHabit("Smoke");

        var section = InvokeBuildHabitSection([badHabit], Today);

        section.Should().Contain("Smoke (bad habit, clean, no slips on record)");
    }

    [Fact]
    public void BuildSummaryPrompt_BadHabitSlip_ExcludedFromCompletedTally()
    {
        var goodDone = CreateHabit("Drink water");
        goodDone.Log(Today);
        var badSlip = CreateBadHabit("Skip Gym");
        badSlip.Log(Today);

        var result = InvokeBuildSummaryPrompt([goodDone, badSlip], Today, "en");

        result.Should().Contain("Progress: 1/1 habits completed");
        result.Should().Contain("Bad habit slips today: 1");
    }

    [Fact]
    public void BuildSummaryPrompt_RulesContainWinVsSlipGuidance()
    {
        var badHabit = CreateBadHabit("Skip Gym");

        var result = InvokeBuildSummaryPrompt([badHabit], Today, "en");

        result.Should().Contain("bad habit, slipped");
        result.Should().Contain("never congratulate it");
        result.Should().Contain("bad habit, clean");
        result.Should().Contain("the real win worth naming");
    }

    [Fact]
    public void BuildSummaryPrompt_OverdueGoodHabit_MarkedOverdueInSection()
    {
        var overdue = CreateOneTimeTask("Renew passport", dueDate: Today.AddDays(-2));

        var result = InvokeBuildSummaryPrompt([overdue], Today, "en");

        result.Should().Contain("Renew passport (pending, overdue)");
    }

    [Fact]
    public void BuildSummaryPrompt_StreakAndFreezesProvided_IncludedInContextHeader()
    {
        var habit = CreateHabit("Walk");

        var result = InvokeBuildSummaryPrompt([habit], Today, "en", currentStreak: 12, streakFreezesAccumulated: 2);

        result.Should().Contain("App-wide active-day streak: 12 days");
        result.Should().Contain("Streak freezes banked: 2");
        result.Should().Contain("Never attribute the app-wide streak to a single habit");
    }

    [Fact]
    public void BuildSummaryPrompt_NoStreak_OmitsStreakLines()
    {
        var habit = CreateHabit("Walk");

        var result = InvokeBuildSummaryPrompt([habit], Today, "en");

        result.Should().NotContain("App-wide active-day streak:");
        result.Should().NotContain("Streak freezes banked:");
    }

    [Fact]
    public async Task GenerateSummaryAsync_ReturnsSummaryWithAlwaysEmptyInsight()
    {
        var service = BuildSummaryService(
            "{\"summary\":\"Nice work getting the **run** in.\"}");

        var result = await InvokeGenerateSummaryAsync(service);

        result.IsSuccess.Should().BeTrue();
        result.Value.Summary.Should().Be("Nice work getting the run in.");
        result.Value.Insight.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateSummaryAsync_PreservesLiteralHabitNameWhileRemovingEmphasis()
    {
        var habit = CreateHabit("Read *daily*");
        var service = BuildSummaryService("{\"summary\":\"Nice work with **Read *daily***.\"}");

        var result = await service.GenerateSummaryAsync([habit],
            new DailySummaryContext(Today, Today, Today, "en", null, 0, 0, new Dictionary<Guid, DateOnly>()));

        result.Value.Summary.Should().Be("Nice work with Read *daily*.");
    }

    [Fact]
    public async Task GenerateSummaryAsync_ModelStillReturnsInsight_InsightDroppedToEmpty()
    {
        var service = BuildSummaryService(
            "{\"summary\":\"Solid day so far.\",\"insight\":\"A little reading later could wind you down.\"}");

        var result = await InvokeGenerateSummaryAsync(service);

        result.IsSuccess.Should().BeTrue();
        result.Value.Summary.Should().Be("Solid day so far.");
        result.Value.Insight.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateSummaryAsync_BlankSummary_ReturnsFailure()
    {
        var service = BuildSummaryService("{\"summary\":\"   \"}");

        var result = await InvokeGenerateSummaryAsync(service);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateSummaryAsync_RecentSkipsAndMisses_AppearInActualPrompt()
    {
        var gym = CreateHabit("Gym", dueDate: Today.AddDays(-13));
        gym.LoadScheduleLogsForRead([
            HabitLog.FromSummaryRead(gym.Id, Today.AddDays(-14), 1),
            HabitLog.FromSummaryRead(gym.Id, Today.AddDays(-3), 0),
            HabitLog.FromSummaryRead(gym.Id, Today.AddDays(-2), 1),
            HabitLog.FromSummaryRead(gym.Id, Today.AddDays(-1), 1)]);
        string? requestBody = null;
        var service = BuildSummaryService("{\"summary\":\"Take it easy today.\"}", body => requestBody = body);

        var result = await service.GenerateSummaryAsync([gym],
            new DailySummaryContext(Today, Today, Today, "en", null, 38, 0, new Dictionary<Guid, DateOnly>()));

        result.IsSuccess.Should().BeTrue();
        requestBody.Should().Contain("App-wide active-day streak: 38 days");
        requestBody.Should().Contain("14-day history: done=2, skipped=1, missed=10, habit streak=2 scheduled occurrences");
        requestBody.Should().Contain("Never praise consistency when skipped or missed is nonzero");
    }

    [Fact]
    public void SelectScheduledHabits_SkippedToday_VisibleAndNotPending()
    {
        var habit = CreateHabit("Gym");
        habit.LoadScheduleLogsForRead([HabitLog.FromSummaryRead(habit.Id, Today, 0)]);

        var selected = InvokeSelectScheduledHabits([habit], Today);

        selected.Should().ContainSingle();
        InvokeBuildHabitSection(selected).Should().Contain("Gym (skipped)").And.NotContain("pending");
    }

    [Fact]
    public void SelectScheduledHabits_FlexibleQuotaSatisfied_NotSelectedAsPending()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, ValidUserId, "Gym", FrequencyUnit.Week, 1,
            DueDate: Today.AddDays(-7))).Value;
        habit.Log(Today.AddDays(-1));

        InvokeSelectScheduledHabits([habit], Today).Should().BeEmpty();
    }

    [Fact]
    public void SelectScheduledHabits_NonDueWeekdayWithResolvedPastOccurrences_NotSelected()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, ValidUserId, "Gym", FrequencyUnit.Day, 1,
            DueDate: Today.AddDays(-1), Days: [Today.AddDays(-1).DayOfWeek])).Value;
        habit.LoadScheduleLogsForRead([HabitLog.FromSummaryRead(habit.Id, Today.AddDays(-1), 0)]);
        var due = CreateHabit("Read");

        InvokeSelectScheduledHabits([habit, due], Today).Should().ContainSingle(h => h.Id == due.Id);
    }

    [Fact]
    public void BuildHabitSection_MovingDueDate_UsesOriginalCadenceForHistory()
    {
        var habit = CreateRecurring("Weekly gym", FrequencyUnit.Week, dueDate: Today.AddDays(-13));
        habit.Log(Today.AddDays(-13));
        habit.Log(Today.AddDays(-6));

        var section = InvokeBuildHabitSection([habit]);

        section.Should().Contain("14-day history: done=2, skipped=0, missed=0, habit streak=2 scheduled occurrences");
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 1)]
    public void BuildHabitSection_RecentGap_ResetsHabitStreak(int completed, int skipped, int missed)
    {
        var habit = CreateHabit("Gym", dueDate: Today.AddDays(-3));
        var logs = new List<HabitLog>
        {
            HabitLog.FromSummaryRead(habit.Id, Today.AddDays(-3), 1),
            HabitLog.FromSummaryRead(habit.Id, Today.AddDays(-2), 1)
        };
        if (completed == 0)
            logs.Add(HabitLog.FromSummaryRead(habit.Id, Today.AddDays(-1), 0));
        habit.LoadScheduleLogsForRead(logs);

        var section = InvokeBuildHabitSection([habit]);

        section.Should().Contain($"done=2, skipped={skipped}, missed={missed}, habit streak=0 scheduled occurrences");
    }

    [Fact]
    public void BuildHabitSection_ParentAndChild_BothCarryHistoryAndSkipStatus()
    {
        var parent = CreateHabit("Workout (home)");
        var child = CreateHabit("Stretch", parentId: parent.Id);
        parent.LoadScheduleLogsForRead([HabitLog.FromSummaryRead(parent.Id, Today, 0)]);
        child.LoadScheduleLogsForRead([HabitLog.FromSummaryRead(child.Id, Today, 0)]);

        var section = InvokeBuildHabitSection([parent, child]);

        section.Should().Contain("Workout (home) (skipped, 0/1 sub-tasks done)");
        section.Should().Contain("Stretch (skipped)");
        section.Split("14-day history:").Should().HaveCount(3);
    }

    [Fact]
    public void BuildHabitSection_FlexibleHabit_DoesNotInventDailyMissesOrStreak()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, ValidUserId, "Gym", FrequencyUnit.Week, 3,
            DueDate: Today.AddDays(-13))).Value;
        habit.Log(Today.AddDays(-1));

        InvokeBuildHabitSection([habit]).Should().Contain(
            "14-day history: done=1, skipped=0, missed=unknown, habit streak=unknown (flexible quota)");
    }

    private static AiSummaryService BuildSummaryService(string modelJsonContent, Action<string>? captureRequest = null)
    {
        var chatClient = new ResponsesClient(
            credential: new ApiKeyCredential("test-key"),
            options: new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(
                    new HttpClient(new JsonResponseHandler(modelJsonContent, captureRequest))),
            });
        var aiClient = new AiCompletionClient(chatClient, NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        return new AiSummaryService(Orbit.Infrastructure.Tests.AiBudgetTestDouble.Wrap(aiClient), NullLogger<AiSummaryService>.Instance);
    }

    private static Task<Result<DailySummaryContent>> InvokeGenerateSummaryAsync(AiSummaryService service) =>
        service.GenerateSummaryAsync(
            new List<Habit>(),
            new DailySummaryContext(Today, Today, Today, "en", null, 0, 0, new Dictionary<Guid, DateOnly>()),
            CancellationToken.None);

    private sealed class JsonResponseHandler(string modelJsonContent, Action<string>? captureRequest) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captureRequest?.Invoke(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = ResponsesTestData.Text(modelJsonContent);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static Habit CreateHabit(
        string title,
        Guid? parentId = null,
        DateOnly? dueDate = null,
        TimeOnly? dueTime = null)
    {
        return Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            ValidUserId,
            title,
            FrequencyUnit.Day,
            1,
            DueDate: dueDate ?? Today,
            DueTime: dueTime,
            ParentHabitId: parentId)).Value;
    }

    private static Habit CreateRecurring(
        string title,
        FrequencyUnit unit,
        int quantity = 1,
        DateOnly? dueDate = null,
        Guid? parentId = null)
    {
        return Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            ValidUserId,
            title,
            unit,
            quantity,
            DueDate: dueDate ?? Today,
            ParentHabitId: parentId)).Value;
    }

    private static Habit CreateBadHabit(string title, DateOnly? dueDate = null)
    {
        return Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            ValidUserId,
            title,
            FrequencyUnit.Day,
            1,
            DueDate: dueDate ?? Today,
            IsBadHabit: true)).Value;
    }

    private static Habit CreateOneTimeTask(string title, DateOnly dueDate, Guid? parentId = null)
    {
        return Habit.Create(new HabitCreateParams(HabitSchedule.OneTime,
            ValidUserId,
            title,
            null,
            null,
            DueDate: dueDate,
            ParentHabitId: parentId)).Value;
    }

    private static string InvokeBuildSummaryPrompt(
        List<Habit> scheduledHabits,
        DateOnly date,
        string language,
        TimeOnly? currentLocalTime = null,
        int currentStreak = 0,
        int streakFreezesAccumulated = 0,
        IReadOnlyDictionary<Guid, DateOnly>? lastBadHabitSlipDates = null)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("BuildSummaryPrompt", PrivateStatic)!;
        return (string)method.Invoke(null,
            [scheduledHabits, new DailySummaryContext(
                date, date, date, language, currentLocalTime, currentStreak, streakFreezesAccumulated,
                lastBadHabitSlipDates ?? new Dictionary<Guid, DateOnly>())])!;
    }

    private static string InvokeBuildHabitSection(
        List<Habit> scheduledHabits,
        DateOnly? userToday = null,
        IReadOnlyDictionary<Guid, DateOnly>? lastBadHabitSlipDates = null)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("BuildHabitSection", PrivateStatic)!;
        return (string)method.Invoke(null,
            [scheduledHabits, Today, Today, userToday ?? Today,
             lastBadHabitSlipDates ?? new Dictionary<Guid, DateOnly>(), 1, new HashSet<Guid>()])!;
    }

    private static List<Habit> InvokeSelectScheduledHabits(
        List<Habit> allHabits, DateOnly userToday)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("SelectScheduledHabits", PrivateStatic)!;
        return (List<Habit>)method.Invoke(
            null,
            [allHabits, userToday, userToday, userToday, 1, new HashSet<Guid>()])!;
    }
}
