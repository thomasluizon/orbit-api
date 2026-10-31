using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Orbit.Application.Tests.Common;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false, "null")]
    [InlineData(false, "[]")]
    [InlineData(false, "{}")]
    [InlineData(true, "null")]
    [InlineData(true, "[]")]
    [InlineData(true, "{}")]
    public async Task ProactiveWriteReplay_UnsupportedClientStagesCapturedWritesWithEmptySuggestions(bool bulk, string placeholder)
    {
        SetupUserAndPayGate();
        EnableComposition();
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.PassThroughTransactions<AgentExecuteOperationResponse>();
        _pendingOperationStore.Revise(UserId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        IAiTool tool = bulk ? new BulkCreateHabitsTool(_mediator) : new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(tool);
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call => new AgentPolicyDecision(
            AgentPolicyDecisionStatus.ConfirmationRequired, catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)!,
            PendingOperation: new(Guid.NewGuid(), call.Arg<AgentPolicyEvaluationContext>().CapabilityId, "Create habit", "Preview", AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(10))));
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), UserId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var real = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(), ownership,
            Substitute.For<IAgentStepUpAuthorizationBridge>(), new([tool]),
            new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService, new([tool])),
            _pendingOperationStore, _unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var outcome = await real.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>());
                outcome.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, JsonSerializer.Serialize(outcome));
                return outcome;
            });
        var relative = bulk ? "GateNineMedical/g9i-composition-medical-batch-pt-BR-r3.json" : "GateTenPreviews/single-create-pt-r1.json";
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", relative)))!;
        var arguments = fixture["toolCalls"]!.AsArray().Single(call => call!["name"]!.GetValue<string>() == tool.Name)!["arguments"]!.DeepClone().AsObject();
        arguments["orbit_write_complete"] = true;
        if (bulk)
            foreach (var item in arguments["habits"]!.AsArray()) item!["suggested_fields"] = JsonNode.Parse(placeholder);
        else arguments["suggested_fields"] = JsonNode.Parse(placeholder);
        SetupAiResponse(ToolResponse(tool.Name, "captured", arguments.ToJsonString()));

        var result = await handler.Handle(new(UserId, "Continue.",
            ClientContext: OfferClient("pt-BR") with { SupportsProactiveOffers = false }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().NotBeEmpty();
        await AssertNoCompositionContinuation();
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            !ProactiveProposal.Contains(request.Arguments)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProactiveWriteReplay_DeclaresOnlySupportedOfferFields(bool supports)
    {
        SetupUserAndPayGate();
        var tools = new IAiTool[]
        {
            new CreateHabitTool(_userDateService, _mediator), new CreateSubHabitTool(_mediator), new BulkCreateHabitsTool(_mediator),
            new UpdateHabitTool(_habitRepo, _userDateService, _mediator), new BulkUpdateHabitsTool(_mediator),
            new CreateGoalTool(_mediator), new UpdateGoalTool(_goalRepo, _mediator),
            new CreateChecklistTemplateTool(_mediator), new UpdateChecklistTemplateTool(_mediator),
            new RevisePendingChangesTool(Substitute.For<IServiceProvider>())
        };
        _offers.IssuedAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<AstraOfferFact>());
        SetupAiResponse(new AiResponse { TextMessage = "Ready." });
        var result = await CreateHandler(tools).Handle(new(UserId, "Prepare the requested draft.",
            ClientContext: OfferClient("en") with { SupportsProactiveOffers = supports }), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        var request = (AiToolRequest)_aiIntentService.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "SendWithToolsAsync").GetArguments()[0]!;
        if (supports) request.DynamicContext.Should().Contain(ProactiveProposal.Instruction);
        else request.DynamicContext.Should().NotContain(ProactiveProposal.Instruction);
        foreach (var declaration in request.ToolDeclarations.Select(value => JsonSerializer.SerializeToElement(value)))
        {
            var name = declaration.GetProperty("name").GetString();
            var schema = declaration.GetProperty("parameters");
            schema.GetProperty("properties").TryGetProperty("orbit_dismissed_offer_keys", out _).Should().Be(supports);
            if (name == "revise_pending_changes")
            {
                schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties")
                    .TryGetProperty("remove_suggested_fields", out _).Should().Be(supports);
                continue;
            }
            if (name is "bulk_create_habits" or "bulk_update_habits")
            {
                schema.GetProperty("properties").TryGetProperty("suggested_fields", out _).Should().BeFalse();
                schema = name == "bulk_create_habits" ? schema.GetProperty("properties").GetProperty("habits").GetProperty("items")
                    : schema.GetProperty("properties").GetProperty("updates");
            }
            schema.GetProperty("properties").TryGetProperty("suggested_fields", out _).Should().Be(supports, name);
            if (!supports) continue;
            schema.GetProperty("properties").GetProperty("suggested_fields").GetProperty("type").GetString().Should().Be("array");
            if (schema.GetProperty("properties").TryGetProperty("sub_habits", out var children))
                children.GetProperty("items").GetProperty("properties").TryGetProperty("suggested_fields", out _).Should().BeTrue(name);
            declaration.GetProperty("description").GetString()!.Contains(ProactiveProposal.Instruction, StringComparison.Ordinal).Should().BeTrue(name);
        }
    }

    [Theory]
    [InlineData("en", "repeat")]
    [InlineData("pt-BR", "repeat")]
    [InlineData("en", "goal")]
    [InlineData("pt-BR", "goal")]
    [InlineData("en", "calendar")]
    [InlineData("pt-BR", "calendar")]
    [InlineData("en", "calendar-default")]
    [InlineData("pt-BR", "calendar-default")]
    [InlineData("en", "calendar-disabled")]
    [InlineData("pt-BR", "calendar-disabled")]
    [InlineData("en", "nested")]
    [InlineData("pt-BR", "nested")]
    public async Task ProactiveWriteReplay_RealExecutorStagesSuggestedFieldsInTheOnlyApprovalPreview(string locale, string mode)
    {
        var calendar = mode.StartsWith("calendar", StringComparison.Ordinal);
        var owner = User.Create("Alex", "alex@test.com").Value;
        if (calendar) owner.GrantLifetimePro();
        SetupUserAndPayGate(owner);
        EnableComposition();
        if (mode is "calendar" or "calendar-default") _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(["astra_block_first", "astra_conversation_recording", "calendar_integration"]);
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.PassThroughTransactions<AgentExecuteOperationResponse>();
        var tool = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(tool);
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        var pendingId = Guid.NewGuid();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
        {
            var context = call.Arg<AgentPolicyEvaluationContext>();
            using var args = JsonDocument.Parse(context.OperationArgumentsJson!);
            if (mode == "nested")
                args.RootElement.GetProperty("sub_habits")[0].GetProperty("repeat_reminder").GetProperty("interval_minutes").GetInt32().Should().Be(120);
            else if (!calendar)
                args.RootElement.GetProperty("repeat_reminder").GetProperty("interval_minutes").GetInt32().Should().Be(120);
            var capability = catalog.GetCapability(context.CapabilityId)!;
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.ConfirmationRequired, capability,
                PendingOperation: new(pendingId, capability.Id, "Create habit", "Preview", AgentRiskClass.Low,
                    AgentConfirmationRequirement.FreshConfirmation, TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(10)));
        });
        _pendingOperationStore.Revise(UserId, pendingId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), UserId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var real = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, Substitute.For<IAgentStepUpAuthorizationBridge>(),
            new([tool]), new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService, new([tool])),
            _pendingOperationStore, _unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var outcome = await real.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>());
                outcome.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, JsonSerializer.Serialize(outcome));
                return outcome;
            });
        var draft = ProactiveWriteDraft(locale, mode);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(call.Arg<AiToolRequest>().SystemPrompt == ProactiveOfferPlanner.SelectionInstruction
                ? new AiResponse
                {
                    TextMessage = JsonSerializer.Serialize(new
                    {
                        candidateId = calendar ? "calendar:calendar-sync" : mode == "goal" ? "aim:account" : null,
                        interpretation = calendar ? "dated_event" : mode == "goal" ? "aim" : null,
                        dismissedOfferKeys = Array.Empty<string>()
                    })
                }
                : ToolResponse(tool.Name, "create", draft)));
        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient(locale)), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items!.Single().Fields.Should().Contain(field => field.Field == (mode == "nested" ? "sub_habits/0/repeat_reminder" : calendar ? "emoji" : "repeat_reminder")
            && field.Provenance == "suggested" && field.Removal != null && field.IsEditable);
        if (mode == "nested") preview.Items!.Single().Fields.Should().Contain(field => field.Field == "sub_habits"
            && field.Provenance == "suggested" && field.Removal != null && field.IsEditable);
        if (mode == "calendar")
        {
            preview.NextAction!.Surface.Should().Be("calendar-sync");
            preview.NextAction.Label.Should().Be(locale == "en" ? "Connect Google Calendar" : "Conectar Google Agenda");
        }
        if (mode is "repeat" or "goal" or "calendar-disabled" or "calendar-default" or "nested") preview.NextAction.Should().BeNull();
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await toolMediatorWasNotDispatched();

        async Task toolMediatorWasNotDispatched() => await _mediator.DidNotReceive().Send(
            Arg.Any<Orbit.Application.Habits.Commands.CreateHabitCommand>(), Arg.Any<CancellationToken>());
    }

    private static string ProactiveWriteDraft(string locale, string mode)
    {
        return mode == "nested" ? JsonSerializer.Serialize(new
        {
            title = locale == "en" ? "Routine" : "Rotina",
            frequency_unit = "Day",
            frequency_quantity = 1,
            sub_habits = new[] { new { title = locale == "en" ? "Water" : "Água",
                repeat_reminder = new { interval_minutes = 120 }, reminder_enabled = true,
                suggested_fields = new[] { "repeat_reminder", "reminder_enabled" } } },
            suggested_fields = new[] { "sub_habits" },
            orbit_write_complete = true
        }) : mode.StartsWith("calendar", StringComparison.Ordinal) ? JsonSerializer.Serialize(new
        {
            title = locale == "en" ? "Appointment" : "Consulta",
            frequency_unit = "OneTime",
            due_date = mode == "calendar-default" ? null : Today.AddDays(1).ToString("yyyy-MM-dd"),
            due_time = "08:00",
            emoji = "📅",
            suggested_fields = new[] { "emoji" },
            orbit_write_complete = true
        }) : JsonSerializer.Serialize(new
        {
            title = locale == "en" ? "Water" : "Água",
            is_general = true,
            repeat_reminder = new { interval_minutes = 120, active_start = "08:00", active_end = "20:00" },
            suggested_fields = new[] { "repeat_reminder" },
            orbit_write_complete = true
        });
    }

}
