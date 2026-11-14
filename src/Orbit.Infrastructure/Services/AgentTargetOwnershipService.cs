using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
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
        var targets = AgentTargets.Collect(arguments);
        var ownershipChecks = new List<OwnershipCheck>(AgentTargets.Resources.Count);
        foreach (var resource in AgentTargets.Resources)
            ownershipChecks.Add(CreateCheck(resource.Name,
                await AllOwnedAsync(resource, userId, TargetIdsOf(targets, resource, operationId), cancellationToken)));

        var failedCheck = ownershipChecks.FirstOrDefault(check => check is { HasTargets: true, IsOwned: false });
        return failedCheck is null
            ? null
            : $"target_not_owned:{operationId}:{failedCheck.ResourceName}";
    }

    private static OwnershipCheck CreateCheck(string resourceName, OwnershipResult result)
        => new(resourceName, result.HasTargets, result.IsOwned);

    /// <summary>
    /// The table of each resource in <see cref="AgentTargets.Resources"/>. A resource with no table here fails every
    /// check, so a new resource in the catalog cannot pass without its own ownership check.
    /// </summary>
    private Task<OwnershipResult> AllOwnedAsync(AgentTargetResource resource, Guid userId, TargetIds targets,
        CancellationToken cancellationToken) =>
        resource == AgentTargets.Habit ? AllOwnedAsync(dbContext.Habits.IgnoreQueryFilters(), userId, targets, cancellationToken)
        : resource == AgentTargets.Tag ? AllOwnedAsync(dbContext.Tags, userId, targets, cancellationToken)
        : resource == AgentTargets.Goal ? AllOwnedAsync(dbContext.Goals.IgnoreQueryFilters(), userId, targets, cancellationToken)
        : resource == AgentTargets.Notification ? AllOwnedAsync(dbContext.Notifications, userId, targets, cancellationToken)
        : resource == AgentTargets.Template ? AllOwnedAsync(dbContext.ChecklistTemplates, userId, targets, cancellationToken)
        : resource == AgentTargets.ApiKey ? AllOwnedAsync(dbContext.ApiKeys, userId, targets, cancellationToken)
        : resource == AgentTargets.CalendarSuggestion ? AllOwnedAsync(dbContext.GoogleCalendarSyncSuggestions, userId, targets, cancellationToken)
        : throw new InvalidOperationException($"The {resource.Name} target has no ownership check.");

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

    private static TargetIds TargetIdsOf(IEnumerable<AgentTargetField> targets, AgentTargetResource resource, string operationId)
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
        foreach (var target in targets.Where(target => target.Resource == resource))
        {
            if (target.Name.EndsWith("_ids", StringComparison.Ordinal))
            {
                if (target.Value.ValueKind != JsonValueKind.Array) valid = false;
                else foreach (var item in target.Value.EnumerateArray()) Add(item);
            }
            else if (!(target.AtRoot && target.Value.ValueKind == JsonValueKind.Null
                && (operationId == "move_habit_parent" && target.Name == "parent_id"
                    || operationId == "move_habit" && target.Name == "new_parent_id")))
                Add(target.Value);
        }
        return new TargetIds(values, valid);
    }

    private sealed record TargetIds(HashSet<Guid> Values, bool Valid);

    private sealed record OwnershipCheck(string ResourceName, bool HasTargets, bool IsOwned);
    private sealed record OwnershipResult(bool HasTargets, bool IsOwned);
}
