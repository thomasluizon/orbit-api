using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task EmptyRetrospective_ReplaysSuccessfulWeekBlock(string language, bool hasHabits)
    {
        var user = User.Create("Person", "person@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        var from = Today.AddDays(-4);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId,
            "Reading", FrequencyUnit.Day, 1, DueDate: from)).Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(hasHabits ? new List<Habit> { habit }.AsReadOnly() : new List<Habit>().AsReadOnly());
        _payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var narrative = Substitute.For<IRetrospectiveService>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var queryHandler = new GetRetrospectiveQueryHandler(_habitRepo, _payGate, narrative,
            _userStreakService, _userDateService, cache);
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetRetrospectiveQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetRetrospectiveTool(_mediator, _userDateService);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "week", """{"relative_period":"this_week"}"""));
        ContinueWithReadDirective("99 completions.\n[[orbit:read:week]]");
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["periodInsight"],
            SupportsPeriodInsightCard: true);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId,
            language == "en" ? "How did I do this week?" : "Como fui esta semana?",
            ClientContext: client, StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Operations.Should().ContainSingle().Which.Status.Should().Be(AgentOperationStatus.Succeeded);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Kind.Should().Be("periodInsight");
        var card = result.Value.Parts!.OfType<ChatBlockPart>().Single().Block.Should().BeOfType<PeriodInsightCard>().Subject;
        card.DateFrom.Should().Be(from);
        card.DateTo.Should().Be(Today);
        card.TotalCompletions.Should().Be(0);
        card.TotalScheduled.Should().Be(hasHabits ? 5 : 0);
        card.CompletionRate.Should().Be(0);
        card.Series!.Points.Should().HaveCount(5).And.OnlyContain(point => point.Completed == 0);
        if (hasHabits)
            card.NeedsAttention.Should().ContainSingle().Which.HabitId.Should().Be(habit.Id);
        else
        {
            card.TopHabits.Should().BeEmpty();
            card.NeedsAttention.Should().BeEmpty();
        }
        card.Read.Should().NotBeNull();
        card.Read!.Completeness.Should().Be("complete");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        narrative.ReceivedCalls().Should().BeEmpty();
        await _mediator.Received(1).Send(Arg.Is<GetRetrospectiveQuery>(query =>
            query.AllowEmptyPeriod && query.GenerateNarrative && query.Language == language && query.DateFrom == from && query.DateTo == Today),
            Arg.Any<CancellationToken>());

        var stored = JsonSerializer.Deserialize<ChatResponse>(JsonSerializer.Serialize(result.Value,
            ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
        var replay = ChatResponseNegotiation.ForClient(stored, client, [], language, user.TimeZone);
        ChatStreamEvent.Final(replay).ToJson().Should().Be(ChatStreamEvent.Final(result.Value).ToJson());
        foreach (var fallbackClient in new AgentClientContext?[] { new(SupportsParts: true, PartKinds: []), null })
        {
            var fallback = ChatResponseNegotiation.ForClient(stored, fallbackClient, [], language, user.TimeZone);
            fallback.AiMessage.Should().Contain(language == "en" ? "Completed / scheduled" : "Concluídos / agendados")
                .And.Contain(hasHabits ? "0/5" : "0/0").And.NotContain("99")
                .And.NotContain(EnglishToolFailureMessage).And.NotContain(PortugueseToolFailureMessage);
        }
    }
}
