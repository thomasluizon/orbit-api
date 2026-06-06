using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the domain logic that AccountDeletionService relies on:
/// User deactivation, scheduled deletion, cancellation, and the
/// stale record cleanup cutoff logic.
/// The background service loop and DB interactions are integration concerns.
/// </summary>
public class AccountDeletionServiceTests
{
    private static User CreateUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public void Deactivate_SetsIsDeactivatedAndScheduledDeletion()
    {
        var user = CreateUser();
        var scheduledDeletion = DateTime.UtcNow.AddDays(30);

        user.Deactivate(scheduledDeletion);

        user.IsDeactivated.Should().BeTrue();
        user.ScheduledDeletionAt.Should().Be(scheduledDeletion);
        user.DeactivatedAt.Should().NotBeNull();
    }

    [Fact]
    public void CancelDeactivation_ClearsDeactivationState()
    {
        var user = CreateUser();
        user.Deactivate(DateTime.UtcNow.AddDays(30));

        user.CancelDeactivation();

        user.IsDeactivated.Should().BeFalse();
        user.ScheduledDeletionAt.Should().BeNull();
        user.DeactivatedAt.Should().BeNull();
    }

    [Fact]
    public void Deactivate_ThenCancel_ThenDeactivate_WorksCorrectly()
    {
        var user = CreateUser();

        user.Deactivate(DateTime.UtcNow.AddDays(30));
        user.CancelDeactivation();
        var newDeletion = DateTime.UtcNow.AddDays(60);
        user.Deactivate(newDeletion);

        user.IsDeactivated.Should().BeTrue();
        user.ScheduledDeletionAt.Should().Be(newDeletion);
    }

    [Fact]
    public void ScheduledDeletionAt_PastDate_IsEligibleForDeletion()
    {
        var user = CreateUser();
        var pastDate = DateTime.UtcNow.AddDays(-1);
        user.Deactivate(pastDate);

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeTrue();
    }

    [Fact]
    public void ScheduledDeletionAt_FutureDate_IsNotEligibleForDeletion()
    {
        var user = CreateUser();
        var futureDate = DateTime.UtcNow.AddDays(30);
        user.Deactivate(futureDate);

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void NonDeactivatedUser_IsNotEligibleForDeletion()
    {
        var user = CreateUser();

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void CancelledDeactivation_IsNotEligibleForDeletion()
    {
        var user = CreateUser();
        user.Deactivate(DateTime.UtcNow.AddDays(-1));
        user.CancelDeactivation();

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void ScheduledDeletionAt_ExactlyNow_IsEligibleForDeletion()
    {
        var user = CreateUser();
        var now = DateTime.UtcNow;
        user.Deactivate(now);

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_SetsDeactivatedAtCloseToNow()
    {
        var user = CreateUser();
        var before = DateTime.UtcNow;

        user.Deactivate(DateTime.UtcNow.AddDays(30));

        user.DeactivatedAt.Should().NotBeNull();
        user.DeactivatedAt!.Value.Should().BeOnOrAfter(before);
        user.DeactivatedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Deactivate_MultipleTimes_UpdatesScheduledDeletion()
    {
        var user = CreateUser();
        var first = DateTime.UtcNow.AddDays(30);
        var second = DateTime.UtcNow.AddDays(60);

        user.Deactivate(first);
        user.ScheduledDeletionAt.Should().Be(first);

        user.Deactivate(second);
        user.ScheduledDeletionAt.Should().Be(second);
    }

    [Fact]
    public void StaleRecordCutoff_Is90DaysAgo()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var daysDiff = today.DayNumber - cutoff.DayNumber;
        daysDiff.Should().Be(90);
    }

    [Fact]
    public void StaleRecordCutoff_RecordAt91DaysAgo_ShouldBeDeleted()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var oldRecord = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-91));

        var shouldDelete = oldRecord < cutoff;
        shouldDelete.Should().BeTrue();
    }

    [Fact]
    public void StaleRecordCutoff_RecordAt89DaysAgo_ShouldNotBeDeleted()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var recentRecord = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-89));

        var shouldDelete = recentRecord < cutoff;
        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void StaleRecordCutoff_RecordExactlyAtCutoff_ShouldNotBeDeleted()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var exactRecord = cutoff;

        var shouldDelete = exactRecord < cutoff;
        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void DeletionEligibility_MultipleUsers_FiltersCorrectly()
    {
        var users = new[]
        {
            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-1)),            CreateDeactivatedUser(DateTime.UtcNow.AddDays(30)),            CreateUser(),            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-7)),        };

        var eligible = users.Where(u =>
            u.IsDeactivated
            && u.ScheduledDeletionAt.HasValue
            && u.ScheduledDeletionAt.Value <= DateTime.UtcNow).ToList();

        eligible.Should().HaveCount(2);
    }

    [Fact]
    public void DeletionEligibility_AllUsersActive_NoneEligible()
    {
        var users = Enumerable.Range(0, 5).Select(_ => CreateUser()).ToList();

        var eligible = users.Where(u =>
            u.IsDeactivated
            && u.ScheduledDeletionAt.HasValue
            && u.ScheduledDeletionAt.Value <= DateTime.UtcNow).ToList();

        eligible.Should().BeEmpty();
    }

    [Fact]
    public void DeletionEligibility_AllDeactivatedPastDue_AllEligible()
    {
        var users = new[]
        {
            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-1)),
            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-10)),
            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-30)),
        };

        var eligible = users.Where(u =>
            u.IsDeactivated
            && u.ScheduledDeletionAt.HasValue
            && u.ScheduledDeletionAt.Value <= DateTime.UtcNow).ToList();

        eligible.Should().HaveCount(3);
    }

    private static User CreateDeactivatedUser(DateTime scheduledDeletion)
    {
        var user = CreateUser();
        user.Deactivate(scheduledDeletion);
        return user;
    }
}
