using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("ambiguity-en-r1", 0)]
    [InlineData("ambiguity-en-r2", 1)]
    [InlineData("ambiguity-en-r3", 0)]
    [InlineData("ambiguity-en-r3", 1)]
    [InlineData("composition-choice-en-r1", 0)]
    [InlineData("composition-choice-en-r1", 1)]
    [InlineData("composition-choice-en-r2", 1)]
    [InlineData("composition-choice-pt-BR-r1", 0)]
    [InlineData("composition-choice-pt-BR-r1", 1)]
    [InlineData("composition-choice-pt-BR-r2", 0)]
    [InlineData("composition-choice-pt-BR-r3", 0)]
    [InlineData("composition-choice-pt-BR-r3", 1)]
    public async Task GateEightAsk_RenderableCapturedCallIssuesChoice(string capture, int callIndex)
    {
        var pt = capture.Contains("pt-BR", StringComparison.Ordinal);
        var user = User.Create("Person", "ask@example.test").Value;
        user.SetLanguage(pt ? "pt-BR" : "en");
        SetupUserAndPayGate(user);
        EnableComposition();
        var call = AskWithMissingInput(GateEightAskCalls(capture)[callIndex], "record_selection");
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [AskSelectionRead(), call] });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Which habit would you like to change?" }));

        var result = await CreateRecordAskHandler().Handle(new(UserId,
            pt ? "Mude meu hábito, mas ainda não escolhi qual." : "Change my habit.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice", "question"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var choice = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>().Subject;
        var args = call.Args;
        var nested = args.GetProperty("form").GetString() == "question";
        var prompt = args.TryGetProperty("question", out var heading) && heading.ValueKind == JsonValueKind.String
            ? heading.GetString() : args.GetProperty("questions")[0].GetProperty("prompt").GetString();
        choice.Question.Should().Be(prompt);
        choice.AllowFreeText.Should().BeTrue();
        choice.Options.Select(option => option.Label).Should().Equal((nested
            ? args.GetProperty("questions")[0].GetProperty("options") : args.GetProperty("options"))
            .EnumerateArray().Select(option => option.GetProperty("label").GetString()));
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _blockStore.Received(1).AddAsync(Arg.Any<IssuedAstraBlock>(), Arg.Any<CancellationToken>());
        await AssertOnlyAskSelectionRead();
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("ambiguity-en-r2", "questions[0].options", "ambiguity-en-r2", 1, false)]
    [InlineData("ambiguity-en-r2", "questions[0].options", "ambiguity-en-r2", 1, true)]
    [InlineData("composition-choice-en-r2", "questions[0].options", "composition-choice-en-r2", 1, false)]
    [InlineData("composition-choice-en-r2", "questions[0].options", "composition-choice-en-r2", 1, true)]
    [InlineData("composition-compound-missing-kind-en-r3", "questions", "composition-choice-en-r1", 1, false)]
    [InlineData("composition-compound-missing-kind-en-r3", "questions", "composition-choice-en-r1", 1, true)]
    public async Task GateEightAsk_InvalidCapturedCallReturnsPreciseErrorAndAcceptsRetry(
        string capture, string errorPath, string retryCapture, int retryIndex, bool complete)
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = new[] { AskSelectionRead(), GateEightAskCalls(capture)[0] }
                .Select(call => WithTerminalReadCompletion(call, complete)).ToArray()
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls = [WithTerminalReadCompletion(AskWithMissingInput(GateEightAskCalls(retryCapture)[retryIndex], "record_selection"), complete)]
            }));

        var result = await CreateRecordAskHandler().Handle(new(UserId, "Change my habit.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice", "question"])), default);

        result.IsSuccess.Should().BeTrue();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(TestConversationContext,
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 2 && results[0].Success && !results[1].Success
                && results[1].Error != null && results[1].Error!.Contains(errorPath)
                && results[1].Error!.Contains("Retry ask once")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Kind.Should().Be("choice");
        await _blockStore.Received(1).AddAsync(Arg.Any<IssuedAstraBlock>(), Arg.Any<CancellationToken>());
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await AssertOnlyAskSelectionRead();
    }

    private static AiToolCall WithTerminalReadCompletion(AiToolCall call, bool complete)
    {
        var args = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        args[TerminalReadDirective.CompleteArgument] = complete;
        return call with { Args = JsonSerializer.SerializeToElement(args) };
    }

    [Fact]
    public async Task GateEightAsk_MissingCapabilitiesPreservesProseWithoutIssuing()
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = GateEightAskCalls("composition-compound-missing-kind-en-r3")
        });
        const string question = "Which habits and goals would you like to see together?";
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = question }));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Show my habits and goals together.",
            ClientContext: new(SupportsParts: true, PartKinds: [])), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(question);
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    private static AiToolCall[] GateEightAskCalls(string name)
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "GateEightAsk", name + ".json")));
        return capture.RootElement.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
            new AiToolCall(call.GetProperty("name").GetString()!, "gate-eight-" + index,
                call.GetProperty("arguments").Clone())).ToArray();
    }

    [Theory]
    [InlineData("multiple")]
    [InlineData("longLabel")]
    [InlineData("recommended")]
    public async Task GateEightAsk_RetainsQuestionFieldsThatChoiceCannotRender(string field)
    {
        SetupUserAndPayGate();
        var captured = GateEightAskCalls("ambiguity-en-r1")[0];
        var args = JsonNode.Parse(captured.Args.GetRawText())!;
        var questions = args["questions"]!.AsArray();
        switch (field)
        {
            case "multiple":
                var second = questions[0]!.DeepClone();
                second["id"] = "change";
                questions.Add(second);
                break;
            case "longLabel": questions[0]!["options"]![0]!["label"] = new string('a', 21); break;
            case "recommended": questions[0]!["options"]![0]!["recommended"] = true; break;
        }
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [AskSelectionRead(), AskWithMissingInput(captured with { Args = JsonSerializer.SerializeToElement(args) }, "record_selection")]
        });

        var result = await CreateRecordAskHandler().Handle(new(UserId, "Help me plan.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice", "question"])), default);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraQuestionBlock>().Subject;
        block.Questions.Should().HaveCount(field == "multiple" ? 2 : 1);
        if (field == "longLabel") block.Questions[0].Options![0].Label.Should().HaveLength(21);
        if (field == "recommended") block.Questions[0].Options![0].Recommended.Should().BeTrue();
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task GateEightAsk_RepeatedInvalidCallFallsBackAfterOneRetryWithoutWrites()
    {
        SetupUserAndPayGate();
        var call = GateEightAskCalls("ambiguity-en-r2")[0];
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [call] });
        const string question = "Which habit would you like to change, and what would you like to change?";
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [call] }),
                Result.Success(new AiResponse { TextMessage = question }));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Change my habit.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice", "question"])), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(question);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _aiIntentService.Received(2).ContinueWithToolResultsAsync(TestConversationContext,
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                && results[0].Error != null && results[0].Error!.Contains("questions[0].options")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }
}
