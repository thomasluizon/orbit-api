#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Reflection;
using Orbit.Application.Habits.Services;
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

public partial class AiSummaryServiceTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2025, 4, 1);
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

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
        document.RootElement.GetProperty("max_output_tokens").GetInt32().Should().Be(600);
    }

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
    public void SelectScheduledHabits_DailyGoodHabitLoggedToday_KeptAndLabeledDoneNotPending()
    {
        var habit = CreateHabit("Café da manhã");
        habit.Log(Today);

        var selected = InvokeSelectScheduledHabits([habit], Today);

        selected.Should().ContainSingle();
    }

    [Fact]
    public void SelectScheduledHabits_WeeklyGoodHabitLoggedToday_KeptAndLabeledDone()
    {
        var habit = CreateRecurring("Pesar na balança", FrequencyUnit.Week);
        habit.Log(Today);

        var selected = InvokeSelectScheduledHabits([habit], Today);

        selected.Should().ContainSingle();
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
        selected.Should().ContainSingle(habit => habit.Id == parent.Id);

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
        selected.Should().HaveCount(3);
        using var facts = ReadFacts(AiSummaryService.BuildSummaryPrompt(selected,
            new(Today, Today, Today, "en", null, 0, 0, new Dictionary<Guid, DateOnly>())));
        facts.RootElement.GetProperty("routines")[0].GetProperty("state").GetString().Should().Be("done");

    }

    [Fact]
    public void SelectScheduledHabits_OverdueHabit_StillSelected()
    {
        var overdue = CreateRecurring("Take vitamins", FrequencyUnit.Day, dueDate: Today.AddDays(-2));

        var selected = InvokeSelectScheduledHabits([overdue], Today);

        selected.Should().ContainSingle();
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
    }

    [Fact]
    public void SelectScheduledHabits_OneTimeTaskDueTodayNotDone_SelectedAndPending()
    {
        var task = CreateOneTimeTask("Ligar para o médico", dueDate: Today);

        var selected = InvokeSelectScheduledHabits([task], Today);

        selected.Should().ContainSingle();
    }

    [Fact]
    public void SelectScheduledHabits_OneTimeTaskOverdueNotDone_SelectedAsPending()
    {
        var task = CreateOneTimeTask("Renovar passaporte", dueDate: Today.AddDays(-3));

        var selected = InvokeSelectScheduledHabits([task], Today);

        selected.Should().ContainSingle();
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
    public void SelectScheduledHabits_SkippedToday_VisibleAndNotPending()
    {
        var habit = CreateHabit("Gym");
        habit.LoadScheduleLogsForRead([HabitLog.FromSummaryRead(habit.Id, Today, 0)]);

        var selected = InvokeSelectScheduledHabits([habit], Today);

        selected.Should().ContainSingle();
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

    private static AiSummaryService BuildSummaryService(string modelJsonContent, Action<string>? captureRequest = null)
        => BuildSummaryService(_ => modelJsonContent, captureRequest);

    private static AiSummaryService BuildSummaryService(Func<int, string> modelReply, Action<string>? captureRequest = null)
    {
        var chatClient = new ResponsesClient(
            credential: new ApiKeyCredential("test-key"),
            options: new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(
                    new HttpClient(new JsonResponseHandler(modelReply, captureRequest))),
            });
        var aiClient = new AiCompletionClient(chatClient, NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        return new AiSummaryService(Orbit.Infrastructure.Tests.AiBudgetTestDouble.Wrap(aiClient), NullLogger<AiSummaryService>.Instance);
    }

    private static Task<Result<DailySummaryContent>> InvokeGenerateSummaryAsync(AiSummaryService service) =>
        service.GenerateSummaryAsync(
            new List<Habit>(),
            new DailySummaryContext(Today, Today, Today, "en", null, 0, 0, new Dictionary<Guid, DateOnly>()),
            CancellationToken.None);

    private sealed class JsonResponseHandler(Func<int, string> modelReply, Action<string>? captureRequest) : HttpMessageHandler
    {
        private int calls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captureRequest?.Invoke(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = ResponsesTestData.Text(modelReply(++calls));
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
