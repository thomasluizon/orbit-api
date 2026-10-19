using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(185)]
    public async Task Handle_EmojiToolCard_EmitsTranslationKeyAndNumericCounts(int count)
    {
        SetupUserAndPayGate();
        var habits = Enumerable.Range(0, count)
            .Select(index => Habit.Create(new HabitCreateParams(UserId, $"Habit {index}", FrequencyUnit.Day, 1, DueDate: Today)).Value)
            .ToList();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(habits);
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).ToList());
        _unitOfWork.PassThroughTransactions<int>();
        var tool = new BulkUpdateHabitEmojisTool(_habitRepo, Substitute.For<IHabitEmojiInferenceService>(),
            _unitOfWork, NullLogger<BulkUpdateHabitEmojisTool>.Instance);
        var handler = CreateHandler(tool);
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new AiToolCall(tool.Name, "emoji_call", JsonDocument.Parse("""{"emoji":"✅","infer_from_title":false}""").RootElement)],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done." }));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Set all habit emojis"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var action = result.Value.Actions.Should().ContainSingle().Subject;
        action.EntityName.Should().Be($"Updated emojis for {count} of {count} matching habit(s). Skipped 0. Complete result.");
        using var json = JsonDocument.Parse(ChatStreamEvent.Final(result.Value).ToJson());
        var card = json.RootElement.GetProperty("response").GetProperty("actions")[0];
        card.TryGetProperty("messageKey", out var key).Should().BeTrue("every server action card needs a localisation key");
        key.GetString().Should().Be("chat.toolResult.updatedEmojis");
        var parameters = card.GetProperty("params");
        parameters.GetProperty("count").GetInt32().Should().Be(count);
        parameters.GetProperty("totalCount").GetInt32().Should().Be(count);
        parameters.GetProperty("skippedCount").GetInt32().Should().Be(0);
        parameters.GetProperty("partial").GetBoolean().Should().BeFalse();
    }
}
