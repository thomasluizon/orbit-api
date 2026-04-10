using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class UserCalendarAutoSyncTests
{
    private static User CreateProUserWithGoogleToken()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(60));
        user.SetGoogleTokens("access", "refresh");
        return user;
    }

    [Fact]
    public void EnableCalendarAutoSync_ProWithGoogleToken_Succeeds()
    {
        var user = CreateProUserWithGoogleToken();

        var result = user.EnableCalendarAutoSync();

        result.IsSuccess.Should().BeTrue();
        user.GoogleCalendarAutoSyncEnabled.Should().BeTrue();
        user.GoogleCalendarAutoSyncStatus.Should().Be(GoogleCalendarAutoSyncStatus.Idle);
    }

    [Fact]
    public void EnableCalendarAutoSync_NonProUser_Fails()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetGoogleTokens("access", "refresh");
        // Clear trial
        typeof(User).GetProperty("TrialEndsAt")!.SetValue(user, null);

        var result = user.EnableCalendarAutoSync();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("calendar.autoSync.proRequired");
        user.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
    }

    [Fact]
    public void EnableCalendarAutoSync_NoGoogleToken_Fails()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(60));

        var result = user.EnableCalendarAutoSync();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("calendar.autoSync.notConnected");
    }

    [Fact]
    public void DisableCalendarAutoSync_ClearsFlagAndError()
    {
        var user = CreateProUserWithGoogleToken();
        user.EnableCalendarAutoSync();
        user.MarkCalendarSyncTransientError("boom");

        user.DisableCalendarAutoSync();

        user.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
        user.GoogleCalendarLastSyncError.Should().BeNull();
    }

    [Fact]
    public void MarkCalendarSyncReconnectRequired_ClearsTokensAndFlipsOff()
    {
        var user = CreateProUserWithGoogleToken();
        user.EnableCalendarAutoSync();

        user.MarkCalendarSyncReconnectRequired("invalid_grant");

        user.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
        user.GoogleCalendarAutoSyncStatus.Should().Be(GoogleCalendarAutoSyncStatus.ReconnectRequired);
        user.GoogleAccessToken.Should().BeNull();
        user.GoogleRefreshToken.Should().BeNull();
        user.GoogleCalendarLastSyncError.Should().Be("invalid_grant");
    }

    [Fact]
    public void MarkCalendarSyncSuccess_UpdatesTimestampAndClearsError()
    {
        var user = CreateProUserWithGoogleToken();
        user.MarkCalendarSyncTransientError("prev");

        var now = new DateTime(2026, 4, 9, 12, 0, 0, DateTimeKind.Utc);
        user.MarkCalendarSyncSuccess(now);

        user.GoogleCalendarAutoSyncStatus.Should().Be(GoogleCalendarAutoSyncStatus.Idle);
        user.GoogleCalendarLastSyncedAt.Should().Be(now);
        user.GoogleCalendarLastSyncError.Should().BeNull();
    }

    [Fact]
    public void MarkCalendarSyncTransientError_SetsStatusAndMessage()
    {
        var user = CreateProUserWithGoogleToken();

        user.MarkCalendarSyncTransientError("timeout");

        user.GoogleCalendarAutoSyncStatus.Should().Be(GoogleCalendarAutoSyncStatus.TransientError);
        user.GoogleCalendarLastSyncError.Should().Be("timeout");
    }

    [Fact]
    public void MarkCalendarSyncReconciled_SetsTimestamp()
    {
        var user = CreateProUserWithGoogleToken();
        var now = new DateTime(2026, 4, 9, 12, 0, 0, DateTimeKind.Utc);

        user.MarkCalendarSyncReconciled(now);

        user.GoogleCalendarSyncReconciledAt.Should().Be(now);
    }

    [Fact]
    public void ResetAccount_ClearsAllAutoSyncFields()
    {
        var user = CreateProUserWithGoogleToken();
        user.EnableCalendarAutoSync();
        user.MarkCalendarSyncSuccess(DateTime.UtcNow);
        user.MarkCalendarSyncReconciled(DateTime.UtcNow);

        user.ResetAccount();

        user.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
        user.GoogleCalendarAutoSyncStatus.Should().BeNull();
        user.GoogleCalendarLastSyncedAt.Should().BeNull();
        user.GoogleCalendarLastSyncError.Should().BeNull();
        user.GoogleCalendarSyncReconciledAt.Should().BeNull();
    }
}
