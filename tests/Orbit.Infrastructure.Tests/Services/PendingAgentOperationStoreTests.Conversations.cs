using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public partial class PendingAgentOperationStoreTests
{
    private PendingAgentOperation ConversationProposal(PendingOperationConversation conversation, string title,
        Guid? userId = null, bool bind = true)
    {
        var arguments = JsonSerializer.Serialize(new { title, frequency_unit = "OneTime", due_date = "2026-12-31", due_time = "20:30" });
        var fingerprint = AgentOperationFingerprint.Compute("create_habit", arguments);
        var pending = _store.Create(userId ?? _userId, _catalogService.GetCapabilityByChatTool("create_habit")!,
            "create_habit", arguments, title, fingerprint, AgentExecutionSurface.Chat, conversation);
        if (bind)
            _store.Revise(userId ?? _userId, pending.Id, fingerprint, arguments, fingerprint, "preview").Should().BeTrue();
        return pending;
    }

    [Fact]
    public void Consume_StaleRevisionInExistingTransaction_PreservesEditedHistory()
    {
        using var fixture = new ConversationTurnRecorderTests.Fixture();
        var db = fixture.Db;
        var settings = Options.Create(new AgentPlatformSettings());
        var store = new PendingAgentOperationStore(db, settings, fixture.Flags);
        var capability = _catalogService.GetCapabilityByChatTool("create_habit")! with
        { ConfirmationRequirement = AgentConfirmationRequirement.FreshConfirmation };
        const string original = "{\"title\":\"Read\"}";
        var fingerprint = AgentOperationFingerprint.Compute("create_habit", original);
        var pending = store.Create(fixture.User.Id, capability, "create_habit", original, "Create", fingerprint,
            AgentExecutionSurface.Chat, new(fixture.ConversationId, DateTime.UtcNow));
        var confirmation = store.Confirm(fixture.User.Id, pending.Id)!;
        var state = db.PendingAgentOperations.Single();
        var preview = new ConversationPreviewOutcome(pending.Id, "approved", JsonSerializer.SerializeToElement(new { title = "Read" }), state.RevisionNumber);
        var turn = ConversationTurn.Create(fixture.User.Id, Guid.NewGuid(), "assistant", DateTime.UtcNow,
            null, "Review", "[]", conversationId: fixture.ConversationId, turnOrder: 1,
            previewOutcomesJson: JsonSerializer.Serialize(new[] { preview }), modelContextJson: "{}").Value;
        db.ConversationTurns.Add(turn);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        db.PendingAgentOperations.Single();
        using (var transaction = db.Database.BeginTransaction())
        {
            Action? revise = () =>
            {
                using var revisionDb = fixture.Database.CreateContext();
                revisionDb.Database.UseTransaction(transaction.GetDbTransaction());
                var revisionStore = new PendingAgentOperationStore(revisionDb, settings, fixture.Flags);
                const string revised = "{\"title\":\"Walk\"}";
                revisionStore.Revise(fixture.User.Id, pending.Id, fingerprint, revised,
                    AgentOperationFingerprint.Compute("create_habit", revised), "edited-preview").Should().BeTrue();
            };
            fixture.Flags.ResolveForUser(fixture.User.Id).Returns(_ =>
            {
                var callback = revise;
                revise = null;
                callback?.Invoke();
                return [new(FeatureFlagKeys.AiConversationRecording, true, null)];
            });
            store.TryConsumeFreshConfirmation(fixture.User.Id, capability.Id, fingerprint,
                confirmation.ConfirmationToken, false).Should().BeFalse();
            transaction.Commit();
        }
        db.ChangeTracker.Clear();
        var persisted = db.PendingAgentOperations.Single();
        persisted.ConfirmedAtUtc.Should().BeNull();
        persisted.ConsumedAtUtc.Should().BeNull();
        var outcome = JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(db.ConversationTurns.Single().PreviewOutcomesJson)!.Single();
        outcome.Outcome.Should().Be("edited");
        outcome.Arguments.GetProperty("title").GetString().Should().Be("Walk");
        outcome.Revision.Should().Be(persisted.RevisionNumber);
    }

    [Theory]
    [InlineData("pending", "pending")]
    [InlineData("edited", "edited")]
    [InlineData("confirmed", "approved")]
    [InlineData("consumed", "approved")]
    [InlineData("rejected", "rejected")]
    [InlineData("expired", "expired")]
    [InlineData("confirmed-expired", "approved")]
    [InlineData("edited-expired", "expired")]
    public async Task LastConversationPreview_UsesCurrentStateOutcome(string transition, string expected)
    {
        var conversation = new PendingOperationConversation(Guid.NewGuid(), DateTime.UtcNow);
        var pending = ConversationProposal(conversation, "Reservation");
        var state = await _dbContext.PendingAgentOperations.SingleAsync(item => item.Id == pending.Id);
        if (transition is "edited" or "edited-expired")
        {
            var changed = state.ArgumentsJson.Replace("20:30", "21:00", StringComparison.Ordinal);
            _store.Revise(_userId, pending.Id, state.OperationFingerprint, changed,
                AgentOperationFingerprint.Compute(state.OperationId, changed), "edited-preview").Should().BeTrue();
        }
        if (transition is "confirmed" or "confirmed-expired" or "consumed")
            _store.Confirm(_userId, pending.Id).Should().NotBeNull();
        if (transition == "consumed") state.MarkConsumed();
        if (transition == "rejected") _store.Cancel(_userId, pending.Id, state.OperationFingerprint).Should().BeTrue();
        if (transition is "expired" or "confirmed-expired" or "edited-expired")
            _dbContext.Entry(state).Property(item => item.ExpiresAtUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var preview = (await _store.GetLastConversationPreviewsAsync(_userId, conversation.Id)).Should().ContainSingle().Which;
        preview.Outcome.Should().Be(expected);
        preview.Revision.Should().Be(state.RevisionNumber);
        preview.CurrentArguments.GetProperty("due_date").GetString().Should().Be("2026-12-31");
        preview.CurrentArguments.GetProperty("due_time").GetString().Should()
            .Be(transition is "edited" or "edited-expired" ? "21:00" : "20:30");
        (await _dbContext.PendingAgentOperations.SingleAsync()).ConversationId.Should().Be(conversation.Id);
    }

    [Fact]
    public async Task LastConversationBatch_ExcludesOlderOtherUserOtherConversationAndUnboundOperations()
    {
        var nowAtUtc = DateTime.UtcNow;
        var id = Guid.NewGuid();
        ConversationProposal(new(id, nowAtUtc.AddMinutes(-3)), "Older");
        var batch = new PendingOperationConversation(id, nowAtUtc.AddMinutes(-2));
        var first = ConversationProposal(batch, "Reservation");
        var second = ConversationProposal(batch, "Travel");
        ConversationProposal(new(Guid.NewGuid(), nowAtUtc), "Other conversation");
        ConversationProposal(new(id, nowAtUtc), "Other user", Guid.NewGuid());
        ConversationProposal(new(id, nowAtUtc), "Not shown", bind: false);
        _dbContext.ChangeTracker.Clear();

        var previews = await _store.GetLastConversationPreviewsAsync(_userId, id);
        previews.Select(item => item.Proposal.Id).Should().BeEquivalentTo([first.Id, second.Id]);
        (await _store.GetLastConversationPreviewsAsync(_userId, Guid.NewGuid())).Should().BeEmpty();
        (await _store.GetLastConversationPreviewsAsync(_userId, Guid.Empty)).Should().BeEmpty();
        (await _store.GetLastConversationPreviewsAsync(Guid.Empty, id)).Should().BeEmpty();
    }

    /// <summary>
    /// A turn withdraws its preview before the person sees it. The row closes like a cancel, and the conversation lists
    /// the batch that the turn before it showed, never the withdrawn row as a rejected proposal.
    /// </summary>
    [Fact]
    public async Task Withdraw_ClosesTheRowAndKeepsItOutOfTheLastConversationBatch()
    {
        var id = Guid.NewGuid();
        var nowAtUtc = DateTime.UtcNow;
        var shown = ConversationProposal(new(id, nowAtUtc.AddMinutes(-1)), "Reservation");
        var withdrawn = ConversationProposal(new(id, nowAtUtc), "Travel");
        var state = await _dbContext.PendingAgentOperations.SingleAsync(item => item.Id == withdrawn.Id);

        _store.Withdraw(_userId, withdrawn.Id, state.OperationFingerprint).Should().BeTrue();
        _dbContext.ChangeTracker.Clear();

        _store.GetExecution(_userId, withdrawn.Id).Should().BeNull("a withdrawn row never runs");
        _store.Withdraw(_userId, withdrawn.Id, state.OperationFingerprint).Should().BeFalse("the row is already closed");
        (await _store.GetLastConversationPreviewsAsync(_userId, id)).Select(item => item.Proposal.Id).Should().Equal(shown.Id);
    }

    /// <summary>
    /// A turn combines a preview that the person saw and a write of its own into a change set, then withdraws the change
    /// set. The write is withdrawn with it, the released earlier preview stays open on its own, and the conversation lists
    /// the batch that the person saw, never the withdrawn write as a rejected proposal.
    /// </summary>
    [Fact]
    public async Task Withdraw_AChangeSetWithdrawsTheMembersItReservesAndKeepsAReleasedMemberOpen()
    {
        var id = Guid.NewGuid();
        var nowAtUtc = DateTime.UtcNow;
        var shown = ConversationProposal(new(id, nowAtUtc.AddMinutes(-1)), "Reservation");
        var batch = new PendingOperationConversation(id, nowAtUtc);
        var created = ConversationProposal(batch, "Travel");
        var arguments = AccountChangeSet.BuildArguments([
            _store.GetExecution(_userId, shown.Id)!, _store.GetExecution(_userId, created.Id)!]);
        var fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationName, arguments.GetRawText());
        var combined = _store.Create(_userId, _catalogService.GetCapability(AccountChangeSet.CapabilityId)!,
            AccountChangeSet.OperationName, arguments.GetRawText(), "Review changes", fingerprint, AgentExecutionSurface.Chat, batch);
        _store.Revise(_userId, combined.Id, fingerprint, arguments.GetRawText(), fingerprint, "combined-preview").Should().BeTrue();
        _store.Confirm(_userId, shown.Id).Should().BeNull("the change set reserves the earlier preview");

        _store.Withdraw(_userId, combined.Id, fingerprint, [shown.Id]).Should().BeTrue();
        _dbContext.ChangeTracker.Clear();

        _store.GetExecution(_userId, created.Id).Should().BeNull("the change set withdraws the write that it reserves");
        _store.IsAwaitingConfirmation(_userId, shown.Id).Should().BeTrue("a released member stays open");
        (await _store.GetLastConversationPreviewsAsync(_userId, id)).Select(item => item.Proposal.Id).Should().Equal(shown.Id);
        _store.Confirm(_userId, shown.Id).Should().NotBeNull("no change set holds the released member");
    }

    [Fact]
    public void Creation_DeduplicatesOnlyWithinTheSameConversationTurn()
    {
        var firstBatch = new PendingOperationConversation(Guid.NewGuid(), DateTime.UtcNow);
        var first = ConversationProposal(firstBatch, "Reservation", bind: false);
        ConversationProposal(firstBatch, "Reservation", bind: false).Id.Should().Be(first.Id);
        ConversationProposal(new(Guid.NewGuid(), firstBatch.StartedAtUtc), "Reservation", bind: false).Id.Should().NotBe(first.Id);
        ConversationProposal(new(firstBatch.Id, firstBatch.StartedAtUtc.AddSeconds(1)), "Reservation", bind: false).Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task LastConversationBatch_ReturnsShownChangeSetWithMemberArguments()
    {
        var batch = new PendingOperationConversation(Guid.NewGuid(), DateTime.UtcNow);
        var first = ConversationProposal(batch, "Reservation");
        var second = ConversationProposal(batch, "Travel");
        var arguments = AccountChangeSet.BuildArguments([
            _store.GetExecution(_userId, first.Id)!, _store.GetExecution(_userId, second.Id)!]);
        var fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationName, arguments.GetRawText());
        var combined = _store.Create(_userId, _catalogService.GetCapability(AccountChangeSet.CapabilityId)!,
            AccountChangeSet.OperationName, arguments.GetRawText(), "Review changes", fingerprint, AgentExecutionSurface.Chat, batch);
        _store.Revise(_userId, combined.Id, fingerprint, arguments.GetRawText(), fingerprint, "combined-preview").Should().BeTrue();

        var shown = (await _store.GetLastConversationPreviewsAsync(_userId, batch.Id)).Should().ContainSingle().Which;
        shown.Proposal.Id.Should().Be(combined.Id);
        shown.CurrentArguments.GetRawText().Should().ContainAll("Reservation", "Travel", "2026-12-31", "20:30");
    }
}
