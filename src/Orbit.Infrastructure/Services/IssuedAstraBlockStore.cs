using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class IssuedAstraBlockStore(OrbitDbContext dbContext) : IIssuedAstraBlockStore
{
    public async Task AddAsync(IssuedAstraBlock block, CancellationToken cancellationToken)
    {
        dbContext.IssuedAstraBlocks.Add(block);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<IssuedAstraBlock?> GetAsync(Guid blockId, Guid userId, CancellationToken cancellationToken) =>
        dbContext.IssuedAstraBlocks.AsNoTracking()
            .FirstOrDefaultAsync(block => block.Id == blockId && block.UserId == userId, cancellationToken);

    public async Task<bool> MarkAnsweredAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, CancellationToken cancellationToken) =>
        await dbContext.IssuedAstraBlocks
            .Where(block => block.Id == blockId && block.UserId == userId
                && block.AnsweredAtUtc == null && block.InvalidatedAtUtc == null && block.ExpiresAtUtc > answeredAtUtc)
            .ExecuteUpdateAsync(setters => setters.SetProperty(block => block.AnsweredAtUtc, answeredAtUtc), cancellationToken) == 1;

    public Task InvalidateOpenAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.IssuedAstraBlocks
            .Where(block => block.UserId == userId && block.AnsweredAtUtc == null && block.InvalidatedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(block => block.InvalidatedAtUtc,
                TimeProvider.System.GetUtcNow().UtcDateTime), cancellationToken);
}
