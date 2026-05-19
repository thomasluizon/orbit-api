using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class PendingClarificationStore(OrbitDbContext dbContext) : IPendingClarificationStore
{
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
            ExtractQuickActionValues(entity.QuickActionsJson));
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

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(quickActionsJson);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }

        if (node is not JsonArray array)
            return Array.Empty<string>();

        var values = new List<string>(array.Count);
        foreach (var element in array)
        {
            // Records serialize as PascalCase by default; tolerate camelCase too.
            var value = (element?["Value"] ?? element?["value"])?.GetValue<string>();
            if (!string.IsNullOrEmpty(value))
                values.Add(value);
        }
        return values;
    }
}
