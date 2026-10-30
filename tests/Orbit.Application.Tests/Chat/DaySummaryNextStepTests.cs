using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "parts")]
    [InlineData("pt-BR", "parts")]
    [InlineData("en", "unsupported")]
    [InlineData("pt-BR", "unsupported")]
    [InlineData("en", "disabled")]
    [InlineData("pt-BR", "disabled")]
    [InlineData("en", "legacy")]
    [InlineData("pt-BR", "legacy")]
    [InlineData("en", "legacy-unsupported")]
    [InlineData("pt-BR", "legacy-unsupported")]
    public async Task DaySummary_NextStepPreservesLocalizedProducerSentenceAndOneBlock(string language, string mode)
    {
        var sentence = language == "pt-BR"
            ? "Seu almoço é o próximo passo por ser a primeira refeição pendente."
            : "Your lunch is the next step because it is the earliest pending meal.";
        var user = User.Create("Person", "person@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        if (mode != "disabled") EnableComposition();
        var habits = NextStepHabits();
        var storedTitles = habits.Select(habit => habit.Title).ToArray();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(habits);
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var summaryLogs = Substitute.For<IHabitSummaryLogReader>();
        summaryLogs.ReadAsync(Arg.Any<IReadOnlyCollection<HabitSummaryLogWindow>>(), Arg.Any<CancellationToken>())
            .Returns(habits.SelectMany(habit => habit.Logs)
                .Select(log => new HabitSummaryLogFact(log.HabitId, log.Date, log.Value)).ToList());
        var schedule = DailySummaryScheduleFixture.Project(habits, Today);
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PaginatedResponse<HabitScheduleItem>(
                schedule, 1, 100, schedule.Count, 1)));
        var summaries = Substitute.For<ISummaryService>();
        DailySummaryContext? generation = null;
        summaries.GenerateSummaryAsync(Arg.Any<IEnumerable<Habit>>(), Arg.Any<DailySummaryContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                generation = call.Arg<DailySummaryContext>();
                return Result.Success(new DailySummaryContent(sentence, string.Empty));
            });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var summaryHandler = new GetDailySummaryQueryHandler(_habitRepo, _userRepo,
            Substitute.For<IGenericRepository<HabitLog>>(), summaryLogs, _payGate, summaries, cache, _userDateService, _mediator);
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => summaryHandler.Handle(call.Arg<GetDailySummaryQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetDailySummaryTool(_mediator, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "next", """{"relative_period":"today","generate_narrative":true,"orbit_read_complete":true}"""));
        ContinueWithReadDirective("A later meal would be next.\n[[orbit:read:next]]");
        var supported = mode is "parts" or "disabled" or "legacy";
        var client = new AgentClientContext(SupportsParts: !mode.StartsWith("legacy", StringComparison.Ordinal),
            PartKinds: supported ? ["daySummary"] : [], SupportsDaySummaryCard: supported,
            SupportsHabitListDoneStatus: true);

        var result = await handler.Handle(new(UserId, "What should I do next today?", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Contain(sentence).And.NotContain("A later meal").And.NotContain("Lunch");
        if (client.SupportsParts == true)
        {
            result.Value.Parts![0].Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(sentence);
            result.Value.Parts.OfType<ChatBlockPart>().Should().HaveCount(supported ? 1 : 0);
            if (supported)
                result.Value.Parts.OfType<ChatBlockPart>().Single().Block.Should().BeOfType<DaySummaryCard>()
                    .Which.Rows.Should().Contain(row => row.Title == "Afternoon snack")
                    .And.Contain(row => row.Title == "Lunch");
        }
        generation.Should().NotBeNull();
        generation!.Language.Should().Be(language);
        generation.PendingHabits.Should().HaveCount(11);
        generation.PendingHabits!.First(item => !item.HasPendingChildren).Title.Should().Be("Lunch");
        generation.PendingHabits.Single(item => item.Title == "Meals").Should().BeEquivalentTo(new { CompletedCount = 1, TotalCount = 4 });
        generation.PendingHabits.Single(item => item.Title == "Water").Should().BeEquivalentTo(new { CompletedCount = 0, TotalCount = 6 });
        habits.Select(habit => habit.Title).Should().Equal(storedTitles);
        var stored = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
            ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        var replay = ChatResponseNegotiation.ForClient(stored, new(SupportsParts: false), [], language);
        replay.AiMessage.Should().Contain(sentence).And.NotContain("A later meal");
        replay.AiMessage!.Split(sentence, StringSplitOptions.None).Should().HaveCount(2);
    }

    private static List<Habit> NextStepHabits()
    {
        var habits = new List<Habit>();
        Habit Add(string title, int position, Guid? parent = null, TimeOnly? time = null)
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, title,
                FrequencyUnit.Day, 1, DueDate: Today, ParentHabitId: parent, Position: position, DueTime: time)).Value;
            habits.Add(habit);
            return habit;
        }
        var meals = Add("Meals", 0);
        var water = Add("Water", 1);
        Add("Afternoon snack", 2, meals.Id, new(16, 0));
        Add("Dinner", 3, meals.Id, new(19, 0));
        Add("Lunch", 1, meals.Id, new(12, 0));
        Add("Breakfast", 0, meals.Id, new(8, 0)).Log(Today).IsSuccess.Should().BeTrue();
        for (var index = 5; index >= 0; index--)
            Add("Water - 710ml", index, water.Id, new(14, 0));
        return habits;
    }
}
