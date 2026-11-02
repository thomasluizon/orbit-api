using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class PendingAgentOperationStoreRetryTests
{
    [Theory]
    [InlineData("confirm", false)]
    [InlineData("revise", false)]
    [InlineData("cancel", false)]
    [InlineData("confirm", true)]
    [InlineData("revise", true)]
    [InlineData("cancel", true)]
    public void Transition_TransientCommitFailure_PersistsPendingStateAndMatchingSnapshot(string transition, bool commitApplied)
    {
        var failure = new CommitFailure(commitApplied);
        using var database = new SqliteOrbitDbContextFactory(
            dependencies => new NpgsqlRetryingExecutionStrategy(dependencies, 1, TimeSpan.Zero, null), failure);
        var db = database.Context;
        var user = User.Create("Person", "conversation-retry@example.com").Value;
        db.Users.Add(user);
        db.SaveChanges();
        var flags = Substitute.For<IFeatureFlagService>();
        flags.ResolveForUser(user.Id).Returns([new(FeatureFlagKeys.AstraConversationRecording, true, null)]);
        var store = new PendingAgentOperationStore(db, Options.Create(new AgentPlatformSettings()), flags);
        var conversation = new PendingOperationConversation(Guid.NewGuid(), DateTime.UtcNow, usesServerHistory: true);
        const string arguments = "{\"title\":\"Reservation\",\"due_date\":\"2026-12-31\",\"due_time\":\"20:30\"}";
        var fingerprint = AgentOperationFingerprint.Compute("create_habit", arguments);
        var capability = new AgentCatalogService().GetCapabilityByChatTool("create_habit")! with
        {
            ConfirmationRequirement = AgentConfirmationRequirement.FreshConfirmation
        };
        var proposal = store.Create(user.Id, capability, "create_habit", arguments, "Reservation", fingerprint,
            AgentExecutionSurface.Chat, conversation);
        store.Revise(user.Id, proposal.Id, fingerprint, arguments, fingerprint, "original-preview").Should().BeTrue();
        var state = db.PendingAgentOperations.Single(item => item.Id == proposal.Id);
        if (transition == "revise")
        {
            store.Confirm(user.Id, proposal.Id).Should().NotBeNull();
            state.MarkStepUpSatisfied();
        }
        var turn = ConversationTurn.Create(user.Id, Guid.NewGuid(), "assistant", conversation.StartedAtUtc,
            null, "Proposal", "[]", conversationId: conversation.Id, turnOrder: 2,
            previewOutcomesJson: JsonSerializer.Serialize(new[] { ConversationTurnRecorder.Preview(state) })).Value;
        db.ConversationTurns.Add(turn);
        db.SaveChanges();
        var originalRevision = state.RevisionNumber;
        db.ChangeTracker.Clear();
        failure.Enabled = true;

        PendingAgentOperationConfirmation? confirmation = null;
        var revisedArguments = arguments.Replace("20:30", "21:00", StringComparison.Ordinal);
        var revisedFingerprint = AgentOperationFingerprint.Compute("create_habit", revisedArguments);
        switch (transition)
        {
            case "confirm":
                confirmation = store.Confirm(user.Id, proposal.Id);
                confirmation.Should().NotBeNull();
                break;
            case "revise":
                store.Revise(user.Id, proposal.Id, fingerprint, revisedArguments, revisedFingerprint, "revised-preview")
                    .Should().BeTrue();
                break;
            case "cancel":
                store.Cancel(user.Id, proposal.Id, fingerprint).Should().BeTrue();
                break;
        }

        using var verification = database.CreateContext();
        var persisted = verification.PendingAgentOperations.AsNoTracking().Single(item => item.Id == proposal.Id);
        var snapshot = verification.ConversationTurns.AsNoTracking().Single(item => item.Id == turn.Id);
        var preview = JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(snapshot.PreviewOutcomesJson)!
            .Should().ContainSingle().Which;
        using var assertions = new AssertionScope();
        failure.CommitAttempts.Should().Be(commitApplied ? 1 : 2);
        failure.Failures.Should().Be(1);
        db.Entry(db.PendingAgentOperations.Single(item => item.Id == proposal.Id)).State.Should().Be(EntityState.Unchanged);
        db.ChangeTracker.HasChanges().Should().BeFalse();
        persisted.RevisionNumber.Should().Be(originalRevision + 1);
        persisted.ArgumentsJson.Should().Be(transition == "revise" ? revisedArguments : arguments);
        persisted.OperationFingerprint.Should().Be(transition == "revise" ? revisedFingerprint : fingerprint);
        persisted.PreviewFingerprint.Should().Be(transition == "revise" ? "revised-preview" : "original-preview");
        persisted.StepUpSatisfiedAtUtc.Should().BeNull();
        if (confirmation is not null)
        {
            persisted.ConfirmedAtUtc.Should().NotBeNull();
            persisted.ConfirmationTokenHash.Should().Be(Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(confirmation.ConfirmationToken))));
            store.GetConfirmedExecution(user.Id, capability.Id, fingerprint, confirmation.ConfirmationToken).Should().NotBeNull();
        }
        else
        {
            persisted.ConfirmedAtUtc.Should().BeNull();
            persisted.ConfirmationTokenHash.Should().BeNull();
        }
        if (transition == "cancel") persisted.ConsumedAtUtc.Should().NotBeNull();
        else persisted.ConsumedAtUtc.Should().BeNull();
        preview.PendingOperationId.Should().Be(persisted.Id);
        preview.Outcome.Should().Be(transition switch { "confirm" => "approved", "revise" => "edited", _ => "rejected" });
        preview.Revision.Should().Be(persisted.RevisionNumber);
        preview.Arguments.GetRawText().Should().Be(persisted.ArgumentsJson);
    }

    private sealed class CommitFailure(bool commitApplied) : DbTransactionInterceptor
    {
        internal bool Enabled { get; set; }
        internal int CommitAttempts { get; private set; }
        internal int Failures { get; private set; }

        public override InterceptionResult TransactionCommitting(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result)
        {
            if (Enabled)
            {
                CommitAttempts++;
                if (!commitApplied) FailOnce();
            }
            return result;
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            if (Enabled && commitApplied) FailOnce();
        }

        private void FailOnce()
        {
            if (Failures > 0) return;
            Failures++;
            throw new NpgsqlException("Transient conversation commit failure", new TimeoutException());
        }
    }
}
