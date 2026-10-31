using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private static AiToolCall R9fCall(string capture, string name)
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "ProactiveOffers", "offers-" + capture + ".json")));
        var call = file.RootElement.GetProperty("toolCalls").EnumerateArray().First(item => item.GetProperty("name").GetString() == name);
        return new(name, "captured", call.GetProperty("arguments").Clone());
    }

    private List<AgentExecuteOperationResponse> R9fWriteExecutor(params IAiTool[] tools)
    {
        _unitOfWork.PassThroughTransactions<AgentExecuteOperationResponse>();
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var catalog = new AgentCatalogService(tools);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
        {
            var capability = catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)!;
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.ConfirmationRequired, capability,
                PendingOperation: new(Guid.NewGuid(), capability.Id, "Preview", "Preview", AgentRiskClass.Low,
                    AgentConfirmationRequirement.FreshConfirmation, TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(10)));
        });
        _pendingOperationStore.Revise(UserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var registry = new AiToolRegistry(tools);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), UserId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var real = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, Substitute.For<IAgentStepUpAuthorizationBridge>(), registry,
            new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService, registry, templateRepository: _checklistTemplateRepo),
            _pendingOperationStore, _unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "Ready." }));
        var outcomes = new List<AgentExecuteOperationResponse>();
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var result = await real.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>());
            outcomes.Add(result);
            return result;
        });
        return outcomes;
    }

    [Theory]
    [InlineData("metadata-en-r1", "emoji", "requested")]
    [InlineData("dated-reminder-pt-BR-r2", "emoji", "suggested")]
    [InlineData("repeat-en-r1", "repeat_reminder", "suggested")]
    [InlineData("repeat-pt-BR-r3", "repeat_reminder", "suggested")]
    public async Task R9f_StrictPlaceholdersPreserveTheWriteAndValidSuggestions(string capture, string field, string provenance)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        SetupAiResponse(new AiResponse { ToolCalls = [R9fCall(capture, tool.Name)], ConversationContext = TestConversationContext });
        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient(capture.Contains("pt-BR") ? "pt-BR" : "en")), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.Single().Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items!.Single().Fields.Should().Contain(value => value.Field == field && value.Provenance == provenance);
        if (R9fCall(capture, tool.Name).Args.GetProperty("due_date").ValueKind == JsonValueKind.Null)
            preview.Items!.Single().Fields.Should().NotContain(value => value.Field == "due_date" && value.Provenance == "requested");
        if (provenance == "suggested") preview.Items!.Single().Fields.Single(value => value.Field == field).Removal.Should().NotBeNull();
        preview.NextAction.Should().BeNull();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeEmpty();
    }

    [Fact]
    public async Task R9f_InvalidScheduleIsRepairableInTheSameTurn()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        SetupAiResponse(new AiResponse { ToolCalls = [R9fCall("repeat-en-r3", tool.Name)], ConversationContext = TestConversationContext });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var error = call.Arg<IReadOnlyList<AiToolCallResult>>().Single();
                error.Success.Should().BeFalse();
                error.Error.Should().Contain("GENERAL_HABIT_HAS_FREQUENCY");
                error.Error.Should().NotContain("Policy denied");
                return Result.Success(new AiResponse { ToolCalls = [R9fCall("repeat-en-r1", tool.Name)], ConversationContext = TestConversationContext });
            });
        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient("en")), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.Select(value => value.Operation.Status).Should().Equal(AgentOperationStatus.Failed, AgentOperationStatus.PendingConfirmation);
        result.Value.PendingOperations.Should().ContainSingle();
        result.Value.PolicyDenials.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("title")]
    [InlineData("operation")]
    public async Task R9f_ArgumentRepairCannotSwitchTheOperationOrNamedTarget(string change)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        SetupAiResponse(new AiResponse { ToolCalls = [R9fCall("repeat-en-r3", tool.Name)], ConversationContext = TestConversationContext });
        var repair = R9fCall("repeat-en-r1", tool.Name);
        if (change == "operation") repair = repair with { Name = "delete_habit" };
        else
        {
            var args = JsonNode.Parse(repair.Args.GetRawText())!.AsObject();
            args["title"] = "Different target";
            repair = repair with { Args = JsonSerializer.SerializeToElement(args) };
        }
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse
            { ToolCalls = [repair], ConversationContext = TestConversationContext }));
        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient("en")), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.Should().ContainSingle().Which.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task R9f_ArgumentRepairCannotReplaceABulkTargetFilter()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var water = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Water", FrequencyUnit.Day, 1, Today)).Value;
        var walk = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Walk", FrequencyUnit.Day, 1, Today)).Value;
        SetupOfferHabits(water, walk);
        var tool = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(tool);
        var captured = R9fCall("stored-en-r1", "update_habit").Args;
        var updates = new { title = captured.GetProperty("title").GetString(), is_general = true, frequency_unit = "Day" };
        var original = new AiToolCall(tool.Name, "original", JsonSerializer.SerializeToElement(new
        {
            filter = new { search = "Water" },
            updates,
            orbit_write_complete = true
        }));
        var repair = original with
        {
            Id = "repair",
            Args = JsonSerializer.SerializeToElement(new
            {
                filter = new { search = "Walk" },
                updates,
                orbit_write_complete = true
            })
        };
        SetupAiResponse(new AiResponse { ToolCalls = [original], ConversationContext = TestConversationContext });
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RepairableArgumentResponse(new(new(tool.Name, tool.Name, AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.Failed,
                PolicyReason: "Invalid schedule.", ErrorCode: "invalid_arguments", Access: "write"))));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse
            { ToolCalls = [repair], ConversationContext = TestConversationContext }));
        var result = await handler.Handle(new(UserId, "Change the schedule and rename Water to Morning water.", ClientContext: OfferClient("en")), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
    }

    [Fact]
    public void R9f_ToolGuidancePinsDirectIdsAndDaySummaryChoice()
    {
        new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader).Description.Should()
            .Contain("Use an explicit habit ID supplied by the person directly in the matching write tool; no selection lookup or ask is needed.");
        new UpdateHabitTool(_habitRepo, _userDateService, _mediator).Description.Should()
            .Contain("Use an explicit habit ID supplied by the person directly; no selection lookup or ask is needed.");
        new GetHabitMonthTool(_mediator).Description.Should()
            .Contain("For a current-day summary, use get_daily_summary with relative_period: today. Reserve this tool for the requested habit-calendar window.");
        new GetDailySummaryTool(_mediator, _userDateService).Description.Should()
            .Contain("For a current-day summary, use this tool with relative_period: today.");
    }

    [Fact]
    public async Task R9f_TargetlessAssignTagsCannotProduceAnApprovalPreview()
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new AssignTagsTool(_habitRepo, _tagRepo, _unitOfWork);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        SetupAiResponse(new AiResponse { ToolCalls = [R9fCall("multi-block-pt-BR-r3", tool.Name)], ConversationContext = TestConversationContext });
        var result = await handler.Handle(new(UserId, "Mostre os registros solicitados.", ClientContext: OfferClient("pt-BR")), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.First().Operation.Status.Should().Be(AgentOperationStatus.Failed);
        outcomes.First().Operation.PolicyReason.Should().Contain("habit_id");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
    }
}
