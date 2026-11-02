using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R9iStartingStepReplay_DeclaresAReadAndKeepsTheRecordedStep(bool legacyBreakdown)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new SuggestBreakdownTool();
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var call = R9iCall("composition-coaching-advice-en", 0);
        if (legacyBreakdown)
        {
            var arguments = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
            arguments["one_small_step"] = false;
            call = call with { Args = JsonSerializer.SerializeToElement(arguments) };
            ContinueWithReadDirective("Try the suggested step.");
        }
        SetupAiResponse(new AiResponse { ToolCalls = [call], ConversationContext = TestConversationContext });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["oneSmallStep"], Locale: "en");

        var result = await handler.Handle(new(UserId, "Help me begin.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var request = (AiToolRequest)_aiIntentService.ReceivedCalls().Single(invocation => invocation.GetMethodInfo().Name == "SendWithToolsAsync").GetArguments()[0]!;
        var declaration = JsonSerializer.SerializeToElement(request.ToolDeclarations.Single());
        var fields = declaration.GetProperty("parameters").GetProperty("properties");
        fields.TryGetProperty("orbit_read_complete", out _).Should().BeTrue();
        fields.TryGetProperty("orbit_write_complete", out _).Should().BeFalse();
        declaration.GetProperty("description").GetString().Should().Contain("Call once per request; never repeat identical arguments.");
        declaration.GetProperty("description").GetString().Should().Contain(
            "Use the offer's or account's concrete activity or habit.");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        if (legacyBreakdown)
            result.Value.Actions.Should().ContainSingle().Which.SuggestedSubHabits.Should().ContainSingle()
                .Which.Title.Should().Be("Put on your walking shoes");
        else
        {
            var card = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Block
                .Should().BeOfType<OneSmallStepCard>().Subject;
            card.Suggestion.Step.Should().Be("Put on your walking shoes");
            result.Value.Actions.Should().BeEmpty();
            await AssertNoCompositionContinuation();
        }
    }
}
