using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "Create Read a book daily", "Day", 0)]
    [InlineData("pt-BR", "Crie Ler um livro todo dia", "Day", 0)]
    [InlineData("en", "Create Read a book every week", "Week", 0)]
    [InlineData("en", "Add a task to call the bank", "OneTime", 0)]
    [InlineData("pt-BR", "Adicione uma tarefa para ligar para o banco", "OneTime", 0)]
    public async Task CalendarOffer_UndatedOrRecurringDraftGetsNoCalendarAction(string locale, string message, string unit, int dayOffset)
    {
        var preview = await CalendarOfferTurnAsync(locale, message, unit, dayOffset);

        preview.NextAction.Should().BeNull();
    }

    [Theory]
    [InlineData("en", "Dentist appointment tomorrow at 8", 1)]
    [InlineData("pt-BR", "Consulta no dentista amanhã às 8", 1)]
    [InlineData("en", "Dentist appointment today at 8", 0)]
    [InlineData("pt-BR", "Consulta no dentista hoje às 8", 0)]
    [InlineData("en", "Dentist appointment on 2026-04-10 at 8", 7)]
    public async Task CalendarOffer_OneTimeDraftWithRequestedDateKeepsCalendarAction(string locale, string message, int dayOffset)
    {
        var preview = await CalendarOfferTurnAsync(locale, message, "OneTime", dayOffset);

        preview.NextAction!.Surface.Should().Be("calendar-sync");
        preview.NextAction.Label.Should().Be(locale == "en" ? "Connect Google Calendar" : "Conectar Google Agenda");
    }

    /// <summary>
    /// Runs one offer-enabled create turn on the real executor and previewer. The scripted selector
    /// always accepts the calendar candidate, so the result depends only on the server candidate gate.
    /// </summary>
    private async Task<PendingAgentOperation> CalendarOfferTurnAsync(string locale, string message, string unit, int dayOffset)
    {
        var owner = User.Create("Alex", "alex@test.com").Value;
        owner.GrantLifetimePro();
        SetupUserAndPayGate(owner);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(["astra_block_first", "astra_conversation_recording", "calendar_integration"]);
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.PassThroughTransactions<AgentExecuteOperationResponse>();
        var tool = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(tool);
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        var pendingId = Guid.NewGuid();
        policy.EvaluateAsync(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
        {
            var capability = catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)!;
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
        var draft = JsonSerializer.Serialize(new
        {
            title = locale == "en" ? "Appointment" : "Consulta",
            frequency_unit = unit,
            frequency_quantity = unit == "OneTime" ? (int?)null : 1,
            due_date = Today.AddDays(dayOffset).ToString("yyyy-MM-dd"),
            emoji = "📅",
            suggested_fields = new[] { "emoji" },
            orbit_write_complete = true
        });
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(call.Arg<AiToolRequest>().SystemPrompt == ProactiveOfferPlanner.SelectionInstruction
                ? new AiResponse
                {
                    TextMessage = JsonSerializer.Serialize(new
                    {
                        candidateId = "calendar:calendar-sync",
                        interpretation = "dated_event",
                        dismissedOfferKeys = Array.Empty<string>()
                    })
                }
                : ToolResponse(tool.Name, "create", draft)));

        var result = await handler.Handle(new(UserId, message, ClientContext: OfferClient(locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value.PendingOperations.Should().ContainSingle().Which;
    }
}
