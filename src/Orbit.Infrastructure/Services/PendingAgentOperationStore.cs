using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Application.Chat;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class PendingAgentOperationStore(
    OrbitDbContext dbContext,
    IOptions<AgentPlatformSettings> settings) : IPendingAgentOperationStore
{
    private readonly AgentPlatformSettings _settings = settings.Value;

    public PendingAgentOperation Create(
        Guid userId,
        AgentCapability capability,
        string operationId,
        string argumentsJson,
        string summary,
        string operationFingerprint,
        AgentExecutionSurface surface)
    {
        PendingOperationActionKeys.GetRequired(operationId, argumentsJson);

        var existing = dbContext.PendingAgentOperations
            .Where(item =>
                item.ArgumentSchemaVersion == PendingAgentOperationState.CurrentArgumentSchemaVersion &&
                item.UserId == userId &&
                item.CapabilityId == capability.Id &&
                item.OperationFingerprint == operationFingerprint &&
                item.ConsumedAtUtc == null &&
                item.ExpiresAtUtc > DateTime.UtcNow)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault();

        if (existing is not null)
            return Map(existing);

        var entity = PendingAgentOperationState.Create(new PendingAgentOperationStateCreateRequest
        {
            UserId = userId,
            Capability = capability,
            OperationId = operationId,
            ArgumentsJson = argumentsJson,
            Summary = summary,
            OperationFingerprint = operationFingerprint,
            Surface = surface,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.PendingOperationTtlMinutes))
        });

        dbContext.PendingAgentOperations.Add(entity);
        dbContext.SaveChanges();

        return Map(entity);
    }

    public PendingAgentOperationConfirmation? Confirm(Guid userId, Guid pendingOperationId)
    {
        var entity = dbContext.PendingAgentOperations
            .FirstOrDefault(item => item.Id == pendingOperationId && item.UserId == userId);

        if (entity is null || entity.ArgumentSchemaVersion != PendingAgentOperationState.CurrentArgumentSchemaVersion
            || entity.IsExpired(DateTime.UtcNow) || entity.ConsumedAtUtc.HasValue)
            return null;

        var confirmationToken = GenerateToken();
        entity.SetConfirmationTokenHash(HashToken(confirmationToken));
        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            return null;
        }

        return new PendingAgentOperationConfirmation(entity.Id, confirmationToken, entity.ExpiresAtUtc);
    }

    public PendingAgentOperationExecution? GetExecution(Guid userId, Guid pendingOperationId)
    {
        var entity = dbContext.PendingAgentOperations
            .AsNoTracking()
            .FirstOrDefault(item => item.Id == pendingOperationId && item.UserId == userId);

        if (entity is null || entity.IsExpired(DateTime.UtcNow) || entity.ConsumedAtUtc.HasValue)
            return null;

        var parsedArguments = ParseArguments(entity.ArgumentsJson);

        return new PendingAgentOperationExecution(
            entity.Id,
            entity.CapabilityId,
            entity.OperationId,
            parsedArguments,
            entity.Surface,
            entity.ConfirmationRequirement,
            entity.PreviewFingerprint,
            entity.ArgumentSchemaVersion);
    }

    public IReadOnlyList<PendingHabitCreation> GetPendingHabitCreations(Guid userId)
    {
        var checkedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var proposals = dbContext.PendingAgentOperations.AsNoTracking()
            .Where(item => item.UserId == userId && item.ConsumedAtUtc == null && item.ConfirmedAtUtc == null
                && item.ExpiresAtUtc > checkedAtUtc && item.CreatedAtUtc > checkedAtUtc.AddMinutes(-10)
                && (item.OperationId == "create_habit" || item.OperationId == "bulk_create_habits" || item.OperationId == "create_sub_habit"))
            .Select(item => item.ArgumentsJson).ToList();
        var names = new List<PendingHabitCreation>();
        foreach (var json in proposals)
        {
            var arguments = ParseArguments(json);
            if (arguments.ValueKind != JsonValueKind.Object)
                continue;
            Guid? parentId = arguments.TryGetProperty("parent_habit_id", out var parent)
                && parent.ValueKind == JsonValueKind.String && Guid.TryParse(parent.GetString(), out var parsedParent)
                    ? parsedParent : null;
            if (arguments.TryGetProperty("habits", out var habits) && habits.ValueKind == JsonValueKind.Array)
            {
                foreach (var habit in habits.EnumerateArray())
                    if (habit.ValueKind == JsonValueKind.Object && habit.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                        names.Add(new PendingHabitCreation(title.GetString()!, parentId));
            }
            else if (arguments.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                names.Add(new PendingHabitCreation(title.GetString()!, parentId));
        }
        return names;
    }

    public bool SupersedeHabitCreation(Guid userId, string title, Guid? parentHabitId)
    {
        var checkedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var pending = dbContext.PendingAgentOperations.Where(item => item.UserId == userId
            && item.ConsumedAtUtc == null && item.ConfirmedAtUtc == null && item.ExpiresAtUtc > checkedAtUtc
            && (item.OperationId == "create_habit" || item.OperationId == "bulk_create_habits" || item.OperationId == "create_sub_habit"))
            .ToList();
        var matching = new List<PendingAgentOperationState>();
        foreach (var item in pending)
        {
            var args = ParseArguments(item.ArgumentsJson);
            if (args.ValueKind != JsonValueKind.Object)
                continue;
            var parent = args.TryGetProperty("parent_habit_id", out var value)
                && Guid.TryParse(value.ToString(), out var id) ? (Guid?)id : null;
            if (parent != parentHabitId)
                continue;
            var titles = args.TryGetProperty("habits", out var habits) && habits.ValueKind == JsonValueKind.Array
                ? habits.EnumerateArray().Where(habit => habit.ValueKind == JsonValueKind.Object)
                    .Select(habit => habit.TryGetProperty("title", out var name) ? name.ToString() : "")
                : args.TryGetProperty("title", out var name) ? [name.ToString()] : Array.Empty<string>();
            if (titles.Any(name => HabitNameMatcher.Normalize(name) == HabitNameMatcher.Normalize(title)))
            {
                if (habits.ValueKind == JsonValueKind.Array && habits.GetArrayLength() > 1)
                    return false;
                matching.Add(item);
            }
        }
        return matching.Count > 0 && matching.All(item => Cancel(userId, item.Id, item.OperationFingerprint));
    }

    public bool IsAwaitingConfirmation(Guid userId, Guid pendingOperationId)
    {
        var checkedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        return dbContext.PendingAgentOperations.AsNoTracking().Any(item => item.Id == pendingOperationId
            && item.UserId == userId && item.ConfirmedAtUtc == null && item.ConsumedAtUtc == null
            && item.ExpiresAtUtc > checkedAtUtc);
    }

    public bool Revise(Guid userId, Guid pendingOperationId, string expectedFingerprint,
        string argumentsJson, string operationFingerprint, string previewFingerprint)
    {
        var entity = dbContext.PendingAgentOperations.FirstOrDefault(item =>
            item.Id == pendingOperationId && item.UserId == userId);
        var checkedAtUtc = DateTime.UtcNow;
        if (entity is null || entity.ArgumentSchemaVersion != PendingAgentOperationState.CurrentArgumentSchemaVersion
            || entity.OperationFingerprint != expectedFingerprint
            || !entity.Revise(argumentsJson, operationFingerprint, previewFingerprint, checkedAtUtc))
            return false;

        try
        {
            dbContext.SaveChanges();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public bool Cancel(Guid userId, Guid pendingOperationId, string expectedFingerprint)
    {
        var entity = dbContext.PendingAgentOperations.FirstOrDefault(item =>
            item.Id == pendingOperationId && item.UserId == userId);
        var cancelledAtUtc = DateTime.UtcNow;
        if (entity is null || entity.OperationFingerprint != expectedFingerprint
            || !entity.Cancel(cancelledAtUtc))
            return false;
        try
        {
            dbContext.SaveChanges();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public PendingAgentOperationExecution? GetConfirmedExecution(Guid userId, string capabilityId,
        string operationFingerprint, string confirmationToken)
    {
        var hash = HashToken(confirmationToken);
        var checkedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var entity = dbContext.PendingAgentOperations.AsNoTracking().FirstOrDefault(item => item.UserId == userId
            && item.CapabilityId == capabilityId && item.OperationFingerprint == operationFingerprint
            && item.ConfirmationTokenHash == hash && item.ConfirmedAtUtc != null
            && item.ConsumedAtUtc == null && item.ExpiresAtUtc > checkedAtUtc);
        return entity is null ? null : new PendingAgentOperationExecution(entity.Id, entity.CapabilityId,
            entity.OperationId, ParseArguments(entity.ArgumentsJson), entity.Surface,
            entity.ConfirmationRequirement, entity.PreviewFingerprint, entity.ArgumentSchemaVersion);
    }

    public bool TryConsumeFreshConfirmation(
        Guid userId,
        string capabilityId,
        string operationFingerprint,
        string confirmationToken,
        bool requireStepUp)
    {
        var tokenHash = HashToken(confirmationToken);
        var entity = dbContext.PendingAgentOperations
            .Where(item =>
                item.UserId == userId &&
                item.CapabilityId == capabilityId &&
                item.OperationFingerprint == operationFingerprint &&
                item.ConfirmationTokenHash == tokenHash)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault();

#pragma warning disable ORBIT0004 // TryConsumeFreshConfirmation passes the current instant to IsUsable to check confirmation and step-up TTLs before consumption. https://github.com/thomasluizon/orbit-tickets/issues/230
        if (entity is null || !entity.IsUsable(capabilityId, operationFingerprint, requireStepUp, DateTime.UtcNow))
#pragma warning restore ORBIT0004
            return false;

        entity.MarkConsumed();
        try
        {
            dbContext.SaveChanges();
            return true;
        }
        catch (DbUpdateConcurrencyException exception) when (
            exception.Entries.Any(entry => ReferenceEquals(entry.Entity, entity)))
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    private static PendingAgentOperation Map(PendingAgentOperationState entity)
    {
        return new PendingAgentOperation(
            entity.Id,
            entity.CapabilityId,
            entity.DisplayName,
            entity.Summary,
            entity.RiskClass,
            entity.ConfirmationRequirement,
            entity.ExpiresAtUtc,
            ActionKey: PendingOperationActionKeys.GetRequired(entity.OperationId, entity.ArgumentsJson));
    }

    private static JsonElement ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return JsonDocument.Parse("{}").RootElement.Clone();

        try
        {
            return JsonDocument.Parse(argumentsJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static string GenerateToken()
    {
        var random = RandomNumberGenerator.GetBytes(24);
        return $"agc_{Convert.ToBase64String(random).Replace("+", "-").Replace("/", "_").TrimEnd('=')}";
    }

    private static string HashToken(string confirmationToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(confirmationToken));
        return Convert.ToHexString(bytes);
    }
}
