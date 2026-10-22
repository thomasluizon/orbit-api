using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task HistoricalQuery_BlockUsesActualFilteredRows()
    {
        SetupUserAndPayGate();
        var past = Today.AddDays(-7);
        var old = Habit.Create(new HabitCreateParams(UserId, "Historical task", null, null, past)).Value;
        var live = CreateHabit("Live task");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { old, live }.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        SetupAiResponse(ToolResponse(tool.Name, "historical_call", "{\"date\":\"" + past.ToString("yyyy-MM-dd") + "\",\"include_overdue\":false}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(new AiResponse { TextMessage = "[[orbit:habits:today]]" }));

        var result = await CreateHandler(tool).Handle(new(UserId, "Show that past day",
            ClientContext: new AgentClientContext(SupportsHabitListCard: true, SupportsHabitListDoneStatus: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.HabitList.Should().NotBeNull();
        result.Value.HabitList!.Items.Select(item => item.Title).Should().Equal(old.Title);
        result.Value.HabitList.Date.Should().Be(past);
        result.Value.HabitList.Scope.Should().Be("date");
    }
    [Fact]
    public async Task MultipleHabitQueries_DoNotBindAnUnqualifiedDirectiveToTheLastRead()
    {
        SetupUserAndPayGate();
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [
                new("query_habits", "first", ParseArguments("{\"date\":\"2026-04-01\"}")),
                new("query_habits", "second", ParseArguments("{\"date\":\"2026-04-02\"}"))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(new AiResponse { TextMessage = "[[orbit:habits:today]]" }));

        var result = await CreateHandler(tool).Handle(new(UserId, "Show two past days",
            ClientContext: new AgentClientContext(SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.HabitList.Should().BeNull();
        result.Value.Operations!.Count(operation => operation.OperationId == "query_habits").Should().Be(2);
    }

    [Fact]
    public async Task MetricsRead_DoesNotCreateRecapOrReferral()
    {
        SetupUserAndPayGate();
        SetupProgress(Metrics(totalScheduled: 1));
        SetupAiResponse(new AiResponse { TextMessage = "[[orbit:metrics]]" });
        await CreateHandler().Handle(new(UserId, "My week",
            ClientContext: new AgentClientContext(SupportsMetricsCard: true)), default);
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.Gamification.Queries.GetRecapQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultipleReadReferencesKeepTheirOwnAbsoluteQuery()
    {
        SetupUserAndPayGate();
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(tool);
        var tokens = new Orbit.Infrastructure.Services.ReadQueryTokenService(Microsoft.Extensions.Options.Options.Create(
            new Orbit.Infrastructure.Configuration.JwtSettings { SecretKey = "test-read-query-signing-key-with-32-bytes", Issuer = "test", Audience = "test" }));
        var blocks = new ReadBlockFactory(_userDateService, tokens, _userRepo);
        var executor = Orbit.Application.Tests.Chat.ReadBlockScopeTests.CreateExecutor(tool, blocks);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => executor.ExecuteAsync(c.Arg<AgentExecuteOperationRequest>(), c.Arg<CancellationToken>()));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new("query_habits", "first", ParseArguments("{\"date\":\"2026-04-01\"}")),
                new("query_habits", "second", ParseArguments("{\"date\":\"2026-04-02\"}"))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success(new AiResponse { TextMessage = "[[orbit:read:first]]\n[[orbit:read:second]]" }));
        var result = await handler.Handle(new(UserId, "Show both days", ClientContext: new AgentClientContext(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);
        var cards = result.Value.Parts!.OfType<ChatBlockPart>().Select(p => (HabitListCard)p.Block).ToList();
        cards.Should().HaveCount(2);
        cards.Select(c => c.Date).Should().Equal(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2));
        cards[0].Read!.QueryId.Should().NotBe(cards[1].Read!.QueryId);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(r => r[0].ReadDirective == "[[orbit:read:first]]" && r[1].ReadDirective == "[[orbit:read:second]]"),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

}
