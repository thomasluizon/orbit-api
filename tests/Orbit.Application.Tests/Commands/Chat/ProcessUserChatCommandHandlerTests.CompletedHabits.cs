using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("How many habits did I complete this week?", "en", "completedHabits", 3, false, false)]
    [InlineData("Quantos hábitos concluí esta semana?", "pt-BR", "completedHabits", 3, false, false)]
    [InlineData("How many habits did I complete this week?", "en", "completedHabits", 3, true, false)]
    [InlineData("Quantos hábitos concluí esta semana?", "pt-BR", "completedHabits", 3, true, false)]
    [InlineData("How many times did I complete habits this week?", "en", "positiveLogRecords", 5, false, false)]
    [InlineData("Quantas vezes concluí hábitos esta semana?", "pt-BR", "positiveLogRecords", 5, true, false)]
    [InlineData("Compare how many habits I completed each week", "en", "completedHabits", 3, false, true)]
    [InlineData("Compare quantos hábitos concluí em cada semana", "pt-BR", "completedHabits", 3, true, true)]
    [InlineData("How many habits did I complete this week?", "en", "completedHabits", 3, false, false, false)]
    [InlineData("Quantos hábitos concluí esta semana?", "pt-BR", "completedHabits", 3, true, false, false)]
    [InlineData("Compare how many habits I completed each week", "en", "completedHabits", 3, false, true, false)]
    [InlineData("Compare quantos hábitos concluí em cada semana", "pt-BR", "completedHabits", 3, true, true, false)]
    [InlineData("How many habits did I complete this week?", "en", "completedHabits", 3, false, false, false, false)]
    [InlineData("Quantos hábitos concluí esta semana?", "pt-BR", "completedHabits", 3, false, false, false, false)]
    [InlineData("Compare habits", "en", "completedHabits", 3, false, true, false, false)]
    [InlineData("Compare hábitos", "pt-BR", "completedHabits", 3, false, true, false, false)]
    [InlineData("How many habits did I complete this week?", "en", "completedHabits", 3, false, false, false, true, true)]
    [InlineData("Quantos hábitos concluí esta semana?", "pt-BR", "completedHabits", 3, true, false, false, true, true)]
    [InlineData("How many habits did I complete this week?", "en", "completedHabits", 3, false, false, true, true, true)]
    [InlineData("Quantos hábitos concluí esta semana?", "pt-BR", "completedHabits", 3, true, false, true, true, true)]
    [InlineData("How many habits did I complete this week?", "en", "completedHabits", 3, false, false, true, false)]
    [InlineData("Quantos hábitos concluí esta semana?", "pt-BR", "completedHabits", 3, false, false, true, false)]
    public async Task CompletedHabits_ProductionQueryKeepsCountsAndFiguresInBlock(string message, string language,
        string metric, int expected, bool composition, bool compare, bool supportsCompletedHabits = true, bool supportsParts = true, bool missingKind = false)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var user = await _userRepo.GetByIdAsync(UserId, default);
        user!.SetLanguage(language);
        var from = Today.AddDays(-6);
        var habits = Enumerable.Range(0, 3).Select(index => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, UserId, $"Habit {index}", FrequencyUnit.Day, 1, DueDate: from)).Value).ToList();
        foreach (var habit in habits)
            habit.Log(from, advanceDueDate: false);
        habits[0].Log(from.AddDays(1), advanceDueDate: false);
        habits[1].Log(from.AddDays(2), advanceDueDate: false);
        habits[2].Log(from.AddDays(-1), advanceDueDate: false);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        var queryHandler = new GetPeriodAnalyticsQueryHandler(_habitRepo, _userRepo);
        _mediator.Send(Arg.Any<GetPeriodAnalyticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetPeriodAnalyticsQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetPeriodAnalyticsTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "total", JsonSerializer.Serialize(new
        {
            date_from = from.ToString("yyyy-MM-dd"),
            date_to = Today.ToString("yyyy-MM-dd"),
            metric,
            baseline_from = compare ? from.AddDays(-7).ToString("yyyy-MM-dd") : null,
            baseline_to = compare ? from.AddDays(-1).ToString("yyyy-MM-dd") : null
        })));
        const string narrative = "You completed 99 habits.\n[[orbit:read:total]]\nThere were 99 records.";
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (call.ArgAt<Func<AiStreamEvent, Task>?>(2) is { } sink)
                    await sink(AiStreamEvent.Delta(narrative));
                return Result.Success(new AiResponse { TextMessage = narrative });
            });
        var events = new List<ChatStreamEvent>();
        var kind = compare ? "periodComparison" : "periodTotal";
        var result = await handler.Handle(new(UserId, message,
            ClientContext: new(SupportsParts: supportsParts, PartKinds: missingKind ? [] : [kind],
                SupportsCompletedHabitsMetric: supportsCompletedHabits ? true : null),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue();
        if (!supportsCompletedHabits || !supportsParts || missingKind)
        {
            var label = language == "pt-BR" ? "Hábitos concluídos" : "Habits completed";
            result.Value.AiMessage.Should().NotBeNullOrWhiteSpace().And.Contain($"{label}: 3")
                .And.NotContain("completedHabits").And.NotContain("99")
                .And.NotMatchRegex(@"\d{4}-\d{2}-\d{2}");
            if (compare) result.Value.AiMessage.Should().Contain("1");
            if (supportsParts)
            {
                events.Should().NotContain(item => item.Type == "delta", "fallback figures replace the model narrative");
                result.Value.Parts.Should().NotBeNull();
                result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
                result.Value.Parts.OfType<ChatTextPart>().Should().Contain(part => part.Text.Contains($"{label}: 3"));
            }
            else
            {
                result.Value.Parts.Should().BeNull();
                events.Where(item => item.Type == "delta").Should().ContainSingle().Which.Text.Should().Be(result.Value.AiMessage);
            }
        }
        else
        {
            var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
            block.Kind.Should().Be(kind);
            if (compare)
            {
                var card = block.Block.Should().BeOfType<PeriodComparisonCard>().Subject;
                card.CurrentValue.Should().Be(expected);
                card.Current.CompletedHabits.Should().Be(3);
                card.Baseline.CompletedHabits.Should().Be(1);
                card.AbsoluteDelta.Should().Be(2);
            }
            else
            {
                var card = block.Block.Should().BeOfType<PeriodTotalCard>().Subject;
                card.Value.Should().Be(expected);
                card.Metric.Should().Be(metric);
                card.Unit.Should().Be(metric == "completedHabits" ? "habits" : "records");
                card.Metrics.CompletedHabits.Should().Be(3);
                card.Metrics.PositiveLogRecords.Should().Be(5);
            }
            result.Value.AiMessage.Should().BeNullOrEmpty();
            events.Should().NotContain(item => item.Type == "delta", "period figures must appear only in their block");
        }
        var request = _aiIntentService.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "SendWithToolsAsync")
            .GetArguments().OfType<AiToolRequest>().Single();
        request.DynamicContext.Should().Contain("how many habits did I complete this week")
            .And.Contain("quantos hábitos concluí esta semana").And.Contain("completedHabits")
            .And.Contain("quantas vezes").And.Contain("positiveLogRecords")
            .And.Contain($"Completed habits metric supported: {supportsCompletedHabits}.");
        var replay = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
            ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(result.Value).ToJson());
    }
}
