using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    public static TheoryData<string, string, string> EmptyReadContinuations => new()
    {
        { "en", "parts", "empty" }, { "pt-BR", "parts", "empty" },
        { "en", "legacy", "empty" }, { "pt-BR", "legacy", "empty" },
        { "en", "disabled", "empty" }, { "pt-BR", "disabled", "empty" },
        { "en", "parts", "error" }, { "pt-BR", "parts", "error" },
        { "en", "legacy", "error" }, { "pt-BR", "legacy", "error" },
        { "en", "disabled", "error" }, { "pt-BR", "disabled", "error" },
        { "en", "parts", "ungrounded" }, { "pt-BR", "parts", "ungrounded" },
        { "en", "legacy", "ungrounded" }, { "pt-BR", "legacy", "ungrounded" },
        { "en", "disabled", "ungrounded" }, { "pt-BR", "disabled", "ungrounded" }
    };

    [Theory]
    [MemberData(nameof(EmptyReadContinuations))]
    public async Task EmptyReadContinuation_PreservesSuccessfulReadWithCompositionOff(string language, string mode, string continuation)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        _appConfig.GetAsync(AppConfigKeys.AstraGroundingEnforce, false, Arg.Any<CancellationToken>()).Returns(true);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(mode == "disabled"
                ? [FeatureFlagKeys.AstraConversationRecording, FeatureFlagKeys.AstraPartsDisabled]
                : [FeatureFlagKeys.AstraConversationRecording]);
        var habit = CreateHabit(language == "en" ? "Reading" : "Leitura");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns([habit]);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(query);
        UseProductionReadExecutor(query);
        SetupAiResponse(ToolResponse(query.Name, "today", "{}"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(continuation == "error" ? Result.Failure<AiResponse>(ErrorMessages.AiNoOutput)
                : Result.Success(new AiResponse { TextMessage = continuation == "ungrounded" ? "Your streak is 999999 days." : null }));
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Show the requested data.",
            ClientContext: new(SupportsParts: mode != "legacy", PartKinds: ["habitList"], SupportsHabitListCard: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var read = result.Value.Operations.Should().ContainSingle().Which.ReadBlock;
        read.Should().NotBeNull();
        if (mode == "parts")
        {
            var block = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
            block.Kind.Should().Be("habitList");
            block.Block.Should().BeEquivalentTo(read!.Block);
            result.Value.AiMessage.Should().BeNullOrEmpty();
        }
        else
        {
            result.Value.Parts.Should().BeNull();
            result.Value.AiMessage.Should().Contain(habit.Title);
            events.Last(item => item.Type == "delta").Text.Should().Be(result.Value.AiMessage);
        }
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1).TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
    }
}
