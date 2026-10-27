using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class BulkCreateAgentOperationTests
{
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
