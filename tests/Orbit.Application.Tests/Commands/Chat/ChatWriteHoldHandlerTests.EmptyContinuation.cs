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
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EmptyContinuation_ApprovedHeadingWriteReturnsSavedOutcomeWithoutAnotherPreview(bool stream, bool laterRead)
    {
        var parents = ParentAccount("en");
        const string message = "Post lunch: take vitamin D (once a week)";
        ScriptToolCall(JsonSerializer.Serialize(new
        {
            habits = new[] { new { title = "take vitamin D", parent_habit_id = parents[0].Id, frequency_unit = "Week" } }
        }), "bulk_create_habits");
        var held = await CreateHandler().Handle(new(_userId, message), default);
        var preview = held.Value.PendingOperations.Should().ContainSingle().Which;
        var stored = _pendingOperationStore.GetExecution(_userId, preview.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, preview.Id)!;
        _habitRepo.AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>())
            .Returns(async call => { await _dbContext.Habits.AddAsync(call.Arg<Habit>(), call.Arg<CancellationToken>()); });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => _dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        ScriptToolCall(stored.Arguments.GetRawText(), stored.OperationId);
        var continuations = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _dbContext.Habits.Should().ContainSingle().Which.Should().Match<Habit>(habit =>
                    habit.Title == "take vitamin D" && habit.ParentHabitId == parents[0].Id
                    && habit.FrequencyUnit == FrequencyUnit.Week);
                _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
                return ++continuations == 1 && laterRead
                    ? Result.Success(new AiResponse
                    {
                        ToolCalls = [new("query_habits", "read_after_write", JsonSerializer.SerializeToElement(new { }))],
                        ConversationContext = new()
                    })
                    : Result.Failure<AiResponse>(ErrorMessages.AiNoOutput);
            });
        var outcomes = new List<AgentExecuteOperationResponse>();
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler(HoldReplayWrites(outcomes)).Handle(new(_userId, message,
            ConfirmationToken: confirmation.ConfirmationToken,
            ClientContext: new AgentClientContext(SupportsParts: true, SupportsPendingOperationChanges: true),
            StreamSink: stream ? item => { events.Add(item); return Task.CompletedTask; }
        : null), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().BeEmpty();
        var saved = outcomes.Should().ContainSingle(item => item.Operation.OperationId == stored.OperationId).Which;
        saved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        result.Value.Operations.Should().ContainSingle(item => item.OperationId == stored.OperationId)
            .Which.Should().BeEquivalentTo(saved.Operation);
        var action = result.Value.Actions.Should().ContainSingle().Which;
        action.Type.Should().Be("BulkCreateHabits");
        action.Status.Should().Be(ActionStatus.Success);
        action.Params!.Count.Should().Be(1);
        _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
        _pendingOperationStore.GetExecution(_userId, preview.Id).Should().BeNull();
        _dbContext.Habits.Should().ContainSingle();
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        continuations.Should().Be(laterRead ? 2 : 1);
        events.Should().NotContain(item => item.Type == "delta");
    }

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
            new HabitCreateParams(HabitSchedule.Recurring, _userId, title, FrequencyUnit.Day, 1, Today)).Value).ToArray();
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

        var confirmation = _pendingOperationStore.Confirm(_userId, preview.Id)!;
        var approved = await _executor.ExecuteAsync(new(_userId, stored.OperationId, stored.Arguments,
            stored.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.Title == vitamin
            && habit.ParentHabitId == parents[0].Id && habit.FrequencyUnit == FrequencyUnit.Week), Arg.Any<CancellationToken>());
        await _habitRepo.Received(1).AddAsync(Arg.Is<Habit>(habit => habit.Title == statin
            && habit.ParentHabitId == parents[1].Id && habit.FrequencyUnit == FrequencyUnit.Day), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EmptyContinuation_OnlyCurrentHeldWorkSurvivesProviderFailure(bool unavailable, bool expired)
    {
        ParentAccount("en");
        ScriptToolCall("""{"title":"Journal","frequency_unit":"Day"}""");
        var error = unavailable ? ErrorMessages.AiUnavailable : ErrorMessages.AiNoOutput;
        Guid pendingId = default;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var entity = _dbContext.PendingAgentOperations.Should().ContainSingle().Which;
                pendingId = entity.Id;
                if (expired)
                {
                    _dbContext.Entry(entity).Property(item => item.ExpiresAtUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
                    _dbContext.SaveChanges();
                }
                return Result.Failure<AiResponse>(error);
            });

        var result = await CreateHandler().Handle(new(_userId, "Create a daily Journal habit"), default);

        result.IsSuccess.Should().Be(!expired);
        if (expired)
            result.ErrorCode.Should().Be(error.Code);
        else
        {
            result.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(pendingId);
            result.Value.AiMessage.Should().Contain("Journal").And.Contain("approving");
        }
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("AI_EMPTY_RESPONSE", "Review my routines")]
    [InlineData("AI_UNAVAILABLE", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)")]
    [InlineData("AI_DAILY_BUDGET_EXCEEDED", "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)")]
    public async Task EmptyContinuation_WithoutRecoverableWorkPreservesTypedFailure(string errorCode, string message)
    {
        ParentAccount("en");
        ScriptToolCall("{}", "query_habits");
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(new AppError(errorCode, "Provider failure")));

        var result = await CreateHandler().Handle(new(_userId, message), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(errorCode);
        _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyContinuation_MixedHeldWritesRemainOneAccountApproval()
    {
        var parents = ParentAccount("en");
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [
                    new("update_habit", "edit", JsonSerializer.SerializeToElement(new { habit_id = parents[0].Id, title = "After Lunch" })),
                    new("create_sub_habit", "child", JsonSerializer.SerializeToElement(new { parent_habit_id = parents[1].Id, title = "Journal", frequency_unit = "Day" }))
                ],
                ConversationContext = new()
            }));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(ErrorMessages.AiUnavailable));

        var result = await CreateHandler().Handle(new(_userId, "Rename Post Lunch to After Lunch and add Journal under Before Bed"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items.Should().HaveCount(2);
        _pendingOperationStore.GetExecution(_userId, preview.Id)!.OperationId.Should().Be("apply_account_changes");
        result.Value.AiMessage.Should().Contain("approving");
        parents[0].Title.Should().Be("Post Lunch");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
