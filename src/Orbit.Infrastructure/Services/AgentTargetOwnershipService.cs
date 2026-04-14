using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class AgentTargetOwnershipService(OrbitDbContext dbContext) : IAgentTargetOwnershipService
{
    public async Task<string?> GetDenialReasonAsync(
        string operationId,
        Guid userId,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var ownershipChecks = new[]
        {
            CreateCheck("habit", await AllOwnedAsync(dbContext.Habits.IgnoreQueryFilters(), userId, CollectGuids(arguments, "habit_id", "habit_ids", "parent_habit_id", "new_parent_id"), cancellationToken)),
            CreateCheck("goal", await AllOwnedAsync(dbContext.Goals.IgnoreQueryFilters(), userId, CollectGuids(arguments, "goal_id", "goal_ids"), cancellationToken)),
            CreateCheck("notification", await AllOwnedAsync(dbContext.Notifications, userId, CollectGuids(arguments, "notification_id"), cancellationToken)),
            CreateCheck("template", await AllOwnedAsync(dbContext.ChecklistTemplates, userId, CollectGuids(arguments, "template_id"), cancellationToken)),
            CreateCheck("user_fact", await AllOwnedAsync(dbContext.UserFacts.IgnoreQueryFilters(), userId, CollectGuids(arguments, "fact_id", "fact_ids"), cancellationToken)),
            CreateCheck("api_key", await AllOwnedAsync(dbContext.ApiKeys, userId, CollectGuids(arguments, "key_id"), cancellationToken)),
            CreateCheck("calendar_suggestion", await AllOwnedAsync(dbContext.GoogleCalendarSyncSuggestions, userId, CollectGuids(arguments, "suggestion_id"), cancellationToken))
        };

        var failedCheck = ownershipChecks.FirstOrDefault(check => check is { HasTargets: true, IsOwned: false });
        return failedCheck is null
            ? null
            : $"target_not_owned:{operationId}:{failedCheck.ResourceName}";
    }

    private static OwnershipCheck CreateCheck(string resourceName, OwnershipResult result)
        => new(resourceName, result.HasTargets, result.IsOwned);

    private static async Task<OwnershipResult> AllOwnedAsync<TEntity>(
        IQueryable<TEntity> queryable,
        Guid userId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (ids.Count == 0)
            return new OwnershipResult(false, true);

        var ownedCount = await queryable.CountAsync(
            entity => EF.Property<Guid>(entity, "UserId") == userId && ids.Contains(EF.Property<Guid>(entity, "Id")),
            cancellationToken);

        return new OwnershipResult(true, ownedCount == ids.Count);
    }

    private static IReadOnlyCollection<Guid> CollectGuids(JsonElement arguments, params string[] propertyNames)
    {
        var values = new HashSet<Guid>();

        foreach (var propertyName in propertyNames)
        {
            if (!arguments.TryGetProperty(propertyName, out var propertyValue))
                continue;

            if (propertyValue.ValueKind == JsonValueKind.String &&
                Guid.TryParse(propertyValue.GetString(), out var parsedSingle))
            {
                values.Add(parsedSingle);
                continue;
            }

            if (propertyValue.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in propertyValue.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(item.GetString(), out var parsedArrayItem))
                {
                    values.Add(parsedArrayItem);
                }
            }
        }

        return values.ToList();
    }

    private sealed record OwnershipCheck(string ResourceName, bool HasTargets, bool IsOwned);
    private sealed record OwnershipResult(bool HasTargets, bool IsOwned);
}
