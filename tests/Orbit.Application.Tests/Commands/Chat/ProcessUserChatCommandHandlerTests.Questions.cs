using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "How are you feeling?", "I'm feeling good", "I want to hurt myself", "Let's plan your next step.")]
    [InlineData("pt-BR", "Como você está se sentindo?", "Estou me sentindo bem", "Quero me machucar", "Vamos planejar seu próximo passo.")]
    public async Task QuestionAnswer_UnselectedCrisisOption_UsesModelAndNormalAllowance(
        string locale, string prompt, string fine, string distress, string reply)
    {
        var user = User.Create("Person", "question-safety@example.com").Value;
        user.SetLanguage(locale);
        SetupUserAndPayGate(user);
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var block = IssuedAstraBlock.Create(UserId, new("question", "", [], [], Questions:
            [new("feeling", "singleChoice", prompt, [new("fine", fine), new("distress", distress)])]));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        SetupAiResponse(new AiResponse { TextMessage = reply });

        var result = await CreateHandler().Handle(new(UserId, "My answer",
            BlockAnswer: new(block.Id, [], Answers: [new("feeling", ["fine"])]),
            ClientContext: new(SupportsMessageAllowance: true, Locale: locale)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(reply)
            .And.NotContain(CrisisSupportGuard.EnglishSupport).And.NotContain(CrisisSupportGuard.PortugueseSupport)
            .And.NotContain("988").And.NotContain("188");
        result.Value.MessageAllowanceConsumed.Should().BeTrue();
        await _payGate.Received(1).TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Is<AiToolRequest>(request =>
                ModelReceivesQuestionAndAnswer(request.UserMessage, prompt, distress)),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _blockStore.Received(1).CompleteAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static bool ModelReceivesQuestionAndAnswer(string message, string prompt, string distress)
    {
        using var data = JsonDocument.Parse(message);
        var root = data.RootElement;
        return root.GetProperty("questions")[0].GetProperty("prompt").GetString() == prompt
            && root.GetProperty("questions")[0].GetProperty("options")[1].GetProperty("label").GetString() == distress
            && root.GetProperty("answers")[0].GetProperty("selectedIds")[0].GetString() == "fine";
    }

    [Theory]
    [InlineData("en", "I want to hurt myself", "988", false)]
    [InlineData("en", "I want to hurt myself", "988", true)]
    [InlineData("pt-BR", "Quero me machucar", "188", false)]
    [InlineData("pt-BR", "Quero me machucar", "188", true)]
    public async Task QuestionAnswer_SelectedOrCustomCrisisDisclosure_ReturnsSupportWithoutAllowance(
        string locale, string distress, string resource, bool custom)
    {
        var user = User.Create("Person", "question-crisis@example.com").Value;
        user.SetLanguage(locale);
        SetupUserAndPayGate(user, payGatePass: false);
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var block = IssuedAstraBlock.Create(UserId, new("question", "", [], [], Questions:
            [new("feeling", "singleChoice", "Feeling?", [new("fine", "Fine"), new("distress", distress)], AllowFreeText: true)]));
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        var answer = custom ? new AstraQuestionAnswer("feeling", FreeText: distress) : new("feeling", ["distress"]);

        var result = await CreateHandler().Handle(new(UserId, "My answer",
            BlockAnswer: new(block.Id, [], Answers: [answer]),
            ClientContext: new(SupportsMessageAllowance: true, Locale: locale)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Contain(resource);
        result.Value.MessageAllowanceConsumed.Should().BeFalse();
        await _moderation.DidNotReceiveWithAnyArgs().CheckTextAsync(default!, default);
        await _payGate.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("choice")]
    [InlineData("pickList")]
    public async Task BlockAnswer_ModerationChecksSelectedInputAndHistory(string form)
    {
        SetupUserAndPayGate();
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        const string selected = "Selected input";
        const string custom = "Custom answer";
        var definition = form switch
        {
            "question" => new AstraBlockDefinition(form, "", [], [], Questions:
                [new("feeling", "multipleChoice", "Feeling?", [new("fine", selected), new("other", "Unselected input")], AllowFreeText: true)]),
            "choice" => new(form, "Feeling?", [new("fine", selected), new("other", "Unselected input")], [], AllowFreeText: true),
            _ => new(form, "Choose", [], [new("fine", selected, ParseArguments("{\"title\":\"Walk\"}"))], true, Action: "create_habits")
        };
        var block = IssuedAstraBlock.Create(UserId, definition);
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        string? checkedInput = null;
        _moderation.CheckTextAsync(Arg.Do<string>(value => checkedInput = value), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(true, false, ["violence"]));
        var answer = form == "question"
            ? new AstraBlockAnswer(block.Id, [], Answers: [new("feeling", ["fine"], FreeText: custom)])
            : new(block.Id, ["fine"], form == "choice" ? custom : null);

        var result = await CreateHandler().Handle(new(UserId, "Request envelope",
            History: [new("assistant", "Earlier assistant content")], BlockAnswer: answer,
            ClientContext: new(SupportsMessageAllowance: true)), default);

        result.IsSuccess.Should().BeTrue();
        checkedInput.Should().Contain(selected).And.NotContain("Unselected input")
            .And.NotContain("Request envelope").And.Contain("Earlier assistant content");
        if (form != "pickList")
            checkedInput.Should().Contain(custom);
        result.Value.MessageAllowanceConsumed.Should().BeFalse();
        await _moderation.Received(1).CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _blockStore.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        await _payGate.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
    }

    [Theory]
    [InlineData("question", "user", "en")]
    [InlineData("question", "assistant", "en")]
    [InlineData("choice", "user", "en")]
    [InlineData("choice", "assistant", "en")]
    [InlineData("question", "user", "pt-BR")]
    [InlineData("question", "assistant", "pt-BR")]
    [InlineData("choice", "user", "pt-BR")]
    [InlineData("choice", "assistant", "pt-BR")]
    public async Task BlockAnswer_RejectedHistory_RefusesBeforeClaimingOrConsumingAllowance(
        string form, string historyRole, string locale)
    {
        SetupUserAndPayGate();
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var definition = form == "question"
            ? new AstraBlockDefinition(form, "", [], [], Questions:
                [new("feeling", "singleChoice", "Feeling?", [new("fine", "Fine"), new("other", "Other")])])
            : new(form, "Feeling?", [new("fine", "Fine"), new("other", "Other")], []);
        var block = IssuedAstraBlock.Create(UserId, definition);
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        SetupAiResponse(new AiResponse { TextMessage = "Model reply" });
        const string historyMarker = "Rejected history marker";
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var flagged = call.Arg<string>().Contains(historyMarker, StringComparison.Ordinal);
            return new ModerationResult(flagged, false, flagged ? ["violence"] : []);
        });
        var answer = form == "question"
            ? new AstraBlockAnswer(block.Id, [], Answers: [new("feeling", ["fine"])])
            : new(block.Id, ["fine"]);

        var result = await CreateHandler().Handle(new(UserId, "My answer",
            History: [new(historyRole, historyMarker)], BlockAnswer: answer,
            ClientContext: new(SupportsMessageAllowance: true, Locale: locale)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(ErrorCopy.Resolve(ErrorCodes.ContentModerationRefused, locale == "pt-BR"));
        result.Value.MessageAllowanceConsumed.Should().BeFalse();
        await _moderation.Received(1).CheckTextAsync(Arg.Is<string>(input => input.Contains(historyMarker)),
            Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceiveWithAnyArgs().SendWithToolsAsync(default!, default, default);
        await _blockStore.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        await _blockStore.DidNotReceiveWithAnyArgs().RecordAnswerAsync(default, default, default, default!, default, default, default);
        await _payGate.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData("en", "choice", true, null)]
    [InlineData("pt-BR", "choice", true, null)]
    [InlineData("en", "Question", true, null)]
    [InlineData("en", "question", false, null)]
    [InlineData("pt-BR", "question", true, FeatureFlagKeys.AstraAskBlocksDisabled)]
    [InlineData("en", "question", true, FeatureFlagKeys.AstraPartsDisabled)]
    public async Task Question_UnsupportedClientsAndFlagsFallBackWithoutIssuing(string locale, string kind, bool parts, string? flag)
    {
        var user = User.Create("Person", "fallback@example.com").Value;
        user.SetLanguage(locale);
        SetupUserAndPayGate(user);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns(flag is null ? [] : [flag]);
        var arguments = JsonSerializer.Serialize(new
        {
            form = "question",
            questions = new[] { new { id = "q", type = "yesNo", prompt = "Ready?" } }
        });
        SetupAiResponse(ToolResponse("ask", "question", arguments));
        var fallback = locale == "pt-BR" ? "Tudo pronto?" : "Ready?";
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = fallback }));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me plan",
            ClientContext: new(SupportsParts: parts, PartKinds: [kind], Locale: locale)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be(fallback);
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Theory]
    [InlineData("en", "Energy?", "Low", "High")]
    [InlineData("pt-BR", "Energia?", "Baixa", "Alta")]
    public async Task Question_IssuesPartAndFeedsStructuredAnswerIntoNextTurn(string locale, string prompt, string low, string high)
    {
        var user = User.Create("Person", "questions@example.com").Value;
        user.SetLanguage(locale);
        SetupUserAndPayGate(user);
        _unitOfWork.PassThroughTransactions<Result<PreparedAstraBlockAnswer>>();
        var arguments = JsonSerializer.Serialize(new
        {
            missing_input = "check_in",
            form = "question",
            questions = new[] { new { id = "energy", type = "scale", prompt, lowLabel = low, highLabel = high } }
        });
        IssuedAstraBlock? issued = null;
        _blockStore.AddAsync(Arg.Do<IssuedAstraBlock>(block => issued = block), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var handler = CreateRecordAskHandler();
        SetupAiResponse(ToolResponse("query_habits", "checkin-read", """{"for_selection":false,"orbit_read_complete":false}"""));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse("ask", "question1", arguments)));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["question"], Locale: locale);

        var response = await handler.Handle(new(UserId, "Help me plan", ClientContext: client), default);

        response.IsSuccess.Should().BeTrue();
        response.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Kind.Should().Be("question");
        issued.Should().NotBeNull();
        var stored = issued!;
        _blockStore.GetAsync(stored.Id, UserId, Arg.Any<CancellationToken>()).Returns(stored);
        _blockStore.MarkAnsweredAsync(stored.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        var answer = JsonSerializer.Deserialize<AstraBlockAnswer>(JsonSerializer.Serialize(new
        {
            blockId = stored.Id,
            selectedIds = Array.Empty<string>(),
            answers = new[] { new { questionId = "energy", number = 3 } }
        }), ChatStreamEvent.SerializerOptions)!;
        AiToolRequest? next = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(value => next = value), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = locale == "pt-BR" ? "Vamos começar." : "Let's start." }));

        var answered = await handler.Handle(new(UserId, "Answer", BlockAnswer: answer, ClientContext: client), default);

        answered.IsSuccess.Should().BeTrue();
        next.Should().NotBeNull();
        var message = next!.UserMessage;
        using var data = JsonDocument.Parse(message);
        data.RootElement.GetProperty("answers")[0].GetProperty("questionId").GetString().Should().Be("energy");
        data.RootElement.GetProperty("answers")[0].GetProperty("number").GetInt32().Should().Be(3);
        data.RootElement.GetProperty("questions")[0].GetProperty("prompt").GetString().Should().Be(prompt);
        await _blockStore.Received(1).RecordAnswerAsync(stored.Id, UserId, Arg.Any<DateTime>(),
            Arg.Is<string>(json => json.Contains("energy")), Arg.Any<DateOnly?>(), Arg.Is<string?>(json => json == null), Arg.Any<CancellationToken>());
    }
}
