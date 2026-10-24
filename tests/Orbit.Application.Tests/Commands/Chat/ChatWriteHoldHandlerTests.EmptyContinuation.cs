using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    public static TheoryData<string, string, bool, bool> EmptyContinuationCases => new()
    {
        { "en", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)", false, false },
        { "en", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)", true, false },
        { "pt-BR", "Post lunch: tomar vitamina D (uma vez por semana). Before bed: tomar rosuvastatina (todo dia)", false, false },
        { "pt-BR", "Post lunch: tomar vitamina D (uma vez por semana). Before bed: tomar rosuvastatina (todo dia)", true, false },
        { "en", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)", false, true },
        { "en", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)", true, true },
        { "pt-BR", "Post lunch: tomar vitamina D (uma vez por semana). Before bed: tomar rosuvastatina (todo dia)", false, true },
        { "pt-BR", "Post lunch: tomar vitamina D (uma vez por semana). Before bed: tomar rosuvastatina (todo dia)", true, true }
    };

    [Theory]
    [MemberData(nameof(EmptyContinuationCases))]
    public async Task EmptyContinuation_OwnerHeadingsReturnOneHeldPreview(
        string language, string message, bool writeFirst, bool stream)
    {
        var parents = ParentAccount(language);
        var extraParents = new[] { "Morning Routine", "Workout" }.Select(title => Habit.Create(
            new HabitCreateParams(_userId, title, FrequencyUnit.Day, 1, Today)).Value).ToArray();
        var account = parents.Concat(extraParents).ToArray();
        UseParentHabits(account);
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraBlockFirst });
        var vitamin = language == "en" ? "take vitamin D" : "tomar vitamina D";
        var statin = language == "en" ? "take rosuvastatin" : "tomar rosuvastatina";
        ScriptToolCall(writeFirst ? JsonSerializer.Serialize(new
        {
            habits = new[]
            {
                new { title = vitamin, frequency_unit = "Week", frequency_quantity = 1 },
                new { title = statin, frequency_unit = "Day", frequency_quantity = 1 }
            }
        }) : "{}", writeFirst ? "bulk_create_habits" : "query_habits");
        Guid? heldId = null;
        string? heldArguments = null;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.ArgAt<IReadOnlyList<AiToolCallResult>>(1).Should().ContainSingle().Which.Success.Should().BeTrue();
                if (writeFirst)
                {
                    var held = _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle().Which;
                    heldId = held.PendingOperationId;
                    heldArguments = held.Arguments.GetRawText();
                }
                return Result.Failure<AiResponse>(ErrorMessages.AiNoOutput);
            });
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new AgentClientContext(SupportsParts: true, SupportsPendingOperationChanges: true),
            StreamSink: stream ? item => { events.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items.Should().HaveCount(2);
        var stored = _pendingOperationStore.GetExecution(_userId, preview.Id)!;
        stored.OperationId.Should().Be("bulk_create_habits");
        var items = stored.Arguments.GetProperty("habits");
        items[0].GetProperty("title").GetString().Should().Be(vitamin);
        items[0].GetProperty("parent_habit_id").GetGuid().Should().Be(parents[0].Id);
        items[0].GetProperty("frequency_unit").GetString().Should().Be("Week");
        items[1].GetProperty("title").GetString().Should().Be(statin);
        items[1].GetProperty("parent_habit_id").GetGuid().Should().Be(parents[1].Id);
        items[1].GetProperty("frequency_unit").GetString().Should().Be("Day");
        if (writeFirst)
        {
            preview.Id.Should().Be(heldId.GetValueOrDefault());
            stored.Arguments.GetRawText().Should().Be(heldArguments);
        }
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeEmpty();
        result.Value.Actions.Should().BeEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle();
        _pendingOperationStore.IsAwaitingConfirmation(_userId, preview.Id).Should().BeTrue();
        account.Select(habit => habit.Title).Should().Equal("Post Lunch", "Before Bed", "Morning Routine", "Workout");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }
}
