using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    private static readonly DateOnly MonthDaysToday = new(2026, 11, 6);
    private const string TwoHabitMonthDaysRequest =
        "Change only the days of the month of my habits \"Month days A\" and \"Month days B\" to the 5th and the 25th.";

    [Fact]
    public async Task MonthDays_PlanOnlyRoundAfterRead_PreparesOneBulkPreviewWithAMonthDaysRowPerHabit()
    {
        var (first, second) = SetupMonthlyHabits();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ModelRound(new AiToolCall("query_habits", "read",
                JsonSerializer.SerializeToElement(new { search = "Month days" })))));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(MonthDaysPlanRound()), Result.Success(ModelRound(BulkMonthDays(first, second))),
                Result.Success(new AiResponse { TextMessage = "Review the new days." }));

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, TwoHabitMonthDaysRequest), default);

        AssertMonthDaysPreview(result, first, second);
    }

    [Fact]
    public async Task MonthDays_PlanOnlyFirstRound_PreparesThePreviewAndLogsTheHeldRound()
    {
        var (first, second) = SetupMonthlyHabits();
        _handlerLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(MonthDaysPlanRound()));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ModelRound(BulkMonthDays(first, second))),
                Result.Success(new AiResponse { TextMessage = "Review the new days." }));

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId,
            $"Use bulk_update_habits so that the habits \"Month days A\" (id {first.Id}) and \"Month days B\" (id {second.Id}) repeat on days 5 and 25 of every month. Change nothing else."), default);

        AssertMonthDaysPreview(result, first, second);
        _handlerLogger.Received(1).Log(LogLevel.Warning, Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("present_plan")), Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Theory]
    [InlineData("Yes, do that")]
    [InlineData("Yes, prepare that change for me to approve.")]
    [InlineData("Sim, pode fazer")]
    public async Task MonthDays_ConfirmationOfThePreviousScheduleRequest_IsNotRejectedAsACorrection(string confirmation)
    {
        var (first, second) = SetupMonthlyHabits();
        ScriptBulkMonthDays(first, second, new { month_days = new[] { 5, 25 } });

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, confirmation,
            History: [new("user", TwoHabitMonthDaysRequest), new("assistant", "Should I prepare that change?")]), default);

        AssertMonthDaysPreview(result, first, second);
        LoggedRejection().Should().BeFalse();
    }

    [Theory]
    [InlineData("Make both positive", TwoHabitMonthDaysRequest)]
    [InlineData("Yes, do that", "Make both of them positive.")]
    public async Task MonthDays_CorrectionThatDriftsTheSchedule_IsStillRejected(string message, string previousRequest)
    {
        var (first, second) = SetupMonthlyHabits();
        ScriptBulkMonthDays(first, second, new { is_bad_habit = false, month_days = new[] { 5, 25 } });

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, message,
            History: [new("user", previousRequest), new("assistant", "Should I prepare that change?")]), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().BeEmpty();
        LoggedRejection().Should().BeTrue();
        first.MonthDays.Should().Equal(8, 15);
        second.MonthDays.Should().BeEmpty();
    }

    private (Habit First, Habit Second) SetupMonthlyHabits()
    {
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(MonthDaysToday);
        var first = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Month days A", FrequencyUnit.Month, 1,
            new DateOnly(2026, 11, 1), StartDate: new DateOnly(2026, 11, 1), WeekStartDay: 1, MonthDays: [8, 15])).Value;
        var second = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Month days B", FrequencyUnit.Month, 1,
            new DateOnly(2026, 11, 3), StartDate: new DateOnly(2026, 11, 3), WeekStartDay: 1)).Value;
        first.CatchUpDueDate(MonthDaysToday, 1).IsSuccess.Should().BeTrue();
        second.CatchUpDueDate(MonthDaysToday, 1).IsSuccess.Should().BeTrue();
        Habit[] habits = [first, second];
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _unitOfWork.PassThroughTransactions<Result<int>>();
        var bulkHandler = new BulkUpdateHabitsCommandHandler(_habitRepo, _userDateService, _payGate,
            _unitOfWork, _flagCache, NullLogger<BulkUpdateHabitsCommandHandler>.Instance);
        _mediator.Send(Arg.Any<BulkUpdateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => bulkHandler.Handle(call.Arg<BulkUpdateHabitsCommand>(), call.Arg<CancellationToken>()));
        return (first, second);
    }

    private void ScriptBulkMonthDays(Habit first, Habit second, object updates)
    {
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ModelRound(BulkMonthDays(first, second, updates))));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Review the new days." }));
    }

    private static AiToolCall BulkMonthDays(Habit first, Habit second, object? updates = null) =>
        new("bulk_update_habits", "bulk", JsonSerializer.SerializeToElement(new
        {
            filter = new { habit_ids = new[] { first.Id.ToString(), second.Id.ToString() } },
            updates = updates ?? new { month_days = new[] { 5, 25 } }
        }));

    private static AiResponse ModelRound(params AiToolCall[] calls) => new()
    {
        ToolCalls = calls,
        ConversationContext = new() { Messages = new List<object>(), Options = new object() }
    };

    private static AiResponse MonthDaysPlanRound() => ModelRound(new AiToolCall("present_plan", "plan",
        JsonSerializer.SerializeToElement(new
        {
            title = "New days of the month",
            steps = new[]
            {
                new { text = "Prepare the new days", tool = "bulk_update_habits", phase = "execution" },
                new { text = "Save after approval", tool = "bulk_update_habits", phase = "approval" }
            }
        })));

    private bool LoggedRejection() => _handlerLogger.ReceivedCalls().Any(call =>
        call.GetMethodInfo().Name == nameof(ILogger.Log)
        && call.GetArguments()[2]?.ToString() is { } text
        && text.Contains("bulk_update_habits", StringComparison.Ordinal)
        && text.Contains("CORRECTION_PRESERVES_SCHEDULE", StringComparison.Ordinal));

    private void AssertMonthDaysPreview(Result<ChatResponse> result, Habit first, Habit second)
    {
        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().NotBe(EnglishToolFailure);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Subject;
        _pendingOperationStore.GetExecution(_userId, pending.Id)!.OperationId.Should().Be("bulk_update_habits");
        var rows = pending.Items!.Select(item => (item.EntityId, Row: item.Fields.Single(field => field.Field == "month_days")))
            .ToDictionary(item => item.EntityId!.Value, item => item.Row);
        rows.Keys.Should().BeEquivalentTo([first.Id, second.Id]);
        rows[first.Id].OldValue.Should().Be("8, 15");
        rows[second.Id].OldValue.Should().Be("(none)");
        rows.Values.Should().OnlyContain(row => row.NewValue == "5, 25");
        first.MonthDays.Should().Equal(8, 15);
        second.MonthDays.Should().BeEmpty();
    }
}
