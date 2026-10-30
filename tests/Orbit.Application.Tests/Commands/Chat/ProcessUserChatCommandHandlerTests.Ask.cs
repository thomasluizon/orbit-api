using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.Interfaces;
using Orbit.Application.Tests.Common;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private const string ChoiceArguments = """{"form":"choice","question":"When?","options":[{"id":"a","label":"Morning"},{"id":"b","label":"Evening"}]}""";
    private AskTool RealAskTool()
    {
        var planner = new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork);
        return new AskTool(new BulkCreateHabitsTool(_mediator), new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService, planner),
            new BulkSkipHabitsTool(_mediator, _habitRepo, _userDateService, planner), _userDateService);
    }

    [Theory]
    [InlineData(0, "Choose a time.", false)]
    [InlineData(0, "Choose a time.", true)]
    [InlineData(AppConstants.MaxAiRequestTotalTokens / 2 + 1, null, false)]
    [InlineData(AppConstants.MaxAiRequestTotalTokens / 2 + 1, null, true)]
    public async Task Ask_EmitsNegotiatedPartAndIgnoresRepeatedAsk(int tokenCount, string? text, bool nested)
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse
        {
            TextMessage = text,
            ReportedTokenCount = tokenCount,
            ConversationContext = TestConversationContext,
            ToolCalls = [new("ask", "ask1", nested ? JsonSerializer.SerializeToElement(new { input = ParseArguments(ChoiceArguments) }) : ParseArguments(ChoiceArguments)),
                new("ask", "ask2", ParseArguments(ChoiceArguments))]
        });
        var response = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me plan a walk",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);
        response.IsSuccess.Should().BeTrue();
        var block = response.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject;
        block.Kind.Should().Be("choice");
        block.Block.Should().BeOfType<AstraChoiceBlock>().Which.Options.Should().HaveCount(2);
        await _blockStore.Received(1).AddAsync(Arg.Any<IssuedAstraBlock>(), Arg.Any<CancellationToken>());
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "astra_block_issued", Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Theory]
    [InlineData("en", "Which walk?", "Morning", "Evening")]
    [InlineData("pt-BR", "Qual caminhada?", "Manhã", "Noite")]
    public async Task Ask_AmbiguousTarget_SuppressesWritesBeforeAndAfterTheQuestion(
        string language, string question, string first, string second)
    {
        var user = User.Create("Person", "ask@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var write = FakeTool("delete_habit");
        write.IsReadOnly.Returns(false);
        write.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>())
            .Returns(new ToolResult(true));
        var options = JsonSerializer.SerializeToElement(new
        {
            form = "choice",
            question,
            options = new[] { new { id = "morning", label = first }, new { id = "evening", label = second } }
        });
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [new(write.Name, "before", ParseArguments("{}")), new("ask", "question", options),
                new(write.Name, "after", ParseArguments("{}"))]
        });

        var result = await CreateHandler(RealAskTool(), write).Handle(new(UserId,
            language == "pt-BR" ? "Exclua Caminhada" : "Delete Walk",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        var block = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Block
            .Should().BeOfType<AstraChoiceBlock>().Subject;
        block.Question.Should().Be(question);
        block.Options.Select(option => option.Label).Should().Equal(first, second);
        await write.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default, default);
    }

    [Theory]
    [InlineData(false, false, AgentAuthMethod.Jwt)]
    [InlineData(true, true, AgentAuthMethod.Jwt)]
    [InlineData(true, false, AgentAuthMethod.ApiKey)]
    public async Task Ask_UnsupportedClientKillSwitchOrApiKey_OmitsDeclarationAndFallsBackToText(bool supports, bool disabled, AgentAuthMethod auth)
    {
        SetupUserAndPayGate();
        if (disabled)
            _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns(new[] { FeatureFlagKeys.AstraAskBlocksDisabled });
        SetupAiResponse(ToolResponse("ask", "ask1", ChoiceArguments));
        AiToolRequest? initial = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(value => initial = value), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("ask", "ask1", ChoiceArguments)));
        IReadOnlyList<AiToolCallResult>? results = null;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Do<IReadOnlyList<AiToolCallResult>>(value => results = value),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "Which time suits you?" }));
        var response = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me plan a walk", AuthMethod: auth,
            ClientContext: new(SupportsParts: true, PartKinds: supports ? ["choice"] : [])), default);
        response.IsSuccess.Should().BeTrue();
        initial!.ToolDeclarations.Should().NotContain(tool => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString() == "ask");
        initial.DynamicContext.Should().NotContain("do not call ask");
        results.Should().ContainSingle().Which.Error.Should().Contain("plain text");
        response.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task TurnWithoutAnswer_InvalidatesOpenBlocks()
    {
        SetupUserAndPayGate();
        SetupAiResponse(new AiResponse { TextMessage = "Hello." });
        await CreateHandler().Handle(new(UserId, "Hello"), default);
        await _blockStore.Received(1).InvalidateOpenAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChoiceAnswer_ReachesModelWithStoredQuestionAndOneDailyCharge()
    {
        SetupUserAndPayGate();
        EnableRecording();
        _unitOfWork.PassThroughTransactions<Result<Orbit.Application.Chat.PreparedAstraBlockAnswer>>();
        var block = IssuedAstraBlock.Create(UserId, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        SetupAiResponse(new AiResponse { TextMessage = "Evening works." });
        var result = await CreateHandler().Handle(new(UserId, "Evening", BlockAnswer: new(block.Id, ["b"]),
            ClientContext: new(SupportsMessageAllowance: true)), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.MessageAllowanceConsumed.Should().BeTrue();
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn =>
            turn.UserContent == "When?\nEvening" && turn.AssistantContent == result.Value.AiMessage && turn.UsedModel));
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Is<AiToolRequest>(request => request.UserMessage == "When?\nEvening"),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1).TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
        await _blockStore.DidNotReceiveWithAnyArgs().InvalidateOpenAsync(default, default);
    }

    [Theory]
    [InlineData("I want to hurt myself", CrisisSupportGuard.EnglishSupport, CrisisSupportGuard.EnglishResource, false)]
    [InlineData("Quero me machucar", CrisisSupportGuard.PortugueseSupport, CrisisSupportGuard.PortugueseResource, false)]
    [InlineData("I want to hurt myself", CrisisSupportGuard.EnglishSupport, CrisisSupportGuard.EnglishResource, true)]
    [InlineData("Quero me machucar", CrisisSupportGuard.PortugueseSupport, CrisisSupportGuard.PortugueseResource, true)]
    public async Task ChoiceAnswer_CrisisDisclosure_ReturnsStaticResourcesWithoutQuotaOrModel(
        string freeText, string support, string resource, bool allowanceRemaining)
    {
        SetupUserAndPayGate(payGatePass: allowanceRemaining);
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var block = IssuedAstraBlock.Create(UserId, new("choice", "When?",
            [new("a", "Morning"), new("b", "Evening")], [], AllowFreeText: true));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await CreateHandler().Handle(new(UserId, "My answer",
            BlockAnswer: new(block.Id, [], freeText)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(support + "\n\n" + resource);
        await _blockStore.Received(1).MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _payGate.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData("I want to hurt myself", "988", true, false)]
    [InlineData("I want to hurt myself", "988", false, true)]
    [InlineData("Quero me machucar", "188", true, false)]
    [InlineData("Quero me machucar", "188", false, true)]
    public async Task ChoiceAnswer_CrisisDisclosure_BypassesModerationRefusal(string message, string resource, bool flagged, bool unavailable)
    {
        SetupUserAndPayGate();
        EnableRecording();
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var block = IssuedAstraBlock.Create(UserId, new("choice", "When?",
            [new("a", "Morning"), new("b", "Evening")], [], AllowFreeText: true));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(flagged, unavailable, []));

        var result = await CreateHandler().Handle(new(UserId, "My answer", BlockAnswer: new(block.Id, [], message)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Contain(resource);
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn =>
            turn.UserContent == "When?\n" + message && turn.AssistantContent == result.Value.AiMessage
            && !turn.UsedModel && turn.ToolCallsJson == "[]"));
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "astra_message_sent", null,
            Arg.Is<IReadOnlyDictionary<string, object>>(properties => (int)properties["round_count"] == 0
                && (int)properties["tool_call_count"] == 0));
        await _moderation.DidNotReceiveWithAnyArgs().CheckTextAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData("I want to hurt myself", "988", true, false)]
    [InlineData("I want to hurt myself", "988", false, true)]
    [InlineData("Quero me machucar", "188", true, false)]
    [InlineData("Quero me machucar", "188", false, true)]
    [InlineData("I want to hurt myself", "988", false, false)]
    [InlineData("Quero me machucar", "188", false, false)]
    public async Task PickListAnswer_CrisisDisclosure_ReturnsResourcesWithoutModerationModelOrTools(
        string message, string resource, bool flagged, bool unavailable)
    {
        SetupUserAndPayGate(payGatePass: false);
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var item = new AstraBlockItem("a", message, ParseArguments("{\"title\":\"Walk\"}"));
        var block = IssuedAstraBlock.Create(UserId, new("pickList", "Choose", [], [item], true, Action: "create_habits"));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(flagged, unavailable, []));

        var result = await CreateHandler().Handle(new(UserId, "My answer", BlockAnswer: new(block.Id, ["a"])), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Contain(resource);
        await _moderation.DidNotReceiveWithAnyArgs().CheckTextAsync(default!, default);
        await _payGate.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    public async Task ChoiceAnswer_ModerationResult_DoesNotReserveOrDispatch(
        bool flagged, bool unavailable, bool supportsAllowance, bool stream)
    {
        SetupUserAndPayGate();
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var block = IssuedAstraBlock.Create(UserId, new("choice", "When?",
            [new("a", "Morning"), new("b", "Evening")], []));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(flagged, unavailable, []));

        var streamed = new List<ChatStreamEvent>();
        var result = await CreateHandler().Handle(new(UserId, "Morning", BlockAnswer: new(block.Id, ["a"]),
            ClientContext: new(SupportsParts: true, SupportsMessageAllowance: supportsAllowance),
            StreamSink: stream ? item => { streamed.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsSuccess.Should().Be(!unavailable && supportsAllowance);
        if (unavailable)
            result.ErrorCode.Should().Be(ErrorCodes.AiUnavailable);
        else if (!supportsAllowance)
            result.ErrorCode.Should().Be(ErrorCodes.ContentModerationRefused);
        else
        {
            result.Value.AiMessage.Should().NotBeNullOrWhiteSpace();
            result.Value.MessageAllowanceConsumed.Should().BeFalse();
        }
        if (stream && result.IsSuccess)
            streamed.Should().Equal(ChatStreamEvent.Reset(), ChatStreamEvent.Delta(result.Value.AiMessage!));
        else
            streamed.Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        await _payGate.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChoiceAnswer_DisabledCrisisGuard_UsesNormalQuotaAndModelPath(bool allowanceRemaining)
    {
        SetupUserAndPayGate(payGatePass: allowanceRemaining);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraCrisisResourcesDisabled]);
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var block = IssuedAstraBlock.Create(UserId, new("choice", "When?",
            [new("a", "Morning"), new("b", "Evening")], [], AllowFreeText: true));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        SetupAiResponse(new AiResponse { TextMessage = "Model reply" });

        var result = await CreateHandler().Handle(new(UserId, "My answer",
            BlockAnswer: new(block.Id, [], "I want to hurt myself")), default);

        result.IsSuccess.Should().Be(allowanceRemaining);
        await _blockStore.Received(1)
            .MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1)
            .TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
        await _aiIntentService.ReceivedWithAnyArgs(allowanceRemaining ? 1 : 0)
            .SendWithToolsAsync(default!, default, default);
        if (allowanceRemaining)
            result.Value.AiMessage.Should().Be("Model reply");
        else
            result.ErrorCode.Should().Be(Result.PayGateErrorCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Ask_InvalidOptionCount_IssuesNoPart(int count)
    {
        SetupUserAndPayGate();
        var arguments = JsonSerializer.Serialize(new
        {
            form = "choice",
            question = "When?",
            options = Enumerable.Range(0, count).Select(i => new { id = i.ToString(), label = "Option " + i })
        });
        SetupAiResponse(ToolResponse("ask", "ask1", arguments));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "When suits you?" }));
        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me plan a walk",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Theory]
    [InlineData("label", 21)]
    [InlineData("label", 30)]
    [InlineData("description", 25)]
    [InlineData("detail", 25)]
    public async Task Ask_OverlongText_ReturnsValidationFailureToModelWithoutIssuingBlock(string field, int count)
    {
        SetupUserAndPayGate();
        var form = field == "detail" ? "pickList" : "choice";
        var arguments = field == "detail"
            ? JsonSerializer.Serialize(new
            {
                form,
                title = "Choose",
                action = "create_habits",
                items = new[] { new { id = "a", label = "Walk", detail = new string('d', count), arguments = new { title = "Walk" } } }
            })
            : JsonSerializer.Serialize(new
            {
                form,
                question = "When?",
                options = new[]
                {
                    new { id = "a", label = field == "label" ? new string('l', count) : "Morning",
                        description = field == "description" ? new string('d', count) : "Early" },
                    new { id = "b", label = "Evening", description = "Later" }
                }
            });
        SetupAiResponse(ToolResponse("ask", "ask1", arguments));
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        const string rewrittenQuestion = "Would morning or evening suit a walk?";
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = rewrittenQuestion }));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me plan a walk",
            ClientContext: new(SupportsParts: true, PartKinds: [form])), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(rewrittenQuestion);
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(TestConversationContext,
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && results[0].Name == "ask"
                && results[0].Id == "ask1" && !results[0].Success
                && (results[0].Error ?? string.Empty).Contains("question max 120")
                && (results[0].Error ?? string.Empty).Contains("option label max 20")
                && (results[0].Error ?? string.Empty).Contains("option description and item detail max 24")
                && (results[0].Error ?? string.Empty).Contains("Shorten the text and retry.")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PickListAnswer_ReturnsPreviewWithoutModelOrDailyCharge()
    {
        SetupUserAndPayGate(payGatePass: false);
        _unitOfWork.PassThroughTransactions<Result<Orbit.Application.Chat.PreparedAstraBlockAnswer>>();
        var item = new AstraBlockItem("a", "Walk", ParseArguments("{\"title\":\"Walk\"}"));
        var block = IssuedAstraBlock.Create(UserId, new("pickList", "Choose", [], [item], true, Action: "create_habits"));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = CreateHandler();
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits", "Habits", "Preview", AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, block.ExpiresAtUtc, Items: [new("a", null, "Walk", [], "fingerprint")]);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new("bulk_create_habits", "bulk_create_habits", AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), pending));
        var result = await handler.Handle(new(UserId, "Walk", BlockAnswer: new(block.Id, ["a"]),
            ClientContext: new(SupportsMessageAllowance: true)), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.MessageAllowanceConsumed.Should().BeFalse();
        result.Value.PendingOperations.Should().ContainSingle();
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn =>
            !turn.UsedModel && turn.UserContent == "Walk" && turn.ToolCallsJson.Contains("bulk_create_habits")
            && turn.ToolCallsJson.Contains("PendingConfirmation")));
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _payGate.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "astra_block_answered", Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
    }
}
