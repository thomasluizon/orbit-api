using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class UserGoogleTokensTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("new-refresh")]
    public void SetGoogleTokens_AfterReconnectRequired_ClearsStatusAndErrorWithoutEnablingAutoSync(string? refreshToken)
    {
        var user = User.Create("User", "user@example.com").Value;
        user.SetGoogleTokens("old-access", "old-refresh");
        user.EnableCalendarAutoSync().IsSuccess.Should().BeTrue();
        user.MarkCalendarSyncReconnectRequired("invalid_grant");

        user.SetGoogleTokens("new-access", refreshToken);

        user.GoogleCalendarAutoSyncStatus.Should().Be(GoogleCalendarAutoSyncStatus.Idle);
        user.GoogleCalendarLastSyncError.Should().BeNull();
        user.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
        user.GoogleAccessToken.Should().Be("new-access");
        user.GoogleRefreshToken.Should().Be(refreshToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(GoogleCalendarAutoSyncStatus.Idle)]
    [InlineData(GoogleCalendarAutoSyncStatus.TransientError)]
    public void SetGoogleTokens_RefreshWithoutReconnectRequired_PreservesSyncState(GoogleCalendarAutoSyncStatus? status)
    {
        var user = User.Create("User", "user@example.com").Value;
        user.SetGoogleTokens("old-access", "refresh");
        if (status is not null)
            user.EnableCalendarAutoSync().IsSuccess.Should().BeTrue();
        if (status == GoogleCalendarAutoSyncStatus.TransientError)
            user.MarkCalendarSyncTransientError("timeout");
        var enabled = user.GoogleCalendarAutoSyncEnabled;
        var error = user.GoogleCalendarLastSyncError;

        user.SetGoogleTokens("new-access", null);

        user.GoogleCalendarAutoSyncStatus.Should().Be(status);
        user.GoogleCalendarAutoSyncEnabled.Should().Be(enabled);
        user.GoogleCalendarLastSyncError.Should().Be(error);
        user.GoogleAccessToken.Should().Be("new-access");
        user.GoogleRefreshToken.Should().Be("refresh");
    }

    [Fact]
    public void SetGoogleTokens_PreservesRefreshTokenWhenAbsent()
    {
        var user = User.Create("User", "user@example.com").Value;
        user.SetGoogleTokens("first", "refresh");

        user.SetGoogleTokens("second", null);

        user.GoogleAccessToken.Should().Be("second");
        user.GoogleRefreshToken.Should().Be("refresh");
    }

    [Fact]
    public void SetGoogleTokens_RejectsBlankAccessToken()
    {
        var user = User.Create("User", "user@example.com").Value;

        var act = () => user.SetGoogleTokens(" ", "refresh");

        act.Should().Throw<ArgumentException>();
        user.GoogleAccessToken.Should().BeNull();
    }
}
