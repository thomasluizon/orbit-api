using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed partial class ConversationTurnRecorder
{
    private async Task ReconcilePlansAsync(Guid userId, IReadOnlyList<ConversationTurn> page, CancellationToken cancellationToken)
    {
        var ids = page.Where(row => AiPlan.Read(row.ResponseJson)?.Steps.Any(step =>
            step.PendingOperationId.HasValue && step.Status is "pending" or "running") == true).Select(row => row.Id).ToArray();
        if (ids.Length == 0) return;
        try
        {
            if (!(await flags.GetEnabledKeysForUserAsync(userId, cancellationToken, forceRefresh: true))
                .Contains(FeatureFlagKeys.AiConversationRecording)) return;
            var updates = await work.ExecuteInTransactionAsync(async ct =>
            {
                var repaired = new Dictionary<Guid, AiPlan>();
                await work.AcquireUserRowLockAsync(userId, ct);
                if (!(await flags.GetEnabledKeysForUserAsync(userId, ct, forceRefresh: true)).Contains(FeatureFlagKeys.AiConversationRecording)) return repaired;
                foreach (var entry in db.ChangeTracker.Entries<ConversationTurn>().Where(entry => ids.Contains(entry.Entity.Id)
                    && entry.State != EntityState.Added).ToArray()) entry.State = EntityState.Detached;
                var rows = await db.ConversationTurns.Where(row => row.UserId == userId && ids.Contains(row.Id)).ToListAsync(ct);
                var plans = rows.ToDictionary(row => row.Id, row => AiPlan.Read(row.ResponseJson));
                var pendingIds = plans.Values.OfType<AiPlan>().SelectMany(plan => plan.Steps)
                    .Where(step => step.PendingOperationId.HasValue).Select(step => step.PendingOperationId!.Value).Distinct().ToArray();
                var pending = await db.PendingAgentOperations.AsNoTracking().Where(item => item.UserId == userId && pendingIds.Contains(item.Id))
                    .ToDictionaryAsync(item => item.Id, ct);
                var targets = pendingIds.Select(id => id.ToString()).ToArray();
                var outcomes = (await db.AgentAuditLogs.AsNoTracking().Where(entry => entry.UserId == userId
                    && entry.SourceName == ConversationPlanUpdates.ExecutionAuditSource && targets.Contains(entry.TargetId!))
                    .OrderByDescending(entry => entry.CreatedAtUtc).ToListAsync(ct))
                    .GroupBy(entry => entry.TargetId!).ToDictionary(group => group.Key, group => group.First());
                foreach (var row in rows)
                {
                    if (plans[row.Id] is not { } plan) continue;
                    var changed = false;
                    foreach (var step in plan.Steps.Where(step => step.PendingOperationId.HasValue && step.Status is "pending" or "running"))
                    {
                        if (!pending.TryGetValue(step.PendingOperationId!.Value, out var state)) continue;
                        outcomes.TryGetValue(state.Id.ToString(), out var outcome);
                        var status = Status(state, outcome);
                        if (status is null || status == step.Status) continue;
                        plan = plan.Change(step.Id, status);
                        changed = true;
                    }
                    if (!changed) continue;
                    row.UpdatePlan(plan);
                    repaired.Add(row.Id, plan);
                }
                await work.SaveChangesAsync(ct);
                return repaired;
            }, cancellationToken);
            foreach (var row in page)
                if (updates.TryGetValue(row.Id, out var plan)) row.UpdatePlan(plan);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to reconcile conversation plans for user {UserId} and turns {TurnIds}",
                userId, string.Join(", ", page.Where(row => ids.Contains(row.Id)).Select(row => row.TurnId).Distinct()));
        }
    }

    private static string? Status(PendingAgentOperationState pending, AgentAuditLog? outcome)
    {
        if (pending.ConsumedAtUtc.HasValue && pending.ConfirmationTokenHash is null) return "skipped";
        if (pending.ConfirmedAtUtc.HasValue && outcome is not null && outcome.CreatedAtUtc >= pending.ConfirmedAtUtc)
        {
            if (outcome.OutcomeStatus == AgentOperationStatus.Failed) return "failed";
            if (outcome.OutcomeStatus == AgentOperationStatus.Succeeded && pending.ConsumedAtUtc.HasValue) return "done";
        }
        if (!pending.ConsumedAtUtc.HasValue && pending.IsExpired(TimeProvider.System.GetUtcNow().UtcDateTime)) return "skipped";
        return pending.ConfirmedAtUtc.HasValue ? "running" : null;
    }
}
