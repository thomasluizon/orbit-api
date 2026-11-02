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
            CreateCheck("habit", await AllOwnedAsync(dbContext.Habits.IgnoreQueryFilters(), userId, CollectGuids(arguments, "habit_id", "habit_ids", "parent_habit_id", "new_parent_id", "parent_id"), cancellationToken)),
            CreateCheck("tag", await AllOwnedAsync(dbContext.Tags, userId, CollectGuids(arguments, "tag_id", "tag_ids"), cancellationToken)),
            CreateCheck("goal", await AllOwnedAsync(dbContext.Goals.IgnoreQueryFilters(), userId, CollectGuids(arguments, "goal_id", "goal_ids"), cancellationToken)),
            CreateCheck("notification", await AllOwnedAsync(dbContext.Notifications, userId, CollectGuids(arguments, "notification_id", "notification_ids"), cancellationToken)),
            CreateCheck("template", await AllOwnedAsync(dbContext.ChecklistTemplates, userId, CollectGuids(arguments, "template_id"), cancellationToken)),
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
        TargetIds targets,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (!targets.Valid)
            return new OwnershipResult(true, false);
        var ids = targets.Values.ToList();
        if (ids.Count == 0)
            return new OwnershipResult(false, true);

        var ownedCount = await queryable.CountAsync(
            entity => EF.Property<Guid>(entity, "UserId") == userId && ids.Contains(EF.Property<Guid>(entity, "Id")),
            cancellationToken);

        return new OwnershipResult(true, ownedCount == ids.Count);
    }

    private static TargetIds CollectGuids(JsonElement arguments, params string[] propertyNames)
    {
        var values = new HashSet<Guid>();
        var valid = true;
        void Add(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id))
                values.Add(id);
            else
                valid = false;
        }
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Walk(item);
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var property in node.EnumerateObject())
            {
                if (propertyNames.Contains(property.Name, StringComparer.Ordinal))
                {
                    if (property.Name.EndsWith("_ids", StringComparison.Ordinal))
                    {
                        if (property.Value.ValueKind != JsonValueKind.Array) valid = false;
                        else foreach (var item in property.Value.EnumerateArray()) Add(item);
                    }
                    else Add(property.Value);
                }
                else if (property.Name is "positions" or "habits" or "sub_habits" or "filter")
                    Walk(property.Value);
            }
        }
        Walk(arguments);
        return new TargetIds(values, valid);
    }

    private sealed record TargetIds(HashSet<Guid> Values, bool Valid);

    private sealed record OwnershipCheck(string ResourceName, bool HasTargets, bool IsOwned);
    private sealed record OwnershipResult(bool HasTargets, bool IsOwned);
}
