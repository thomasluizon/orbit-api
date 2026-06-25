using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;

namespace Orbit.Api.Controllers;

public partial class SyncController
{
    private async Task ProcessMutation(Guid userId, SyncMutation mutation, CancellationToken ct)
    {
        switch (mutation.Entity.ToLowerInvariant())
        {
            case "habit":
                await ApplyEntityMutationAsync(
                    mutation, "habit", "delete", dbContext.Habits,
                    h => h.Id == mutation.Id && h.UserId == userId,
                    h => h.SoftDelete(), ct);
                break;
            case "goal":
                await ApplyEntityMutationAsync(
                    mutation, "goal", "delete", dbContext.Goals,
                    g => g.Id == mutation.Id && g.UserId == userId,
                    g => g.SoftDelete(), ct);
                break;
            case "tag":
                await ApplyEntityMutationAsync(
                    mutation, "tag", "delete", dbContext.Tags,
                    t => t.Id == mutation.Id && t.UserId == userId,
                    t => t.SoftDelete(), ct);
                break;
            case "notification":
                await ApplyEntityMutationAsync(
                    mutation, "notification", "read", dbContext.Notifications,
                    n => n.Id == mutation.Id && n.UserId == userId,
                    n => n.MarkAsRead(), ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown entity type: {mutation.Entity}");
        }
    }

    private async Task ApplyEntityMutationAsync<TEntity>(
        SyncMutation mutation,
        string entityNoun,
        string supportedAction,
        DbSet<TEntity> set,
        Expression<Func<TEntity, bool>> ownedById,
        Action<TEntity> mutate,
        CancellationToken ct) where TEntity : class
    {
        if (mutation.Action.ToLowerInvariant() != supportedAction)
            throw new InvalidOperationException($"Unsupported action: {mutation.Action} for {entityNoun}.");

        if (mutation.Id is null)
            throw new InvalidOperationException($"Id is required for {supportedAction}.");

        var entity = await set.FirstOrDefaultAsync(ownedById, ct);
        if (entity is not null) mutate(entity);
    }
}
