using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Tags.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AgentExecutionAndSanitizerTests
{
    [Theory]
    [InlineData(false, "success", AgentOperationStatus.Succeeded)]
    [InlineData(true, "success", AgentOperationStatus.Succeeded)]
    [InlineData(false, "exception", AgentOperationStatus.Failed)]
    [InlineData(true, "exception", AgentOperationStatus.Failed)]
    [InlineData(false, "denied", AgentOperationStatus.Denied)]
    [InlineData(true, "denied", AgentOperationStatus.Denied)]
    [InlineData(false, "confirmation", AgentOperationStatus.PendingConfirmation)]
    [InlineData(true, "confirmation", AgentOperationStatus.PendingConfirmation)]
    [InlineData(false, "missing", AgentOperationStatus.UnsupportedByPolicy)]
    [InlineData(true, "missing", AgentOperationStatus.UnsupportedByPolicy)]
    public async Task AgentOperationExecutor_KnownOutcomes_IncludeAccess(bool isMutation, string outcome, AgentOperationStatus status)
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability("test", "test", AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation);
        var operation = CreateOperation("test", capability.Id, isMutation, true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(new AgentPolicyDecision(
            outcome == "denied" ? AgentPolicyDecisionStatus.Denied
                : outcome == "confirmation" ? AgentPolicyDecisionStatus.ConfirmationRequired : AgentPolicyDecisionStatus.Allowed,
            capability));
        var tool = new StubTool(operation.Id, (_, _, _) => outcome == "exception"
            ? throw new InvalidOperationException("tool_failed") : Task.FromResult(new ToolResult(true)), !isMutation);
        var executor = CreateExecutor(catalog, policyEvaluator: policy,
            toolRegistry: new AiToolRegistry(outcome == "missing" ? [] : [tool]));

        var response = await executor.ExecuteAsync(new(UserId, operation.Id, EmptySchema, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(status);
        response.Operation.Access.Should().Be(isMutation ? "write" : "read");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AgentOperationExecutor_UnknownOperation_UsesKnownMetadataOrOmitsAccess(bool? isMutation, bool hasTool)
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = isMutation is { } mutation
            ? CreateCapability("test", "test", AgentRiskClass.Low, AgentConfirmationRequirement.None, mutation) : null;
        if (!hasTool)
            catalog.GetCapabilityByChatTool("test").Returns(capability);
        var tools = hasTool
            ? new AiToolRegistry([new StubTool("test", (_, _, _) => throw new InvalidOperationException(), isMutation == false)])
            : new AiToolRegistry([]);
        var executor = CreateExecutor(catalog, toolRegistry: tools);

        var response = await executor.ExecuteAsync(new(UserId, "test", EmptySchema, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.UnsupportedByPolicy);
        response.Operation.Access.Should().Be(isMutation is null ? null : isMutation.Value ? "write" : "read");
        var json = JsonSerializer.SerializeToElement(response.Operation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.TryGetProperty("access", out _).Should().Be(isMutation is not null);
    }

    [Theory]
    [InlineData(true, "read")]
    [InlineData(false, "write")]
    public async Task Chat_RealFailedToolAndExecutor_ReturnAccess(bool isReadOnly, string access)
    {
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 10, 24));
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RetrospectiveResponse>("read_failed"));
        mediator.Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("write_failed"));
        IAiTool tool = isReadOnly ? new GetRetrospectiveTool(mediator, dates) : new CreateTagTool(mediator);
        var tools = new AiToolRegistry([tool]);
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(new AgentPolicyDecision(
            AgentPolicyDecisionStatus.Allowed, catalog.GetCapabilityByChatTool(tool.Name)!));
        var executor = CreateExecutor(catalog, policyEvaluator: policy, toolRegistry: tools);
        var intent = Substitute.For<IAiIntentService>();
        intent.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new(tool.Name, "failed_call", Parse("""{"name":"Read","color":"#123456"}"""))],
                ConversationContext = new AiConversationContext()
            }));
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(User.Create("Test", "test@example.com").Value);
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(UserId, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        using var services = new ServiceCollection().AddSingleton<IAgentOperationExecutor>(executor)
            .AddSingleton(Substitute.For<IPendingClarificationStore>()).BuildServiceProvider();
        var handler = new ProcessUserChatCommandHandler(
            new(Substitute.For<IGenericRepository<Habit>>(), Substitute.For<IGenericRepository<Goal>>(), users,
                Substitute.For<IGenericRepository<Tag>>(), Substitute.For<IGenericRepository<ChecklistTemplate>>(), Substitute.For<IFeatureFlagService>()),
            new(intent, tools, Substitute.For<ISystemPromptBuilder>(), catalog),
            new(dates, Substitute.For<IUserStreakService>(), quota, work, services.GetRequiredService<IServiceScopeFactory>(), executor,
                Substitute.For<IPendingClarificationStore>(), Substitute.For<IGoalProgressReadSyncer>(), Substitute.For<IGamificationService>(),
                mediator, Substitute.For<IProductAnalytics>(), Substitute.For<IHabitScheduleLogReader>(),
                Substitute.For<IIssuedAstraBlockStore>(), Substitute.For<IPendingAgentOperationStore>()),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), ModerationTestDouble.Clean());

        var response = await handler.Handle(new(UserId, "Run the requested operation"), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        var operation = response.Value.Operations.Should().ContainSingle().Subject;
        operation.Status.Should().Be(AgentOperationStatus.Failed);
        operation.Access.Should().Be(access);
        if (isReadOnly)
            await mediator.Received(1).Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>());
        else
            await mediator.Received(1).Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>());
        using var json = JsonDocument.Parse(ChatStreamEvent.Final(response.Value).ToJson());
        json.RootElement.GetProperty("response").GetProperty("operations")[0].GetProperty("access").GetString().Should().Be(access);
    }
}
