using System.Linq.Expressions;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Events;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Sync.Commands;

public record SyncMutation(string Entity, string Action, Guid? Id, Dictionary<string, object?>? Data);
public record SyncMutationResult(int Index, string Status, string? Error = null);
public record SyncBatchResult(int Processed, int Failed, IReadOnlyList<SyncMutationResult> Results);
public record ProcessSyncBatchCommand(Guid UserId, IReadOnlyList<SyncMutation> Mutations) : IRequest<Result<SyncBatchResult>>;

public class ProcessSyncBatchCommandHandler(
    IGenericRepository<Habit> habits,
    IGenericRepository<Goal> goals,
    IGenericRepository<Tag> tags,
    IGenericRepository<Notification> notifications,
    IUnitOfWork unitOfWork,
    IAccountEventCollector eventCollector,
    ILogger<ProcessSyncBatchCommandHandler> logger) : IRequestHandler<ProcessSyncBatchCommand, Result<SyncBatchResult>>
{
    public async Task<Result<SyncBatchResult>> Handle(ProcessSyncBatchCommand request, CancellationToken cancellationToken)
    {
        if (request.Mutations.Count == 0) return Result.Failure<SyncBatchResult>(ErrorMessages.NoMutations);
        if (request.Mutations.Count > AppConstants.MaxBulkOperationSize)
            return Result.Failure<SyncBatchResult>(ErrorMessages.TooManyMutations.Format(AppConstants.MaxBulkOperationSize));

        var results = new List<SyncMutationResult>();
        for (var index = 0; index < request.Mutations.Count; index++)
        {
            var mutation = request.Mutations[index];
            try
            {
                await ConcurrencyRetry.RunAsync(unitOfWork, async ct =>
                {
                    await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
                    {
                        try
                        {
                            await ProcessMutation(request.UserId, mutation, transactionToken);
                            await unitOfWork.SaveChangesAsync(transactionToken);
                        }
                        catch
                        {
                            unitOfWork.ResetTracking();
                            eventCollector.Clear();
                            throw;
                        }
                    }, ct);
                    return true;
                }, cancellationToken);
                results.Add(new SyncMutationResult(index, "success"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                unitOfWork.ResetTracking();
                eventCollector.Clear();
                logger.LogWarning(exception, "Sync mutation {Index} failed for entity {Entity} action {Action}",
                    index, mutation.Entity, mutation.Action);
                results.Add(new SyncMutationResult(index, "failed", ErrorMessages.MutationFailed.Message));
            }
        }
        var processed = results.Count(result => result.Status == "success");
        return Result.Success(new SyncBatchResult(processed, results.Count - processed, results));
    }

    private async Task ProcessMutation(Guid userId, SyncMutation mutation, CancellationToken ct)
    {
        switch (mutation.Entity.ToLowerInvariant())
        {
            case "habit":
                await unitOfWork.AcquireAdvisoryLockAsync(HabitCeilingLock.ForUser(userId), ct);
                await Apply(mutation, "delete", habits, h => h.Id == mutation.Id && h.UserId == userId, h => h.SoftDelete(), ct);
                break;
            case "goal":
                await Apply(mutation, "delete", goals, g => g.Id == mutation.Id && g.UserId == userId, g => g.SoftDelete(), ct);
                break;
            case "tag":
                await Apply(mutation, "delete", tags, t => t.Id == mutation.Id && t.UserId == userId, t => t.SoftDelete(), ct);
                break;
            case "notification":
                await Apply(mutation, "read", notifications, n => n.Id == mutation.Id && n.UserId == userId, n => n.MarkAsRead(), ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown entity type: {mutation.Entity}");
        }
    }

    private static async Task Apply<T>(SyncMutation mutation, string supportedAction, IGenericRepository<T> repository,
        Expression<Func<T, bool>> ownedById, Action<T> mutate, CancellationToken ct) where T : Entity
    {
        if (!string.Equals(mutation.Action, supportedAction, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported action: {mutation.Action}.");
        if (mutation.Id is null) throw new InvalidOperationException($"Id is required for {supportedAction}.");
        var entity = await repository.FindOneTrackedAsync(ownedById, cancellationToken: ct);
        if (entity is not null) mutate(entity);
    }
}
