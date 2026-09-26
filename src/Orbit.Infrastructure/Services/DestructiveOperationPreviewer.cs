using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class DestructiveOperationPreviewer(OrbitDbContext dbContext) : IDestructiveOperationPreviewer
{
    public Task<PendingOperationChangePreview?> PreviewAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken cancellationToken = default) =>
        arguments.ValueKind != JsonValueKind.Object
            ? Task.FromResult<PendingOperationChangePreview?>(null)
            : operationId switch
    {
        "delete_goal" => LoadAsync(dbContext.Goals, userId, operationId,
            ReadIds(arguments, "goal_id"), goal => goal.Title,
            goal => new { goal.Title, goal.Description, goal.UpdatedAtUtc, goal.IsDeleted }, cancellationToken),
        "delete_tag" => LoadAsync(dbContext.Tags, userId, operationId,
            ReadIds(arguments, "tag_id"), tag => tag.Name,
            tag => new { tag.Name, tag.Color, tag.UpdatedAtUtc, tag.IsDeleted }, cancellationToken),
        "delete_checklist_template" => LoadAsync(dbContext.ChecklistTemplates, userId, operationId,
            ReadIds(arguments, "template_id"), template => template.Name,
            template => new { template.Name, template.UpdatedAtUtc, template.IsDeleted }, cancellationToken),
        "delete_user_facts" => LoadFactsAsync(userId, operationId, arguments, cancellationToken),
        "delete_notifications" => LoadNotificationsAsync(userId, operationId, arguments, cancellationToken),
        "manage_calendar_sync" => LoadCalendarAsync(userId, operationId, arguments, cancellationToken),
        _ => Task.FromResult<PendingOperationChangePreview?>(null)
    };

    private Task<PendingOperationChangePreview?> LoadFactsAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        var ids = ReadIds(arguments, "fact_ids", "fact_id");
        return LoadAsync(dbContext.UserFacts, userId, operationId, ids,
            fact => fact.FactText, fact => new
            {
                fact.FactText, fact.Category, fact.UpdatedAtUtc, fact.IsDeleted
            }, cancellationToken);
    }

    private Task<PendingOperationChangePreview?> LoadNotificationsAsync(Guid userId,
        string operationId, JsonElement arguments, CancellationToken cancellationToken)
    {
        var action = ReadString(arguments, "action");
        var ids = action switch
        {
            "delete_one" => ReadIds(arguments, "notification_id"),
            "delete_selected" => ReadIds(arguments, "notification_ids"),
            "delete_all" => null,
            _ => []
        };
        return LoadAsync(dbContext.Notifications, userId, operationId, ids,
            notification => notification.Title, notification => new
            {
                notification.Title, notification.Body, notification.IsRead,
                notification.UpdatedAtUtc, notification.IsDeleted
            }, cancellationToken);
    }

    private Task<PendingOperationChangePreview?> LoadCalendarAsync(Guid userId,
        string operationId, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (ReadString(arguments, "action") == "dismiss_suggestion")
            return LoadAsync(dbContext.GoogleCalendarSyncSuggestions, userId, operationId,
                ReadIds(arguments, "suggestion_id"), suggestion => suggestion.Title,
                suggestion => new
                {
                    suggestion.Title, suggestion.StartDateUtc, suggestion.DismissedAtUtc,
                    suggestion.ImportedAtUtc
                }, cancellationToken);
        return LoadCalendarUserAsync(userId, operationId, arguments, cancellationToken);
    }

    private async Task<PendingOperationChangePreview?> LoadCalendarUserAsync(Guid userId,
        string operationId, JsonElement arguments, CancellationToken cancellationToken)
    {
        var action = ReadString(arguments, "action");
        if (action is not ("set_auto_sync" or "dismiss_import" or "run_sync"))
            return null;
        var user = await dbContext.Users.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
            return null;
        var field = action == "set_auto_sync" ? "enabled" : action;
        var oldValue = action switch
        {
            "set_auto_sync" => user.GoogleCalendarAutoSyncEnabled.ToString(),
            "dismiss_import" => user.HasImportedCalendar.ToString(),
            _ => user.GoogleCalendarLastSyncedAt?.ToString("O")
        };
        var newValue = action == "set_auto_sync" && arguments.TryGetProperty("enabled", out var enabled)
            ? enabled.ToString() : null;
        var change = new PendingOperationChange(userId, "Calendar sync", field,
            oldValue, newValue, action == "set_auto_sync" ? "boolean" : "action");
        var item = new PendingOperationItem(userId.ToString(), userId, "Calendar sync", [change],
            AgentOperationFingerprint.Compute(operationId, JsonSerializer.Serialize(new
            {
                user.GoogleCalendarAutoSyncEnabled, user.HasImportedCalendar,
                user.GoogleCalendarLastSyncedAt, user.GoogleCalendarAutoSyncStatus
            })));
        var fingerprint = AgentOperationFingerprint.Compute(operationId, JsonSerializer.Serialize(item));
        return new PendingOperationChangePreview([change], 1, [item], fingerprint);
    }

    private static async Task<PendingOperationChangePreview?> LoadAsync<TEntity>(
        IQueryable<TEntity> source, Guid userId, string operationId, IReadOnlyList<Guid>? ids,
        Func<TEntity, string> name, Func<TEntity, object> state,
        CancellationToken cancellationToken) where TEntity : Entity
    {
        if (ids is { Count: 0 })
            return null;
        var query = source.AsNoTracking().Where(item => EF.Property<Guid>(item, "UserId") == userId);
        if (ids is not null)
            query = query.Where(item => ids.Contains(item.Id));
        var entities = await query.OrderBy(item => item.Id).ToListAsync(cancellationToken);
        var items = entities.Select(entity =>
        {
            var displayName = name(entity);
            return new PendingOperationItem(entity.Id.ToString(), entity.Id, displayName,
                [new PendingOperationChange(entity.Id, displayName, "delete", null, null, "action")],
                AgentOperationFingerprint.Compute(entity.Id.ToString(), JsonSerializer.Serialize(state(entity))));
        }).ToList();
        var fingerprint = AgentOperationFingerprint.Compute(operationId, JsonSerializer.Serialize(items));
        return new PendingOperationChangePreview(items.Take(10).SelectMany(item => item.Fields).ToList(),
            items.Count, items, fingerprint);
    }

    private static IReadOnlyList<Guid>? ReadIds(JsonElement arguments, params string[] keys)
    {
        var ids = new List<Guid>();
        foreach (var key in keys)
        {
            if (!arguments.TryGetProperty(key, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id))
                ids.Add(id);
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(item.GetString(), out var listedId))
                        return [];
                    ids.Add(listedId);
                }
            }
            else
                return [];
        }
        return ids.Distinct().ToList();
    }

    private static string? ReadString(JsonElement arguments, string key) =>
        arguments.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
