using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class ConversationPlanStore(OrbitDbContext db, IUnitOfWork work, IFeatureFlagService flags) : IConversationPlanStore
{
    public async Task<AiPlan?> ChangeAsync(Guid userId, Guid pendingOperationId, string status, CancellationToken cancellationToken = default)
    {
        if (!(await flags.GetEnabledKeysForUserAsync(userId, cancellationToken, forceRefresh: true)).Contains(FeatureFlagKeys.AiConversationRecording))
            return null;
        return await work.ExecuteInTransactionAsync(async ct =>
        {
            await work.AcquireUserRowLockAsync(userId, ct);
            if (!(await flags.GetEnabledKeysForUserAsync(userId, ct, forceRefresh: true)).Contains(FeatureFlagKeys.AiConversationRecording))
                return null;
            var pending = await db.PendingAgentOperations.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId && item.Id == pendingOperationId, ct);
            if (pending?.ConversationId is null) return null;
            foreach (var entry in db.ChangeTracker.Entries<ConversationTurn>().Where(entry => entry.State != EntityState.Added
                && entry.Entity.UserId == userId && entry.Entity.ConversationId == pending.ConversationId).ToArray())
                entry.State = EntityState.Detached;
            var rows = await db.ConversationTurns.Where(row => row.UserId == userId && row.ConversationId == pending.ConversationId
                && row.Role == "assistant" && row.CreatedAtUtc >= pending.CreatedAtUtc).ToListAsync(ct);
            AiPlan? updated = null;
            foreach (var row in rows)
            {
                var plan = AiPlan.Read(row.ResponseJson);
                if (plan is null) continue;
                var steps = plan.Steps.Where(step => step.PendingOperationId == pendingOperationId).ToArray();
                if (steps.Length == 0) continue;
                foreach (var step in steps.Where(step => step.Status is "pending" or "running")) plan = plan.Change(step.Id, status);
                row.UpdatePlan(plan);
                updated = plan;
            }
            if (updated is not null) await work.SaveChangesAsync(ct);
            return updated;
        }, cancellationToken);
    }
}
