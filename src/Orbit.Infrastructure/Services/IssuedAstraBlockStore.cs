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
            .Where(block => block.UserId == userId && block.ResponseJson == null && block.InvalidatedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(block => block.InvalidatedAtUtc,
                TimeProvider.System.GetUtcNow().UtcDateTime), cancellationToken);

    public async Task RecordAnswerAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, string answerJson,
        DateOnly? reservedAllowanceDate, string? responseJson, CancellationToken cancellationToken)
    {
        var changed = await UnfinishedAnswer(blockId, userId, answeredAtUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(block => block.AnswerJson, answerJson)
                .SetProperty(block => block.ReservedAllowanceDate, reservedAllowanceDate)
                .SetProperty(block => block.ResponseJson, responseJson), cancellationToken);
        if (changed != 1)
            throw new InvalidOperationException("Block answer reservation was lost.");
    }

    public async Task<bool> CompleteAnswerAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, string responseJson,
        CancellationToken cancellationToken) =>
        await UnfinishedAnswer(blockId, userId, answeredAtUtc)
            .ExecuteUpdateAsync(setters => setters.SetProperty(block => block.ResponseJson, responseJson), cancellationToken) == 1;

    public async Task<bool> CheckpointAnswerAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, string responseJson,
        CancellationToken cancellationToken) =>
        await UnfinishedAnswer(blockId, userId, answeredAtUtc)
            .ExecuteUpdateAsync(setters => setters.SetProperty(block => block.CommittedResponseJson, responseJson), cancellationToken) == 1;

    public async Task<bool> ReleaseAnswerAsync(Guid blockId, Guid userId, DateTime answeredAtUtc, CancellationToken cancellationToken) =>
        await UnfinishedAnswer(blockId, userId, answeredAtUtc)
            .Where(block => block.CommittedResponseJson == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(block => block.AnsweredAtUtc, (DateTime?)null)
                .SetProperty(block => block.AnswerJson, (string?)null)
                .SetProperty(block => block.ReservedAllowanceDate, (DateOnly?)null), cancellationToken) == 1;

    private IQueryable<IssuedAstraBlock> UnfinishedAnswer(Guid blockId, Guid userId, DateTime answeredAtUtc) =>
        dbContext.IssuedAstraBlocks.Where(block => block.Id == blockId && block.UserId == userId
            && block.AnsweredAtUtc == answeredAtUtc && block.ResponseJson == null);
}
