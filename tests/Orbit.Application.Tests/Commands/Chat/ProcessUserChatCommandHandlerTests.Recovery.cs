using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private const string RecoveryNotificationId = "986860e5-e299-480c-8b27-d11d12d361e7";

    public static IEnumerable<object[]> RecoveryMultiplexerCases() =>
    [
        [typeof(UpdateNotificationsTool),
            $$"""{"action":"mark_read","notification_id":"{{RecoveryNotificationId}}"}""",
            """{"action":"mark_all_read"}"""],
        [typeof(DeleteNotificationsTool),
            $$"""{"action":"delete_one","notification_id":"{{RecoveryNotificationId}}"}""",
            """{"action":"delete_all"}"""],
        [typeof(ManageApiKeysTool),
            """{"action":"create","name":"Review key","scopes":["read_habits"]}""",
            $$"""{"action":"revoke","key_id":"{{RecoveryNotificationId}}"}"""],
        [typeof(ManageAccountTool), """{"action":"request_deletion"}""", """{"action":"reset_account"}"""],
        [typeof(ManageCalendarSyncTool),
            """{"action":"set_auto_sync","enabled":true}""", """{"action":"dismiss_import"}"""],
        [typeof(UpdateProfilePreferencesTool),
            """{"action":"set_name","name":"Alex"}""", """{"action":"set_language","language":"en"}"""],
        [typeof(ManageSubscriptionTool),
            """{"action":"create_checkout","interval":"monthly"}""", """{"action":"create_portal"}"""]
    ];

    [Theory]
    [MemberData(nameof(RecoveryMultiplexerCases))]
    public async Task RecoveryBoundary_DifferentMultiplexedActionCannotReplaceFailure(
        Type toolType, string original, string replacement)
    {
        var tool = (IAiTool)RuntimeHelpers.GetUninitializedObject(toolType);
        await AssertUnboundRecoveryRejectedAsync(tool, original, replacement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryBoundary_OppositeSetterCannotRunEvenWithModelRetryIdentity(bool includesRetryIdentity)
    {
        var replacement = includesRetryIdentity
            ? """{"retry_of":"rejected_occurrence","action":"set_auto_sync","enabled":false}"""
            : """{"action":"set_auto_sync","enabled":false}""";
        await AssertUnboundRecoveryRejectedAsync(new ManageCalendarSyncTool(_mediator),
            """{"action":"set_auto_sync","enabled":true}""", replacement);
    }

    [Fact]
    public async Task RecoveryBoundary_SubscribeCannotBecomeUnsubscribe()
    {
        await AssertUnboundRecoveryRejectedAsync(new UpdateNotificationsTool(_mediator),
            """{"action":"subscribe_push","endpoint":"https://push.example.test/device","p256dh":"key","auth":"auth"}""",
            """{"action":"unsubscribe_push","endpoint":"https://push.example.test/device"}""");
    }

    [Fact]
    public async Task RecoveryBoundary_ReemittedSuccessfulSiblingPreservesOnlyOriginalSuccess()
    {
        SetupUserAndPayGate();
        var tool = RecoveryCreateHabitTool();
        var handler = CreateHandler(tool);
        var dispatches = new List<AgentExecuteOperationRequest>();
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<AgentExecuteOperationRequest>(0);
                dispatches.Add(request);
                return RecoveryOperationResponse(request, request.Arguments.GetProperty("title").GetString()
                    == ParseArguments(ProductionAdaptationArguments).GetProperty("title").GetString()
                        ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed);
            });
        SetupAiResponse(new AiResponse
        {
            ToolCalls =
            [
                new("create_habit", "successful_occurrence", ParseArguments(ProductionAdaptationArguments)),
                new("create_habit", "rejected_occurrence", ParseArguments(ProductionMaintenanceRejectedArguments))
            ],
            ConversationContext = TestConversationContext
        });
        QueueUnboundRecovery(new("create_habit", "sibling_reemitted", ParseArguments(ProductionAdaptationArguments)));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create both protocol habits."), default);

        result.IsSuccess.Should().BeTrue();
        dispatches.Should().HaveCount(2);
        result.Value.Actions.Should().HaveCount(2);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Success);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        result.Value.Operations.Should().ContainSingle(operation => operation.Status == AgentOperationStatus.Succeeded);
        result.Value.Operations.Should().ContainSingle(operation => operation.Status == AgentOperationStatus.Failed);
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    [Fact]
    public async Task RecoveryBoundary_UnrelatedSameNameCannotConsumeEitherFailure()
    {
        SetupUserAndPayGate();
        var handler = CreateHandler(RecoveryCreateHabitTool());
        var dispatches = new List<AgentExecuteOperationRequest>();
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<AgentExecuteOperationRequest>(0);
                dispatches.Add(request);
                return RecoveryOperationResponse(request,
                    dispatches.Count <= 2 ? AgentOperationStatus.Failed : AgentOperationStatus.Succeeded);
            });
        SetupAiResponse(new AiResponse
        {
            ToolCalls =
            [
                new("create_habit", "rejected_adaptation", ParseArguments(ProductionAdaptationArguments)),
                new("create_habit", "rejected_maintenance", ParseArguments(ProductionMaintenanceRejectedArguments))
            ],
            ConversationContext = TestConversationContext
        });
        QueueUnboundRecovery(new("create_habit", "unrelated_occurrence",
            ParseArguments("""{"title":"Walk","frequency_unit":"Day","frequency_quantity":1}""")));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Create both protocol habits."), default);

        result.IsSuccess.Should().BeTrue();
        dispatches.Should().HaveCount(2);
        result.Value.Actions.Should().HaveCount(2).And.OnlyContain(action => action.Status == ActionStatus.Failed);
        result.Value.Operations.Should().HaveCount(2)
            .And.OnlyContain(operation => operation.Status == AgentOperationStatus.Failed);
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    [Fact]
    public void RecoveryBoundary_FixturesCoverEveryExposedMultiplexerAndKeepMcpOnlyToolsHidden()
    {
        var tools = typeof(IAiTool).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IAiTool).IsAssignableFrom(type))
            .Select(type => (IAiTool)RuntimeHelpers.GetUninitializedObject(type))
            .Where(tool => !tool.IsReadOnly).ToArray();
        var registry = new AiToolRegistry(tools);
        var multiplexers = tools.Where(tool => JsonSerializer.SerializeToElement(tool.GetParameterSchema())
            .GetProperty("properties").TryGetProperty("action", out var action)
                && action.TryGetProperty("enum", out var options) && options.GetArrayLength() > 1).ToArray();
        var coveredNames = RecoveryMultiplexerCases().Select(row =>
            ((IAiTool)RuntimeHelpers.GetUninitializedObject((Type)row[0])).Name).ToArray();

        multiplexers.Where(tool => tool is not IMcpOnlyAiTool).Select(tool => tool.Name)
            .Should().BeEquivalentTo(coveredNames, "each exposed multiplexer needs a source-backed recovery case");
        coveredNames.Should().HaveCount(7).And.OnlyHaveUniqueItems();
        var mcpOnly = multiplexers.Where(tool => tool is IMcpOnlyAiTool).ToArray();
        mcpOnly.Select(tool => tool.Name).Should().Equal("manage_feature_flags");
        foreach (var tool in mcpOnly)
        {
            registry.GetTool(tool.Name).Should().BeSameAs(tool);
            registry.GetAll().Should().NotContain(tool);
        }
    }

    [Fact]
    public async Task RecoveryBoundary_ValidationAcceptsBothOppositeSetterIntentsWithoutWriting()
    {
        using var context = new Orbit.Application.Tests.Chat.HeldWriteTestContext();
        var tool = (IArgumentCheckTool)context.Tool("manage_calendar_sync");

        foreach (var enabled in new[] { true, false })
        {
            var result = await tool.CheckArgumentsAsync(
                JsonSerializer.SerializeToElement(new { action = "set_auto_sync", enabled }), context.UserId, default);
            result.IsSuccess.Should().BeTrue();
        }

        context.Commands.Should().BeEmpty();
        context.Added.Should().BeEmpty();
        await context.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoveryBoundary_FailedBatchCanContainAnAlreadySavedMutation()
    {
        var notification = Notification.Create(UserId, "First", "Body");
        var secondId = Guid.NewGuid();
        var repository = Substitute.For<IGenericRepository<Notification>>();
        repository.FindOneTrackedAsync(Arg.Any<Expression<Func<Notification, bool>>>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Expression<Func<Notification, bool>>>(0).Compile()(notification)
                ? notification : null);
        var commandHandler = new DeleteNotificationCommandHandler(repository, _unitOfWork);
        _mediator.Send(Arg.Any<DeleteNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(async call => call.ArgAt<DeleteNotificationCommand>(0).NotificationId == secondId
                ? Result.Failure("synthetic_second_item_rejection")
                : await commandHandler.Handle(call.ArgAt<DeleteNotificationCommand>(0), call.ArgAt<CancellationToken>(1)));
        var tool = new DeleteNotificationsTool(_mediator);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            action = "delete_selected",
            notification_ids = new[] { notification.Id, secondId }
        }), UserId, default);

        result.Success.Should().BeFalse();
        notification.IsDeleted.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _mediator.Received(2).Send(Arg.Any<DeleteNotificationCommand>(), Arg.Any<CancellationToken>());
        tool.Should().NotBeAssignableTo<ITransactionalAiTool>();
    }

    [Fact]
    public async Task RecoveryBoundary_HistoricalPayloadsAlreadyCreateTwoHabitsWithTheirOriginalSchedules()
    {
        var tool = RecoveryCreateHabitTool();
        var saved = new List<Habit>();
        _habitRepo.AddAsync(Arg.Do<Habit>(saved.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _payGate.CanCreateHabits(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.PassThroughTransactions<ToolResult>();

        foreach (var json in new[] { ProductionAdaptationArguments, ProductionMaintenanceRejectedArguments })
        {
            var arguments = ParseArguments(json);
            (await tool.CheckArgumentsAsync(arguments, UserId, default)).IsSuccess.Should().BeTrue();
            (await tool.ExecuteAsync(arguments, UserId, default)).Success.Should().BeTrue();
        }

        saved.Should().HaveCount(2);
        saved[0].FrequencyUnit.Should().Be(FrequencyUnit.Day);
        saved[0].FrequencyQuantity.Should().Be(1);
        saved[0].NextDueDate.Should().Be(new DateOnly(2026, 9, 8));
        saved[0].EndDate.Should().Be(new DateOnly(2026, 9, 14));
        saved[1].FrequencyUnit.Should().Be(FrequencyUnit.Day);
        saved[1].FrequencyQuantity.Should().Be(1);
        saved[1].Days.Should().BeEquivalentTo([DayOfWeek.Sunday, DayOfWeek.Wednesday]);
        saved[1].NextDueDate.Should().Be(new DateOnly(2026, 9, 16));
    }

    private CreateHabitTool RecoveryCreateHabitTool() =>
        new(_userDateService, Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habitRepo, _tagRepo, _goalRepo, _userDateService, _payGate, _unitOfWork));

    private async Task AssertUnboundRecoveryRejectedAsync(IAiTool tool, string originalJson, string replacementJson)
    {
        SetupUserAndPayGate();
        var original = ParseArguments(originalJson);
        var replacement = ParseArguments(replacementJson);
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        foreach (var field in original.EnumerateObject().Concat(replacement.EnumerateObject()))
        {
            if (field.Name == "retry_of")
                AgentArgumentSchema.Declares(schema, field.Name).Should().BeFalse();
            else
                AgentArgumentSchema.Accepts(schema, field.Name, field.Value).Should().BeTrue($"{tool.Name}/{field.Name}");
        }
        var handler = CreateHandler(tool);
        var dispatches = new List<AgentExecuteOperationRequest>();
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<AgentExecuteOperationRequest>(0);
                dispatches.Add(request);
                return RecoveryOperationResponse(request,
                    dispatches.Count == 1 ? AgentOperationStatus.Failed : AgentOperationStatus.Succeeded);
            });
        SetupAiResponse(ToolResponse(tool.Name, "rejected_occurrence", originalJson));
        QueueUnboundRecovery(new(tool.Name, "unbound_replacement", replacement));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Complete the requested action."), default);

        result.IsSuccess.Should().BeTrue();
        dispatches.Should().ContainSingle();
        JsonElement.DeepEquals(dispatches[0].Arguments, original).Should().BeTrue();
        dispatches[0].Surface.Should().Be(AgentExecutionSurface.Chat);
        dispatches[0].UserId.Should().Be(UserId);
        result.Value.Actions.Should().ContainSingle(action => action.Status == ActionStatus.Failed);
        result.Value.Operations.Should().ContainSingle(operation => operation.Status == AgentOperationStatus.Failed);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.AiMessage.Should().Be(EnglishToolFailureMessage);
    }

    private void QueueUnboundRecovery(AiToolCall replacement) =>
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [replacement],
                ConversationContext = TestConversationContext
            }), Result.Success(new AiResponse { TextMessage = "All requested changes were saved." }));

    private static AgentExecuteOperationResponse RecoveryOperationResponse(
        AgentExecuteOperationRequest request, AgentOperationStatus status) => new(new AgentOperationResult(
            request.OperationId, request.OperationId, AgentRiskClass.Low, AgentConfirmationRequirement.None, status,
            TargetId: status == AgentOperationStatus.Succeeded ? RecoveryNotificationId : null,
            PolicyReason: status == AgentOperationStatus.Failed ? "synthetic_gateway_rejection" : null));
}
