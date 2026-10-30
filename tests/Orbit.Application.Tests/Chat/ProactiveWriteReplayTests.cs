using System.Text.Json;
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProactiveWriteReplay_GuidanceAndSchemasAreNegotiatedForEverySupportedTool(bool supports)
    {
        SetupUserAndPayGate();
        var tools = new IAiTool[]
        {
            new CreateHabitTool(_userDateService, _mediator), new CreateSubHabitTool(_mediator), new BulkCreateHabitsTool(_mediator),
            new UpdateHabitTool(_habitRepo, _userDateService, _mediator), new BulkUpdateHabitsTool(_mediator),
            new CreateGoalTool(_mediator), new UpdateGoalTool(_goalRepo, _mediator),
            new CreateChecklistTemplateTool(_mediator), new UpdateChecklistTemplateTool(_mediator)
        };
        _offers.IssuedAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<AstraOfferFact>());
        SetupAiResponse(new AiResponse { TextMessage = "Ready." });
        var result = await CreateHandler(tools).Handle(new(UserId, "Prepare the requested draft.",
            ClientContext: OfferClient("en") with { SupportsProactiveOffers = supports }), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        var request = (AiToolRequest)_aiIntentService.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "SendWithToolsAsync").GetArguments()[0]!;
        foreach (var declaration in request.ToolDeclarations.Select(value => JsonSerializer.SerializeToElement(value)))
        {
            var name = declaration.GetProperty("name").GetString();
            var schema = declaration.GetProperty("parameters");
            if (name == "bulk_create_habits") schema = schema.GetProperty("properties").GetProperty("habits").GetProperty("items");
            if (name == "bulk_update_habits") schema = schema.GetProperty("properties").GetProperty("updates");
            schema.GetProperty("properties").TryGetProperty("suggested_fields", out _).Should().Be(supports, name);
            declaration.GetProperty("description").GetString()!.Contains(ProactiveProposal.Instruction, StringComparison.Ordinal).Should().Be(supports, name);
        }
    }

    [Theory]
    [InlineData("en", "repeat")]
    [InlineData("pt-BR", "repeat")]
    [InlineData("en", "goal")]
    [InlineData("pt-BR", "goal")]
    [InlineData("en", "calendar")]
    [InlineData("pt-BR", "calendar")]
    public async Task ProactiveWriteReplay_RealExecutorStagesSuggestedFieldsInTheOnlyApprovalPreview(string locale, string mode)
    {
        var owner = User.Create("Alex", "alex@test.com").Value;
        if (mode == "calendar") owner.GrantLifetimePro();
        SetupUserAndPayGate(owner);
        EnableComposition();
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
            if (mode != "calendar")
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
        var draft = mode == "calendar" ? JsonSerializer.Serialize(new
        {
            title = locale == "en" ? "Appointment" : "Consulta",
            frequency_unit = "OneTime",
            due_date = Today.AddDays(1).ToString("yyyy-MM-dd"),
            due_time = "08:00",
            suggested_fields = new { emoji = "📅" },
            orbit_write_complete = true
        }) : JsonSerializer.Serialize(new
        {
            title = locale == "en" ? "Water" : "Água",
            is_general = true,
            suggested_fields = new { repeat_reminder = new { interval_minutes = 120, active_start = "08:00", active_end = "20:00" } },
            orbit_write_complete = true
        });
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(call.Arg<AiToolRequest>().SystemPrompt == ProactiveOfferPlanner.SelectionInstruction
                ? new AiResponse
                {
                    TextMessage = JsonSerializer.Serialize(new
                    {
                        candidateId = mode == "calendar" ? "calendar:calendar-sync" : mode == "goal" ? "aim:account" : null,
                        interpretation = mode == "calendar" ? "dated_event" : mode == "goal" ? "aim" : null,
                        dismissedOfferKeys = Array.Empty<string>()
                    })
                }
                : ToolResponse(tool.Name, "create", draft)));
        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient(locale)), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items!.Single().Fields.Should().Contain(field => field.Field == (mode == "calendar" ? "emoji" : "repeat_reminder")
            && field.Provenance == "suggested" && field.Removal != null && field.IsEditable);
        if (mode == "calendar") preview.NextAction!.Surface.Should().Be("calendar-sync");
        if (mode == "goal") preview.NextAction!.Kind.Should().Be("continueChat");
        if (mode == "repeat") preview.NextAction.Should().BeNull();
        result.Value.Parts.Should().BeEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await toolMediatorWasNotDispatched();

        async Task toolMediatorWasNotDispatched() => await _mediator.DidNotReceive().Send(
            Arg.Any<Orbit.Application.Habits.Commands.CreateHabitCommand>(), Arg.Any<CancellationToken>());
    }
}
