using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task R9b_AskCompositionAcceptsACompleteTypedTextAnswerWithoutAnotherCall(string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        var answer = language == "en" ? "Start with an easy action." : "Comece com uma ação fácil.";
        SetupAiResponse(new() { TextMessage = JsonSerializer.Serialize(new { aiMessage = answer, kind = "answer" }), ConversationContext = new() });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["choice"], Locale: language);

        var response = await CreateHandler(RealAskTool()).Handle(new(UserId, "Continue.", ClientContext: client), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        response.Value.AiMessage.Should().Be(answer);
        response.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(answer);
        var sent = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
        sent.DynamicContext.Should().Contain(TerminalAnswerDirective.Instruction);
        sent.DynamicContext.Should().Contain("A clarification must never finish as free-text prose or kind=answer.");
        await AssertNoCompositionContinuation();
        AssertStoredComposition(response.Value, client, language);
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task R9b_AskCompositionNeverPublishesAnInvalidOrTruncatedTerminalEnvelope(string language, bool truncated)
    {
        SetupExistingParents(language);
        EnableComposition();
        var incomplete = new AiResponse { TextMessage = "{\"kind\":\"needs_input\",\"aiMessage\":\"Continue?\"}", ConversationContext = new(), IsTruncated = truncated };
        SetupAiResponse(incomplete);
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(incomplete));
        var events = new List<ChatStreamEvent>();

        var response = await CreateHandler(RealAskTool()).Handle(new(UserId, "Continue.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"], Locale: language),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        if (truncated)
        {
            response.IsSuccess.Should().BeTrue(response.Error);
            response.Value.AiMessage.Should().NotContain("needs_input").And.NotContain("Continue?");
            await AssertNoCompositionContinuation();
        }
        else
        {
            response.IsFailure.Should().BeTrue();
            await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 0), null, Arg.Any<CancellationToken>());
        }
        events.Where(item => item.Type == "delta").Should().NotContain(item => item.Text != null && item.Text.Contains("Continue?"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task R9b_AskCompositionRepairsCapturedTextQuestionThroughReadAndChoice(string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        var first = CreateHabit("Beber água");
        var second = CreateHabit("Caminhada");
        SetupSelectionHabits(first, second);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query, RealAskTool());
        UseProductionReadExecutor(query);
        using var failed = R9bCapture("composition-choice-pt-BR-r1");
        using var passed = R9bCapture("composition-choice-pt-BR-r2");
        var question = failed.RootElement.GetProperty("answer").GetString();
        SetupAiResponse(new() { TextMessage = question, ConversationContext = new() });
        var calls = BrainDumpCalls(passed);
        var args = JsonNode.Parse(calls[1].Args.GetRawText())!.AsObject();
        args["input"]!["options"]![0]!["id"] = first.Id.ToString();
        args["input"]!["options"]![1]!["id"] = second.Id.ToString();
        args["input"]!["question"] = language == "en" ? "Which habit and what change?" : "Qual hábito você quer mudar e o que quer mudar?";
        args["input"]!["allowFreeText"] = true;
        var rounds = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result.Success(rounds++ == 0
                ? new AiResponse { ToolCalls = [calls[0]], ConversationContext = new() }
                : new AiResponse { ToolCalls = [calls[1] with { Args = JsonSerializer.SerializeToElement(args) }], ConversationContext = new() }));
        var events = new List<ChatStreamEvent>();
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["choice"], Locale: language);

        var response = await handler.Handle(new(UserId, "Continue.", ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        response.IsSuccess.Should().BeTrue(response.Error);
        response.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>();
        response.Value.AiMessage.Should().BeNullOrEmpty();
        response.Value.Actions.Should().BeEmpty();
        response.Value.PendingOperations.Should().BeNullOrEmpty();
        rounds.Should().Be(2);
        events.Where(item => item.Type == "delta").Should().BeEmpty();
        AssertStoredComposition(response.Value, client, language);
    }
}
