using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
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
    [InlineData(0, "Choose a time.")]
    [InlineData(AppConstants.MaxAiRequestTotalTokens / 2 + 1, null)]
    public async Task Ask_EmitsNegotiatedPartAndStopsBeforeLaterTools(int tokenCount, string? text)
    {
        SetupUserAndPayGate();
        var laterTool = ReadTool("query_habits", new ToolResult(true));
        SetupAiResponse(new AiResponse
        {
            TextMessage = text,
            ReportedTokenCount = tokenCount,
            ConversationContext = TestConversationContext,
            ToolCalls = [new("ask", "ask1", ParseArguments(ChoiceArguments)), new("query_habits", "later", ParseArguments("{}")),
                new("ask", "ask2", ParseArguments(ChoiceArguments))]
        });
        var response = await CreateHandler(RealAskTool(), laterTool).Handle(new(UserId, "Help me plan a walk",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);
        response.IsSuccess.Should().BeTrue();
        var block = response.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject;
        block.Kind.Should().Be("choice");
        block.Block.Should().BeOfType<AstraChoiceBlock>().Which.Options.Should().HaveCount(2);
        await _blockStore.Received(1).AddAsync(Arg.Any<IssuedAstraBlock>(), Arg.Any<CancellationToken>());
        await laterTool.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "astra_block_issued", Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Theory]
    [InlineData(false, false, AgentAuthMethod.Jwt)]
    [InlineData(true, true, AgentAuthMethod.Jwt)]
    [InlineData(true, false, AgentAuthMethod.ApiKey)]
    public async Task Ask_UnsupportedClientKillSwitchOrApiKey_IsNotOfferedAndFallsBackToText(bool supports, bool disabled, AgentAuthMethod auth)
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
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.PassThroughTransactions<Result<Orbit.Application.Chat.PreparedAstraBlockAnswer>>();
        var block = IssuedAstraBlock.Create(UserId, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        SetupAiResponse(new AiResponse { TextMessage = "Evening works." });
        var result = await CreateHandler().Handle(new(UserId, "Evening", BlockAnswer: new(block.Id, ["b"])), default);
        result.IsSuccess.Should().BeTrue();
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
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>())
            .Returns(allowanceRemaining ? Result.Success() : Result.PayGateFailure("AI message limit reached."));
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
        await _payGate.DidNotReceiveWithAnyArgs().CanSendAiMessage(default, default);
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
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>())
            .Returns(allowanceRemaining ? Result.Success() : Result.PayGateFailure("AI message limit reached."));
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
        await _payGate.Received(1).CanSendAiMessage(UserId, Arg.Any<CancellationToken>());
        await _blockStore.Received(allowanceRemaining ? 1 : 0)
            .MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _payGate.Received(allowanceRemaining ? 1 : 0)
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
        var result = await handler.Handle(new(UserId, "Walk", BlockAnswer: new(block.Id, ["a"])), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().ContainSingle();
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _payGate.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        _productAnalytics.Received(1).CaptureUserEvent(UserId, "astra_block_answered", Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
    }
}
