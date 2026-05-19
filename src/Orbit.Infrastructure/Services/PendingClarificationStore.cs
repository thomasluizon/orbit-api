using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class PendingClarificationStore(OrbitDbContext dbContext) : IPendingClarificationStore
{
    private static readonly JsonSerializerOptions QuickActionDeserializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
            DateTime.UtcNow.AddMinutes(AppConstants.PendingClarificationTtlMinutes));

        dbContext.PendingClarifications.Add(entity);
        // SaveChangesAsync runs here (eagerly, before the chat command's UoW commits)
        // so the OperationId is durably stored before the response is returned. If the
        // surrounding chat command fails afterwards, the row is orphaned but the
        // 30-minute TTL + index on ExpiresAtUtc reclaim it. Mirrors the eager-save
        // pattern in PendingAgentOperationStore.
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

        return new PendingClarificationData(
            entity.ToolName,
            entity.PartialArgumentsJson,
            entity.MissingArgumentKey,
            ExtractQuickActionValues(entity.QuickActionsJson),
            entity.ExpiresAtUtc);
    }

    public async Task<bool> MarkResolvedAsync(
        Guid operationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Atomic compare-and-set: also requires the row to be unexpired so a client
        // that catches the row right at the TTL boundary can't claim it.
        var rows = await dbContext.PendingClarifications
            .Where(item =>
                item.Id == operationId &&
                item.UserId == userId &&
                item.ResolvedAtUtc == null &&
                item.ExpiresAtUtc > DateTime.UtcNow)
            .ExecuteUpdateAsync(
                setter => setter.SetProperty(item => item.ResolvedAtUtc, DateTime.UtcNow),
                cancellationToken);

        return rows > 0;
    }

    private static IReadOnlyList<string> ExtractQuickActionValues(string quickActionsJson)
    {
        if (string.IsNullOrWhiteSpace(quickActionsJson))
            return Array.Empty<string>();

        try
        {
            // Case-insensitive deserialize decouples this from whichever casing the
            // serializer used when storing — defaults to PascalCase for records, but
            // a global camelCase policy would silently break a manual JsonNode lookup.
            var actions = JsonSerializer.Deserialize<List<QuickAction>>(quickActionsJson, QuickActionDeserializerOptions);
            if (actions is null) return Array.Empty<string>();

            var values = new List<string>(actions.Count);
            foreach (var action in actions)
            {
                if (!string.IsNullOrEmpty(action?.Value))
                    values.Add(action.Value);
            }
            return values;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
