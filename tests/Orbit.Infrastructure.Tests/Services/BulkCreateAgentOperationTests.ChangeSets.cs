using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class BulkCreateAgentOperationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompoundApproval_BulkSkipRequiresEveryItemToSucceed(bool exceedsWeeklyQuota)
    {
        var today = new DateOnly(2026, 3, 20);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, _userId, "Workout",
            FrequencyUnit.Week, 1, today.AddDays(-1))).Value;
        _factory.Context.Habits.Add(habit);
        await _factory.Context.SaveChangesAsync();
        var items = exceedsWeeklyQuota
            ? new[] { new { habit_id = habit.Id, date = today.AddDays(-1) }, new { habit_id = habit.Id, date = today } }
            : new[] { new { habit_id = habit.Id, date = today } };
        var args = JsonSerializer.SerializeToElement(new { revised_items = items });
        var check = await ((IArgumentCheckTool)_registry.GetTool("bulk_skip_habits")!).CheckArgumentsAsync(args, _userId, default);
        check.IsSuccess.Should().BeTrue(check.Error);
        var creation = await _executor.ExecuteAsync(new(_userId, "bulk_create_habits",
            JsonSerializer.SerializeToElement(new { habits = new[] { new { title = "Read", frequency_unit = "Day" } } }),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        var skip = await _executor.ExecuteAsync(new(_userId, "bulk_skip_habits", args,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
        creation.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        skip.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var service = new AccountChangeSetService(_pending, _previewer,
            new AgentCatalogService(_registry.GetAll()), _registry);
        var combined = await service.CombineAsync(_userId, [creation.PendingOperation!, skip.PendingOperation!], default);
        combined.Should().NotBeNull();
        var pending = combined!.PendingOperation;
        pending.Items.Should().HaveCount(exceedsWeeklyQuota ? 3 : 2);
        var execution = _pending.GetExecution(_userId, pending.Id)!;
        var confirmation = _pending.Confirm(_userId, pending.Id)!;
        var request = new AgentExecuteOperationRequest(_userId, AccountChangeSet.OperationName, execution.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken);

        var result = await _executor.ExecuteAsync(request);

        result.Operation.Status.Should().Be(exceedsWeeklyQuota ? AgentOperationStatus.Failed : AgentOperationStatus.Succeeded);
        var outcomes = result.Operation.Payload.Should().BeAssignableTo<IReadOnlyList<AgentOperationResult>>().Subject;
        outcomes.Should().HaveCount(2);
        outcomes[1].OperationId.Should().Be("bulk_skip_habits");
        var skipResult = JsonSerializer.SerializeToElement(outcomes[1].Payload);
        skipResult.GetProperty("partial").GetBoolean().Should().Be(exceedsWeeklyQuota);
        skipResult.GetProperty("applied_count").GetInt32().Should().Be(1);
        skipResult.GetProperty("total_matched").GetInt32().Should().Be(items.Length);
        using var verify = _factory.CreateContext();
        if (exceedsWeeklyQuota)
        {
            result.Operation.PolicyReason.Should().Be("change_set_failed");
            verify.Habits.Should().ContainSingle().Which.Id.Should().Be(habit.Id);
            verify.HabitLogs.Should().BeEmpty();
            verify.PendingAgentOperations.Should().HaveCount(3).And.OnlyContain(operation => operation.ConsumedAtUtc == null);
            foreach (var member in AccountChangeSet.Read(execution.Arguments)!)
                _pending.GetExecution(_userId, member.PendingOperationId).Should().NotBeNull();
            _pending.GetExecution(_userId, pending.Id).Should().NotBeNull();
            _pending.Confirm(_userId, pending.Id).Should().NotBeNull();
        }
        else
        {
            verify.Habits.Select(saved => saved.Title).Should().BeEquivalentTo("Workout", "Read");
            verify.HabitLogs.Should().ContainSingle().Which.Date.Should().Be(today);
            foreach (var member in AccountChangeSet.Read(execution.Arguments)!)
                _pending.GetExecution(_userId, member.PendingOperationId).Should().BeNull();
            _pending.GetExecution(_userId, pending.Id).Should().BeNull();
        }
    }

    [Fact]
    public async Task CompoundApproval_LaterDatabaseFailure_RollsBackAllMembersAndCanRetryOnce()
    {
        var request = await ConfirmChangeSetAsync("First", "Second");
        _afterWriteFailure.Enabled = true;
        _afterWriteFailure.FailOnTitle = "Second";

        var failed = await _executor.ExecuteAsync(request);

        failed.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        using (var verify = _factory.CreateContext())
        {
            verify.Habits.Should().BeEmpty();
            verify.PendingAgentOperations.Should().OnlyContain(pending => pending.ConsumedAtUtc == null);
        }
        _afterWriteFailure.Enabled = false;
        var retried = await _executor.ExecuteAsync(request);
        retried.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        var replayed = await _executor.ExecuteAsync(request);
        replayed.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
        using var saved = _factory.CreateContext();
        saved.Habits.Select(habit => habit.Title).Should().BeEquivalentTo(["First", "Second"]);
        foreach (var member in AccountChangeSet.Read(request.Arguments)!)
            _pending.GetExecution(_userId, member.PendingOperationId).Should().BeNull();
    }

    [Fact]
    public async Task CompoundApproval_StaleMember_RejectsBeforeAnyWrite()
    {
        var request = await ConfirmChangeSetAsync("First", "Second");
        var member = AccountChangeSet.Read(request.Arguments)![1];
        var entity = _factory.Context.PendingAgentOperations.Single(pending => pending.Id == member.PendingOperationId);
        entity.Cancel(TimeProvider.System.GetUtcNow().UtcDateTime).Should().BeTrue();
        await _factory.Context.SaveChangesAsync();

        var response = await _executor.ExecuteAsync(request);

        response.Operation.PolicyReason.Should().Be("stale_preview");
        using var verify = _factory.CreateContext();
        verify.Habits.Should().BeEmpty();
    }

    [Fact]
    public async Task CompoundApproval_InvalidLaterMember_RejectsBeforeAnyWrite()
    {
        var request = await ConfirmChangeSetAsync("First", "");

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        using var verify = _factory.CreateContext();
        verify.Habits.Should().BeEmpty();
        verify.PendingAgentOperations.Should().OnlyContain(pending => pending.ConsumedAtUtc == null);
    }

    private async Task<AgentExecuteOperationRequest> ConfirmChangeSetAsync(params string[] titles)
    {
        var members = new List<PendingAgentOperation>();
        foreach (var title in titles)
        {
            var args = JsonSerializer.SerializeToElement(new { habits = new[] { new { title, frequency_unit = "Day" } } });
            var held = await _executor.ExecuteAsync(new(_userId, "bulk_create_habits", args,
                AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));
            held.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
            members.Add(held.PendingOperation!);
        }
        var catalog = new AgentCatalogService(_registry.GetAll());
        var service = new AccountChangeSetService(_pending, _previewer, catalog, _registry);
        var combined = await service.CombineAsync(_userId, members, default);
        combined.Should().NotBeNull();
        combined!.MemberIds.Should().BeEquivalentTo(members.Select(member => member.Id));
        combined.PendingOperation.Items.Should().HaveCount(members.Count);
        var execution = _pending.GetExecution(_userId, combined.PendingOperation.Id)!;
        var confirmation = _pending.Confirm(_userId, combined.PendingOperation.Id)!;
        return new(_userId, AccountChangeSet.OperationName, execution.Arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            ConfirmationToken: confirmation.ConfirmationToken);
    }
}
