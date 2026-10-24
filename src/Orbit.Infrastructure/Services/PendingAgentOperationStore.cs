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
            || entity.IsExpired(DateTime.UtcNow) || entity.ConsumedAtUtc.HasValue || HasChangeSetOwner(userId, entity.Id))
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
            entity.ArgumentSchemaVersion, entity.ExpiresAtUtc, PendingOperationActionKeys.GetRequired(entity.OperationId, entity.ArgumentsJson));
    }

    public IReadOnlyList<PendingAgentOperationExecution> GetAwaiting(Guid userId)
    {
        var checkedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var pending = dbContext.PendingAgentOperations.AsNoTracking()
            .Where(item => item.UserId == userId && item.ConsumedAtUtc == null && item.ConfirmedAtUtc == null
                && item.ExpiresAtUtc > checkedAtUtc && item.ArgumentSchemaVersion == PendingAgentOperationState.CurrentArgumentSchemaVersion)
            .OrderByDescending(item => item.CreatedAtUtc).ToList();
        var members = pending.Where(item => item.OperationId == Orbit.Application.Chat.AccountChangeSet.OperationName)
            .SelectMany(item => Orbit.Application.Chat.AccountChangeSet.Reservations(ParseArguments(item.ArgumentsJson)))
            .Select(member => member.PendingOperationId).ToHashSet();
        return pending.Where(item => !members.Contains(item.Id)).Select(item => new PendingAgentOperationExecution(
            item.Id, item.CapabilityId, item.OperationId, ParseArguments(item.ArgumentsJson), item.Surface,
            item.ConfirmationRequirement, item.PreviewFingerprint, item.ArgumentSchemaVersion, item.ExpiresAtUtc, PendingOperationActionKeys.GetRequired(item.OperationId, item.ArgumentsJson))).ToArray();
    }

    public IReadOnlyList<PendingHabitCreation> GetPendingHabitCreations(Guid userId)
    {
        var checkedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var proposals = dbContext.PendingAgentOperations.AsNoTracking()
            .Where(item => item.UserId == userId && item.ConsumedAtUtc == null && item.ConfirmedAtUtc == null
                && item.ExpiresAtUtc > checkedAtUtc
                && (item.OperationId == "create_habit" || item.OperationId == "bulk_create_habits" || item.OperationId == "create_sub_habit"))
            .ToList();
        var names = new List<PendingHabitCreation>();
        foreach (var proposal in proposals)
            names.AddRange(HabitCreations(ParseArguments(proposal.ArgumentsJson))
                .Select(item => item with
                {
                    PendingOperationId = proposal.Id,
                    ExpiresAtUtc = proposal.ExpiresAtUtc,
                    ActionKey = PendingOperationActionKeys.GetRequired(proposal.OperationId, proposal.ArgumentsJson)
                }));
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
            if (HabitCreations(args).Any(item => item.ParentHabitId == parentHabitId
                && HabitNameMatcher.Normalize(item.Title) == HabitNameMatcher.Normalize(title)))
            {
                if (HabitCreations(args).Count > 1)
                    return false;
                matching.Add(item);
            }
        }
        return matching.Count > 0 && matching.All(item => Cancel(userId, item.Id, item.OperationFingerprint));
    }

    private static IReadOnlyList<PendingHabitCreation> HabitCreations(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return [];
        var sharedParent = ReadParent(arguments);
        var items = arguments.TryGetProperty("habits", out var habits) && habits.ValueKind == JsonValueKind.Array
            ? habits.EnumerateArray().ToArray() : [arguments];
        return items.Where(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            .Select(item => new PendingHabitCreation(item.GetProperty("title").GetString()!,
                item.TryGetProperty("parent_habit_id", out _) ? ReadParent(item) : sharedParent)).ToArray();
    }

    private static Guid? ReadParent(JsonElement arguments) =>
        arguments.TryGetProperty("parent_habit_id", out var parent) && parent.ValueKind == JsonValueKind.String
            && Guid.TryParse(parent.GetString(), out var id) ? id : null;

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
            || HasChangeSetOwner(userId, pendingOperationId)
            || !entity.Cancel(cancelledAtUtc))
            return false;
        if (entity.OperationId == AccountChangeSet.OperationName)
            CancelChangeSetMembers(entity, cancelledAtUtc);
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

    private bool HasChangeSetOwner(Guid userId, Guid pendingOperationId)
    {
        var checkedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        return dbContext.PendingAgentOperations.AsNoTracking().Where(item => item.UserId == userId
                && item.OperationId == AccountChangeSet.OperationName && item.ConsumedAtUtc == null && item.ExpiresAtUtc > checkedAtUtc)
            .Select(item => item.ArgumentsJson).AsEnumerable()
            .Any(json => AccountChangeSet.Reservations(ParseArguments(json)).Any(member => member.PendingOperationId == pendingOperationId));
    }

    private void CancelChangeSetMembers(PendingAgentOperationState owner, DateTime cancelledAtUtc)
    {
        var reservations = AccountChangeSet.Reservations(ParseArguments(owner.ArgumentsJson)).ToDictionary(member => member.PendingOperationId);
        var ids = reservations.Keys.ToArray();
        foreach (var member in dbContext.PendingAgentOperations.Where(item => item.UserId == owner.UserId
            && ids.Contains(item.Id) && item.ConsumedAtUtc == null))
            if (member.OperationFingerprint == reservations[member.Id].Fingerprint) member.Cancel(cancelledAtUtc);
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
            entity.ConfirmationRequirement, entity.PreviewFingerprint, entity.ArgumentSchemaVersion, entity.ExpiresAtUtc, PendingOperationActionKeys.GetRequired(entity.OperationId, entity.ArgumentsJson));
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
        if (entity is null || HasChangeSetOwner(userId, entity.Id) || !entity.IsUsable(capabilityId, operationFingerprint, requireStepUp, DateTime.UtcNow))
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
