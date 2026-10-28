using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", "approve", true)]
    [InlineData("pt-BR", "approve", true)]
    [InlineData("en", "edit", true)]
    [InlineData("pt-BR", "edit", true)]
    [InlineData("en", "reject", true)]
    [InlineData("pt-BR", "reject", true)]
    [InlineData("en", "approve", false)]
    [InlineData("pt-BR", "approve", false)]
    public async Task RestartPlan_ModelPortReplay_PreviewsThenAppliesOnlyApprovedDates(string language, string action, bool composition)
    {
        _user.SetLanguage(language);
        if (composition)
            _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
                .Returns([FeatureFlagKeys.AstraBlockFirst]);
        var keep = RestartHabit(language == "en" ? "Read" : "Ler", Today);
        var move = RestartHabit(language == "en" ? "Walk" : "Caminhar", Today.AddDays(-2));
        var later = RestartHabit(language == "en" ? "Stretch" : "Alongar", Today.AddDays(-1));
        var future = RestartHabit("Future", Today.AddDays(7));
        var unrelated = RestartHabit("Other owner", Today, Guid.NewGuid());
        move.Log(Today.AddDays(-2), "Keep history", advanceDueDate: false).IsSuccess.Should().BeTrue();
        var habits = new[] { keep, move, later, future, unrelated };
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();
        SetupRestartRepository();
        var original = await RestartReadbackAsync();
        using var fixture = JsonDocument.Parse((await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "RestartPlans", language + ".json")))
            .Replace("__keep__", keep.Id.ToString(), StringComparison.Ordinal)
            .Replace("__move__", move.Id.ToString(), StringComparison.Ordinal)
            .Replace("__later__", later.Id.ToString(), StringComparison.Ordinal));
        var capture = fixture.RootElement;
        static AiResponse Replay(JsonElement call) => new()
        {
            ToolCalls = [new AiToolCall(call.GetProperty("name").GetString()!, call.GetProperty("id").GetString()!, call.GetProperty("args").Clone())],
            ConversationContext = new() { Messages = new List<object>(), Options = new object() }
        };
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Replay(capture.GetProperty("read"))));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var result = call.Arg<IReadOnlyList<AiToolCallResult>>().Should().ContainSingle().Which;
            result.Success.Should().BeTrue();
            var rows = result.Payload.Should().BeOfType<HabitListCard>().Which;
            rows.Items.Select(item => item.Id).Should().BeEquivalentTo(keep.Id.ToString(), move.Id.ToString(), later.Id.ToString());
            return Result.Success(Replay(capture.GetProperty("plan")));
        });

        var held = await CreateHandler().Handle(new(_userId, capture.GetProperty("message").GetString()!,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, SupportsHabitListCard: true, Locale: language)), default);

        held.IsSuccess.Should().BeTrue(held.Error);
        held.Value.AiMessage.Should().BeNullOrEmpty();
        held.Value.Parts.Should().BeNullOrEmpty("the plan is one preview without prerequisite read blocks");
        var pending = held.Value.PendingOperations.Should().ContainSingle().Which;
        pending.ActionKey.Should().Be("rescheduleHabits");
        pending.Items.Should().HaveCount(3);
        var shown = pending.Items!.ToDictionary(item => item.EntityId!.Value,
            item => item.Fields.Should().ContainSingle().Which);
        shown.Values.Should().OnlyContain(field => field.Field == "due_date" && field.IsEditable && field.ValueType == "date");
        shown[keep.Id].NewValue.Should().Be("2026-04-03");
        shown[move.Id].NewValue.Should().Be("2026-04-04");
        shown[later.Id].NewValue.Should().Be("2026-04-05");
        (await RestartReadbackAsync()).Should().BeEquivalentTo(original);

        if (action == "reject")
        {
            var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
            _pendingOperationStore.Cancel(_userId, pending.Id,
                AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText())).Should().BeTrue();
            _pendingOperationStore.Confirm(_userId, pending.Id).Should().BeNull();
            (await RestartReadbackAsync()).Should().BeEquivalentTo(original);
            return;
        }
        if (action == "edit")
        {
            var selected = pending.Items.Select(item => new RevisedPendingOperationItem(item.ItemId,
                item.EntityId == move.Id ? JsonSerializer.SerializeToElement(new { due_date = "2026-04-06" }) : null)).ToArray();
            var revised = await ParentRevisionService().ReviseAsync(_userId, pending.Id,
                new(pending.PreviewFingerprint!, selected), default);
            revised.IsSuccess.Should().BeTrue(revised.Error);
            shown = revised.Preview!.Items!.ToDictionary(item => item.EntityId!.Value, item => item.Fields.Single());
            shown[move.Id].NewValue.Should().Be("2026-04-06");
            shown[keep.Id].NewValue.Should().Be("2026-04-03");
            shown[later.Id].NewValue.Should().Be("2026-04-05");
            (await RestartReadbackAsync()).Should().BeEquivalentTo(original);
        }
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;

        var applied = await _executor.ExecuteAsync(new(_userId, execution.OperationId, execution.Arguments,
            execution.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken), default);

        applied.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, "{0}", JsonSerializer.Serialize(applied));
        var readback = await RestartReadbackAsync();
        foreach (var habit in habits)
        {
            var expected = shown.TryGetValue(habit.Id, out var field)
                ? original[habit.Id] with { NextDueDate = DateOnly.ParseExact(field.NewValue!, "yyyy-MM-dd") }
                : original[habit.Id];
            readback[habit.Id].Should().BeEquivalentTo(expected);
        }
        _pendingOperationStore.GetExecution(_userId, pending.Id).Should().BeNull();
        var writes = _unitOfWork.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IUnitOfWork.SaveChangesAsync));
        var repeated = await _executor.ExecuteAsync(new(_userId, execution.OperationId, execution.Arguments,
            execution.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken), default);
        repeated.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
        _unitOfWork.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IUnitOfWork.SaveChangesAsync)).Should().Be(writes);
        _dbContext.ChangeTracker.Clear();
        var today = await _toolRegistry.GetTool("query_habits")!.ExecuteAsync(capture.GetProperty("read").GetProperty("args"), _userId, default);
        today.Success.Should().BeTrue(today.Error);
        today.Payload.Should().BeOfType<HabitListCard>().Which.Items.Select(item => item.Id)
            .Should().Equal(keep.Id.ToString());
    }

    private Habit RestartHabit(string title, DateOnly date, Guid? owner = null) => Habit.Create(new HabitCreateParams(
        HabitSchedule.Recurring, owner ?? _userId, title, FrequencyUnit.Day, 1, date,
        Description: "Keep description", DueTime: new TimeOnly(8, 0), Emoji: "📖",
        ReminderEnabled: true, ReminderTimes: [30], ChecklistItems: [new ChecklistItem("Keep checklist", false)])).Value;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartPlan_ForeignOrDuplicateTargetCannotCreatePreview(bool duplicate)
    {
        var own = RestartHabit("Own", Today);
        var foreign = RestartHabit("Foreign", Today, Guid.NewGuid());
        _dbContext.Habits.AddRange(own, foreign);
        await _dbContext.SaveChangesAsync();
        SetupRestartRepository();
        var original = await RestartReadbackAsync();
        var args = JsonSerializer.SerializeToElement(new
        {
            revised_items = new[]
            {
                new { habit_id = own.Id, updates = new { due_date = "2026-04-03" } },
                new { habit_id = duplicate ? own.Id : foreign.Id, updates = new { due_date = "2026-04-04" } }
            }
        });

        var held = await _executor.ExecuteAsync(new(_userId, "bulk_reschedule_habits", args,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), default);

        held.PendingOperation.Should().BeNull();
        held.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        (await RestartReadbackAsync()).Should().BeEquivalentTo(original);
    }

    private void SetupRestartRepository()
    {
        _unitOfWork.PassThroughTransactions<Result<int>>();
        IReadOnlyList<Habit> Read(Expression<Func<Habit, bool>> predicate) => _dbContext.Habits.Where(predicate).ToList();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Read(call.Arg<Expression<Func<Habit, bool>>>()));
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Read(call.Arg<Expression<Func<Habit, bool>>>()));
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => Read(call.Arg<Expression<Func<Habit, bool>>>()));
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Read(call.Arg<Expression<Func<Habit, bool>>>()));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => _dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
    }

    private sealed record RestartState(DateOnly NextDueDate, string Title, string? Description, string? Emoji,
        FrequencyUnit? FrequencyUnit, int? FrequencyQuantity, TimeOnly? DueTime, bool IsCompleted,
        bool ReminderEnabled, string ReminderTimes, string Checklist, string Logs);

    private async Task<Dictionary<Guid, RestartState>> RestartReadbackAsync() => (await _dbContext.Habits.AsNoTracking()
        .Include(habit => habit.Logs).ToListAsync()).ToDictionary(habit => habit.Id, habit => new RestartState(
            habit.NextDueDate, habit.Title, habit.Description, habit.Emoji, habit.FrequencyUnit, habit.FrequencyQuantity,
            habit.DueTime, habit.IsCompleted, habit.ReminderEnabled, JsonSerializer.Serialize(habit.ReminderTimes),
            JsonSerializer.Serialize(habit.ChecklistItems),
            JsonSerializer.Serialize(habit.Logs.OrderBy(log => log.Id).Select(log => new { log.Id, log.Date, log.Value, log.Note }))));
}
