using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AskRerun_SingleQuestionWithHeadingIssuesChoice(bool composition, bool choiceOnly)
    {
        var user = User.Create("Person", "ask@example.test").Value;
        user.SetLanguage("pt-BR");
        SetupUserAndPayGate(user);
        if (composition) EnableComposition();
        var call = AskWithMissingInput(AskRerunCalls("composition-choice-pt-BR-r1")[0], "record_selection");
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [AskSelectionRead(), call] });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Qual hábito você quer mudar?" }));

        var result = await CreateRecordAskHandler().Handle(new(UserId,
            "Mude meu hábito, mas ainda não escolhi qual.",
            ClientContext: new(SupportsParts: true, PartKinds: choiceOnly ? ["choice"] : ["choice", "question"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var choice = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>().Subject;
        var question = call.Args.GetProperty("questions")[0];
        choice.Question.Should().Be(question.GetProperty("prompt").GetString());
        choice.Question.Should().NotBe(call.Args.GetProperty("title").GetString());
        choice.AllowFreeText.Should().BeTrue();
        choice.Options.Select(option => option.Id).Should().Equal(question.GetProperty("options")
            .EnumerateArray().Select(option => option.GetProperty("id").GetString()));
        choice.Options.Select(option => option.Label).Should().Equal(question.GetProperty("options")
            .EnumerateArray().Select(option => option.GetProperty("label").GetString()));
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _blockStore.Received(1).AddAsync(Arg.Is<IssuedAstraBlock>(block => block.ReadDefinition().Form == "choice"),
            Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    private static AiToolCall[] AskRerunCalls(string name)
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "GateEightRerunAsk", name + ".json")));
        return capture.RootElement.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
            new AiToolCall(call.GetProperty("name").GetString()!, "ask-rerun-" + index,
                call.GetProperty("arguments").Clone())).ToArray();
    }
}
