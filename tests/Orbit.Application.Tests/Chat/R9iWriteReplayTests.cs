using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private static AiToolCall R9iCall(string capture, int index)
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "ProactiveOffers", "R9i", capture + "-r1.json")));
        var call = file.RootElement.GetProperty("toolCalls")[index];
        return new(call.GetProperty("name").GetString()!, "captured", call.GetProperty("arguments").Clone());
    }

    [Theory]
    [InlineData("offers-preview-rejection-en", "en", 0)]
    [InlineData("offers-preview-rejection-en", "en", 1)]
    [InlineData("offers-preview-rejection-pt-BR", "pt-BR", 0)]
    [InlineData("offers-preview-rejection-pt-BR", "pt-BR", 1)]
    [InlineData("offers-parent-en", "en", 0)]
    [InlineData("offers-parent-en", "en", 1)]
    public async Task R9iWriteReplay_UnusableSuggestionNamesDoNotLoseTheRequestedPreview(string capture, string locale, int index)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var tool = new CreateHabitTool(_userDateService, _mediator);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        var call = R9iCall(capture, index);
        SetupAiResponse(new AiResponse { ToolCalls = [call], ConversationContext = TestConversationContext });

        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient(locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.Should().ContainSingle().Which.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var fields = result.Value.PendingOperations.Should().ContainSingle().Which.Items!.Single().Fields;
        fields.Should().Contain(field => field.Field == "title" && field.Provenance == "requested"
            && field.NewValue == call.Args.GetProperty("title").GetString());
        fields.Should().Contain(field => field.Field == "emoji" && field.Provenance == "suggested"
            && field.IsEditable && field.Removal != null);
        fields.Should().NotContain(field => field.Field == "reminder_times" && field.Provenance == "suggested");
        if (call.Args.GetProperty("is_bad_habit").ValueKind is JsonValueKind.True or JsonValueKind.False)
            fields.Should().Contain(field => field.Field == "is_bad_habit" && field.Provenance == "requested"
                && field.ProposedValue!.Value.GetBoolean() == call.Args.GetProperty("is_bad_habit").GetBoolean());
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeEmpty();
        await AssertNoCompositionContinuation();
        await _mediator.DidNotReceive().Send(Arg.Any<Orbit.Application.Habits.Commands.CreateHabitCommand>(), Arg.Any<CancellationToken>());
    }
}
