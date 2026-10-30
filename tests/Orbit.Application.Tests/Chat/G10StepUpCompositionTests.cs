using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task G10_ApiKeyReadRendersEmailVerificationWithoutNarrativeOrExecution(string language, bool parts)
    {
        SetupExistingParents(language);
        EnableComposition();
        var tool = new GetApiKeysTool(_mediator);
        var handler = CreateHandler(tool);
        var gateway = InstallCorrectionGateway(tool);
        var capability = new AgentCatalogService([tool]).GetCapability(AgentCapabilityIds.ApiKeysRead)!
            with
        { ConfirmationRequirement = AgentConfirmationRequirement.StepUp };
        var pending = new PendingAgentOperation(Guid.NewGuid(), capability.Id, capability.DisplayName,
            "Get API keys requested via Chat", capability.RiskClass, capability.ConfirmationRequirement,
            TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(10), ActionKey: PendingOperationActionKeys.GetRequired(tool.Name, "{}"));
        gateway.Bridge.GetRequiredConfirmationAsync(capability.Id, Arg.Any<CancellationToken>()).Returns(capability.ConfirmationRequirement);
        gateway.Policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(new AgentPolicyDecision(
            AgentPolicyDecisionStatus.ConfirmationRequired, capability, "step_up_required", pending));
        SetupAiResponse(CapturedCompositionPlan("g10-composition-api-keys-" + language + "-r1"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<AiResponse>(ErrorMessages.AiEmptyResponse));
        var client = new AgentClientContext(SupportsParts: parts, PartKinds: ["recordList"],
            SupportsRecordListCard: true, SupportsPendingOperationChanges: true, Locale: language);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().ContainSingle().Which.Should().Be(pending);
        result.Value.Actions.Should().BeEmpty();
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        if (parts) result.Value.AiMessage.Should().BeNullOrEmpty();
        else result.Value.AiMessage.Should().Be(ErrorCopy.Resolve(ErrorCodes.ApiKeyCreationChallengeRequired, language == "pt-BR"));
        events.Where(item => item.Type == "delta").Should().OnlyContain(item => item.Text == result.Value.AiMessage);
        gateway.Responses.Should().ContainSingle().Which.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.ApiKeys.Queries.GetApiKeysQuery>(), Arg.Any<CancellationToken>());
        await AssertNoCompositionContinuation();
        AssertStoredComposition(result.Value, client, language);
    }
}
