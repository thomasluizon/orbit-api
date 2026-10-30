using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Tests.Chat;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "drop")]
    [InlineData("pt-BR", "drop")]
    [InlineData("en", "owned")]
    [InlineData("pt-BR", "owned")]
    [InlineData("en", "denied")]
    [InlineData("pt-BR", "denied")]
    [InlineData("en", "write")]
    [InlineData("pt-BR", "write")]
    public async Task G12_GoalReadOwnershipDenialOffersOneReadOnlyCorrection(string language, string recovery)
    {
        SetupExistingParents(language);
        EnableComposition();
        var goal = Goal.Create(UserId, language == "en" ? "Consistent hydration" : "Hidratação constante", 10, "sessions").Value;
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]);
        var goals = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var metrics = new GetHabitMetricsTool(_mediator);
        var habit = CreateHabit("Water");
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(
            new HabitMetrics(1, 1, 1, 1, 1, Today, habit.Id, habit.Title)));
        var write = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(goals, metrics, write);
        var blocks = UseProductionReadExecutor(goals);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(metrics.Name, UserId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<JsonElement>().GetProperty("habit_id").GetString() == habit.Id.ToString()
                ? null : "target_not_owned:get_habit_metrics:habit");
        var executor = ReadBlockScopeTests.CreateExecutor(metrics, blocks, ownership);
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == metrics.Name),
            Arg.Any<CancellationToken>()).Returns(call => executor.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>()));
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == write.Name),
            Arg.Any<CancellationToken>()).Returns(new AgentExecuteOperationResponse(new AgentOperationResult(
                write.Name, write.Name, AgentRiskClass.Low, AgentConfirmationRequirement.None, AgentOperationStatus.Failed, Access: "write")));
        var captured = CapturedCompositionPlan("prod-r2-composition-goals-pt-BR-r2");
        SetupAiResponse(captured with
        {
            ToolCalls = captured.ToolCalls!.Select(call => call.Name == metrics.Name
                ? call with { Args = JsonSerializer.SerializeToElement(new { habit_id = goal.Id, orbit_read_complete = true }) }
                : call).ToArray()
        });
        var correction = recovery switch
        {
            "drop" => new AiResponse(),
            "owned" => ToolResponse(metrics.Name, "correction", JsonSerializer.Serialize(new { habit_id = habit.Id })),
            "denied" => ToolResponse(metrics.Name, "correction", JsonSerializer.Serialize(new { habit_id = goal.Id })),
            _ => ToolResponse(write.Name, "correction", """{"title":"Unrequested","frequency_unit":"Day"}""")
        };
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(correction));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["goalList", "metrics"],
            SupportsGoalListCard: true, SupportsMetricsCard: true, Locale: language);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show the requested progress.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Any(item => item.Name == metrics.Name && item.Payload != null
                && JsonSerializer.SerializeToElement(item.Payload, (JsonSerializerOptions?)null).GetProperty("correction_allowed").GetBoolean())),
            Arg.Is<Func<AiStreamEvent, Task>?>(sink => sink == null), Arg.Any<CancellationToken>());
        result.Value.Parts![0].Should().BeOfType<ChatBlockPart>(JsonSerializer.Serialize(result.Value)).Which.Block.Should().BeOfType<GoalListCard>()
            .Which.Items.Should().ContainSingle().Which.Title.Should().Be(goal.Title);
        if (recovery is "drop" or "owned")
        {
            result.Value.Parts.Should().HaveCount(recovery == "drop" ? 1 : 2);
            result.Value.AiMessage.Should().BeNullOrEmpty();
            result.Value.PolicyDenials.Should().BeNullOrEmpty();
            result.Value.Operations.Should().OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded);
            if (recovery == "owned")
                result.Value.Operations!.Single(operation => operation.OperationId == metrics.Name).Payload
                    .Should().BeOfType<HabitMetrics>().Which.HabitId.Should().Be(habit.Id);
        }
        else
            result.Value.AiMessage.Should().Be(language == "en" ? "I couldn't complete that. Please try again." : "Não consegui concluir isso. Tente novamente.");
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == write.Name), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        AssertStoredComposition(result.Value, client, language);
    }
}
