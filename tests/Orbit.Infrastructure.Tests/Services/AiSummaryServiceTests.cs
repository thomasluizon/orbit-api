using System.Reflection;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure logic in AiSummaryService: StripMarkdownFences, BuildSummaryPrompt,
/// and BuildHabitSection. The AI client call itself is an integration concern tested elsewhere.
/// </summary>
public class AiSummaryServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2025, 4, 1);

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
    public void BuildSummaryPrompt_EstablishesCloseFriendPersonaAndLeadWithDone()
    {
        var habits = new List<Habit> { CreateHabit("Walk") };
        var result = InvokeBuildSummaryPrompt(habits, Today, "en");

        result.Should().Contain("Rules:");
        result.Should().Contain("LEAD with a specific");
        result.Should().Contain("ALREADY completed today");
        result.Should().Contain("friend who actually knows you");
        result.Should().Contain("Do NOT use markdown");
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

        section.Should().Contain("Skip Gym (bad habit -- slipped)");
        section.Should().NotContain("(done)");
    }

    [Fact]
    public void BuildHabitSection_BadHabitCleanInRange_FramedAsCleanWinWithDaysSinceSlip()
    {
        var badHabit = CreateBadHabit("Cheat diet");
        badHabit.Log(Today.AddDays(-3), advanceDueDate: false);

        var section = InvokeBuildHabitSection([badHabit], Today);

        section.Should().Contain("Cheat diet (bad habit -- clean, 3 days since last slip)");
        section.Should().NotContain("(done)");
        section.Should().NotContain("slipped");
    }

    [Fact]
    public void BuildHabitSection_BadHabitNeverLogged_FramedAsCleanNoSlipsOnRecord()
    {
        var badHabit = CreateBadHabit("Smoke");

        var section = InvokeBuildHabitSection([badHabit], Today);

        section.Should().Contain("Smoke (bad habit -- clean, no slips on record)");
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

        result.Should().Contain("bad habit -- slipped");
        result.Should().Contain("never congratulate it");
        result.Should().Contain("bad habit -- clean");
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

        result.Should().Contain("Current streak: 12 days");
        result.Should().Contain("Streak freezes banked: 2");
    }

    [Fact]
    public void BuildSummaryPrompt_NoStreak_OmitsStreakLines()
    {
        var habit = CreateHabit("Walk");

        var result = InvokeBuildSummaryPrompt([habit], Today, "en");

        result.Should().NotContain("Current streak:");
        result.Should().NotContain("Streak freezes banked:");
    }

    private static Habit CreateHabit(
        string title,
        Guid? parentId = null,
        DateOnly? dueDate = null,
        TimeOnly? dueTime = null)
    {
        return Habit.Create(new HabitCreateParams(
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
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            title,
            unit,
            quantity,
            DueDate: dueDate ?? Today,
            ParentHabitId: parentId)).Value;
    }

    private static Habit CreateBadHabit(string title, DateOnly? dueDate = null)
    {
        return Habit.Create(new HabitCreateParams(
            ValidUserId,
            title,
            FrequencyUnit.Day,
            1,
            DueDate: dueDate ?? Today,
            IsBadHabit: true)).Value;
    }

    private static Habit CreateOneTimeTask(string title, DateOnly dueDate, Guid? parentId = null)
    {
        return Habit.Create(new HabitCreateParams(
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
        int streakFreezesAccumulated = 0)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("BuildSummaryPrompt", PrivateStatic)!;
        return (string)method.Invoke(null,
            [scheduledHabits, date, date, date, language, currentLocalTime, currentStreak, streakFreezesAccumulated])!;
    }

    private static string InvokeBuildHabitSection(List<Habit> scheduledHabits, DateOnly? userToday = null)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("BuildHabitSection", PrivateStatic)!;
        return (string)method.Invoke(null, [scheduledHabits, Today, Today, userToday ?? Today])!;
    }

    private static List<Habit> InvokeSelectScheduledHabits(
        List<Habit> allHabits, DateOnly userToday)
    {
        var method = typeof(AiSummaryService)
            .GetMethod("SelectScheduledHabits", PrivateStatic)!;
        return (List<Habit>)method.Invoke(null, [allHabits, userToday, userToday, userToday])!;
    }
}
