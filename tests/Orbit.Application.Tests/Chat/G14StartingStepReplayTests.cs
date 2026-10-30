using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private const string FirstMotionGuidance = "For a smaller refinement, use the typed previousStartingStep.goal and previousStartingStep.step to compare effort before choosing the action. Keep the same activity and return only the first preparatory physical motion, stopping before the previous action is completed. When the previous step is already one short action, reduce it to contact or positioning and stop there. Picking up material can take as much effort as opening it. Tapping material to open it still completes the previous action. For opening study material, use a motion such as resting one fingertip on its closed cover: 'Rest one finger on the closed cover' / 'Encostar um dedo na capa fechada'. Do not append opening, reading or another action to that motion. Apply this comparison in both en and pt-BR.";

    [Theory]
    [InlineData(1, "en")]
    [InlineData(2, "en")]
    [InlineData(3, "en")]
    [InlineData(1, "pt-BR")]
    [InlineData(2, "pt-BR")]
    [InlineData(3, "pt-BR")]
    public async Task G14_CapturedSmallerAttemptsReceiveFirstMotionGuidanceAndTypedBaseline(int repeat, string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        var tool = new SuggestBreakdownTool();
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var captured = CapturedCompositionPlan($"prod-r6-composition-one-small-step-smaller-{language}-r{repeat}");
        var previous = new StartingStep(captured.ToolCalls![0].Args.GetProperty("title").GetString()!,
            language == "en" ? "Open the study material" : "Abrir o material de estudo", language);
        SetupAiResponse(captured);
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["oneSmallStep"], Locale: language,
            SupportsStartingStepContext: true, PreviousStartingStep: previous);

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var request = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
        request.DynamicContext.Should().Contain(JsonSerializer.Serialize(new { previousStartingStep = previous }, ChatStreamEvent.SerializerOptions))
            .And.Contain(FirstMotionGuidance);
        JsonSerializer.SerializeToElement(request.ToolDeclarations.Single()).GetProperty("description").GetString()
            .Should().Contain(FirstMotionGuidance);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be("oneSmallStep");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await AssertNoCompositionContinuation();
        AssertStoredComposition(result.Value, client, language);
    }
}
