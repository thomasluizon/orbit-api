using System.Text.Json;
using FluentAssertions;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class BulkCreateAgentOperationTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("failure")]
    [InlineData("partial")]
    public async Task RestartPlan_SecondWriteFailure_RollsBackAllDatesAndApprovalThenRetries(string failure)
    {
        var today = new DateOnly(2026, 3, 20);
        var first = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Read",
            FrequencyUnit.Day, 1, today)).Value;
        var second = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Walk",
            FrequencyUnit.Day, 1, today.AddDays(-1))).Value;
        _factory.Context.Habits.AddRange(first, second);
        await _factory.Context.SaveChangesAsync();
        var original = ReadDates();
        var args = JsonSerializer.SerializeToElement(new
        {
            revised_items = new[]
            {
                new { habit_id = first.Id, updates = new { due_date = "2026-03-21" } },
                new { habit_id = second.Id, updates = new { due_date = "2026-03-22" } }
            }
        });
        var request = new AgentExecuteOperationRequest(_userId, "bulk_reschedule_habits", args,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt);
        var held = await _executor.ExecuteAsync(request);
        held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var preview = held.PendingOperation!;
        var shown = preview.Items!.ToDictionary(item => item.EntityId!.Value,
            item => DateOnly.ParseExact(item.Fields.Single().NewValue!, "yyyy-MM-dd"));
        shown.Should().BeEquivalentTo(new Dictionary<Guid, DateOnly>
        {
            [first.Id] = today.AddDays(1),
            [second.Id] = today.AddDays(2)
        });
        ReadDates().Should().BeEquivalentTo(original);
        var confirmation = _pending.Confirm(_userId, preview.Id)!;
        var execution = _pending.GetExecution(_userId, preview.Id)!;
        request = request with { Arguments = execution.Arguments, ConfirmationToken = confirmation.ConfirmationToken };
        using var cancellation = new CancellationTokenSource();
        _rescheduleFailure.Target = second.Id;
        _rescheduleFailure.Failure = failure;
        _rescheduleFailure.Cancellation = cancellation;

        var failed = await _executor.ExecuteAsync(request, cancellation.Token);

        _rescheduleFailure.CompletedWrites.Should().Be(1);
        failed.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        ReadDates().Should().BeEquivalentTo(original, "every proposed date must roll back together");
        using (var verify = _factory.CreateContext())
            verify.PendingAgentOperations.Single().ConsumedAtUtc.Should().BeNull();
        _pending.GetExecution(_userId, preview.Id).Should().NotBeNull();
        _rescheduleFailure.Failure = null;

        var retried = await _executor.ExecuteAsync(request);

        retried.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        ReadDates().Should().BeEquivalentTo(shown);
        using (var verify = _factory.CreateContext())
            verify.PendingAgentOperations.Single().ConsumedAtUtc.Should().NotBeNull();
        _pending.GetExecution(_userId, preview.Id).Should().BeNull();
        var writes = _rescheduleFailure.CompletedWrites;
        var repeated = await _executor.ExecuteAsync(request);
        repeated.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
        _rescheduleFailure.CompletedWrites.Should().Be(writes);
        ReadDates().Should().BeEquivalentTo(shown);
    }

    private Dictionary<Guid, DateOnly> ReadDates()
    {
        using var verify = _factory.CreateContext();
        return verify.Habits.ToDictionary(habit => habit.Id, habit => habit.NextDueDate);
    }

    private sealed class RescheduleFailureBehavior : IPipelineBehavior<BulkUpdateHabitsCommand, Result<BulkHabitMutationResult>>
    {
        public Guid Target { get; set; }
        public string? Failure { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public int CompletedWrites { get; private set; }

        public async Task<Result<BulkHabitMutationResult>> Handle(BulkUpdateHabitsCommand request,
            RequestHandlerDelegate<Result<BulkHabitMutationResult>> next, CancellationToken cancellationToken)
        {
            if (Failure is not null && request.Filter.HabitIds.Contains(Target))
            {
                if (Failure == "cancel")
                {
                    Cancellation!.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return Failure == "partial"
                    ? Result.Success(new BulkHabitMutationResult(0, 1, 1, true))
                    : Result.Failure<BulkHabitMutationResult>("Second reschedule failed");
            }
            var result = await next(cancellationToken);
            CompletedWrites++;
            return result;
        }
    }
}
