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
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
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
            form = "question",
            questions = new[] { new { id = "energy", type = "scale", prompt, lowLabel = low, highLabel = high } }
        });
        IssuedAstraBlock? issued = null;
        _blockStore.AddAsync(Arg.Do<IssuedAstraBlock>(block => issued = block), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        SetupAiResponse(ToolResponse("ask", "question1", arguments));
        var handler = CreateHandler(RealAskTool());
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
