using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class PendingClarificationStore(OrbitDbContext dbContext) : IPendingClarificationStore
{
    private const int TtlMinutes = 30;

    public async Task<Guid> CreateAsync(
        Guid userId,
        string toolName,
        string partialArgumentsJson,
        string missingArgumentKey,
        string question,
        string quickActionsJson,
        CancellationToken cancellationToken = default)
    {
        var entity = PendingClarification.Create(
            userId,
            toolName,
            partialArgumentsJson,
            missingArgumentKey,
            question,
            quickActionsJson,
            DateTime.UtcNow.AddMinutes(TtlMinutes));

        dbContext.PendingClarifications.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    public async Task<PendingClarificationData?> GetForResolutionAsync(
        Guid operationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.PendingClarifications
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == operationId && item.UserId == userId, cancellationToken);

        if (entity is null || entity.IsExpired(DateTime.UtcNow) || entity.IsResolved)
            return null;

        return new PendingClarificationData(entity.ToolName, entity.PartialArgumentsJson, entity.MissingArgumentKey);
    }

    public async Task MarkResolvedAsync(
        Guid operationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.PendingClarifications
            .FirstOrDefaultAsync(item => item.Id == operationId && item.UserId == userId, cancellationToken);

        if (entity is null)
            return;

        entity.MarkResolved();
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
