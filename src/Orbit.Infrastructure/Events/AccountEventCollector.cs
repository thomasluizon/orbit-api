using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Events;

public sealed class AccountEventCollector(IAccountEventBus bus, IHttpContextAccessor httpContextAccessor)
    : SaveChangesInterceptor, IAccountEventCollector
{
    private readonly Dictionary<Guid, List<AccountChange>> _pending = [];
    private readonly HashSet<Guid> _resyncUsers = [];
    private readonly HashSet<Guid> _revokedSessions = [];
    private readonly HashSet<Guid> _deletedUsers = [];
    private Dictionary<Guid, List<AccountChange>>? _staged;
    private HashSet<Guid>? _stagedRevokedSessions;
    private HashSet<Guid>? _stagedDeletedUsers;
    private string? _origin;

    public void MarkResync(Guid userId)
    {
        if (userId != Guid.Empty)
            _resyncUsers.Add(userId);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is OrbitDbContext context)
        {
            context.ChangeTracker.DetectChanges();
            _staged = context.ChangeTracker.HasChanges() ? Collect(context.ChangeTracker) : null;
            _stagedRevokedSessions = context.ChangeTracker.Entries<UserSession>()
                .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted
                    && (entry.Entity.RevokedAtUtc is not null || entry.State == EntityState.Deleted))
                .Select(entry => entry.Entity.Id).ToHashSet();
            _stagedDeletedUsers = context.ChangeTracker.Entries<User>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id).ToHashSet();
            var candidate = httpContextAccessor.HttpContext?.Request.Headers["X-Orbit-Event-Origin"].ToString();
            _origin = candidate is { Length: > 0 and <= 128 } ? candidate : null;
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (_staged is not null)
        {
            foreach (var (userId, changes) in _staged)
            {
                if (!_pending.TryGetValue(userId, out var pending))
                    _pending[userId] = pending = [];
                pending.AddRange(changes);
            }
            _staged = null;
        }
        if (_stagedRevokedSessions is not null)
            _revokedSessions.UnionWith(_stagedRevokedSessions);
        if (_stagedDeletedUsers is not null)
            _deletedUsers.UnionWith(_stagedDeletedUsers);
        _stagedRevokedSessions = null;
        _stagedDeletedUsers = null;

        if (eventData.Context?.Database.CurrentTransaction is null)
            Flush();

        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _staged = null;
        _stagedRevokedSessions = null;
        _stagedDeletedUsers = null;
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public void Flush()
    {
        foreach (var userId in _pending.Keys.Concat(_resyncUsers).Distinct())
        {
            var changes = _pending.GetValueOrDefault(userId) ?? [];
            bus.Publish(userId, new AccountEventPayload(1, Collapse(changes), _origin), _resyncUsers.Contains(userId));
        }
        foreach (var sessionId in _revokedSessions)
            bus.CloseSession(sessionId);
        foreach (var userId in _deletedUsers)
            bus.CloseAccount(userId);
        Clear();
    }

    public void Clear()
    {
        _pending.Clear();
        _resyncUsers.Clear();
        _revokedSessions.Clear();
        _deletedUsers.Clear();
        _staged = null;
        _stagedRevokedSessions = null;
        _stagedDeletedUsers = null;
        _origin = null;
    }

    private static IReadOnlyList<AccountChange> Collapse(List<AccountChange> changes) =>
        changes.GroupBy(change => (change.Kind, change.Op))
            .Select(group => new AccountChange(group.Key.Kind, group.Key.Op,
                group.SelectMany(change => change.Ids).Distinct().ToArray(),
                group.SelectMany(change => change.Dates ?? []).Distinct().ToArray() is { Length: > 0 } dates
                    ? dates : null))
            .ToArray();

    private static Dictionary<Guid, List<AccountChange>> Collect(ChangeTracker tracker)
    {
        var result = new Dictionary<Guid, List<AccountChange>>();
        var habits = tracker.Entries<Habit>().ToDictionary(entry => entry.Entity.Id, entry => entry.Entity.UserId);
        var goals = tracker.Entries<Goal>().ToDictionary(entry => entry.Entity.Id, entry => entry.Entity.UserId);
        var tags = tracker.Entries<Tag>().ToDictionary(entry => entry.Entity.Id, entry => entry.Entity.UserId);

        foreach (var entry in tracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Metadata.Name is "HabitTags" or "HabitGoals")
            {
                if (entry.Property("HabitId").CurrentValue is Guid habitId
                    && habits.TryGetValue(habitId, out var habitOwner))
                    Add(result, habitOwner, new AccountChange("habit", "update", [habitId]));

                if (entry.Metadata.Name == "HabitTags"
                    && entry.Property("TagId").CurrentValue is Guid tagId
                    && tags.TryGetValue(tagId, out var tagOwner))
                    Add(result, tagOwner, new AccountChange("tag", "update", [tagId]));

                if (entry.Metadata.Name == "HabitGoals"
                    && entry.Property("GoalId").CurrentValue is Guid goalId
                    && goals.TryGetValue(goalId, out var goalOwner))
                    Add(result, goalOwner, new AccountChange("goal", "update", [goalId]));
                continue;
            }

            Guid userId;
            string kind;
            DateOnly[]? dates = null;
            switch (entry.Entity)
            {
                case Habit habit:
                    userId = habit.UserId;
                    kind = "habit";
                    break;
                case HabitLog log when habits.TryGetValue(log.HabitId, out userId):
                    kind = "habitLog";
                    dates = [log.Date];
                    break;
                case Goal goal:
                    userId = goal.UserId;
                    kind = "goal";
                    break;
                case GoalProgressLog log when goals.TryGetValue(log.GoalId, out userId):
                    kind = "goalProgress";
                    break;
                case Tag tag:
                    userId = tag.UserId;
                    kind = "tag";
                    break;
                case ChecklistTemplate template:
                    userId = template.UserId;
                    kind = "checklistTemplate";
                    break;
                case User user:
                    userId = user.Id;
                    kind = "profile";
                    break;
                default:
                    continue;
            }

            if (userId == Guid.Empty)
                continue;
            var deleted = entry.State == EntityState.Deleted || entry.Entity is ISoftDeletable { IsDeleted: true };
            var op = deleted ? "delete" : entry.State == EntityState.Added ? "create" : "update";
            Add(result, userId, new AccountChange(kind, op, [((Entity)entry.Entity).Id], dates));
        }
        return result;
    }

    private static void Add(Dictionary<Guid, List<AccountChange>> result, Guid userId, AccountChange change)
    {
        if (!result.TryGetValue(userId, out var changes))
            result[userId] = changes = [];
        changes.Add(change);
    }
}

public sealed class AccountEventTransactionInterceptor(AccountEventCollector collector) : DbTransactionInterceptor
{
    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        collector.Flush();
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        collector.Clear();
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    public override Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        collector.Clear();
        return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
    }
}
