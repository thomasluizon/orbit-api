using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Calendar;

public class GetCalendarAutoSyncStateQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly GetCalendarAutoSyncStateQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetCalendarAutoSyncStateQueryHandlerTests()
    {
        _handler = new GetCalendarAutoSyncStateQueryHandler(_userRepo);
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(new GetCalendarAutoSyncStateQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_UserFound_NoGoogleConnection_ReturnsState()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new GetCalendarAutoSyncStateQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Enabled.Should().BeFalse();
        result.Value.Status.Should().Be(GoogleCalendarAutoSyncStatus.Idle);
        result.Value.LastSyncedAt.Should().BeNull();
        result.Value.HasGoogleConnection.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_UserWithGoogleConnection_ReturnsConnectionState()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetGoogleTokens("access-token", "refresh-token");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new GetCalendarAutoSyncStateQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasGoogleConnection.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_AutoSyncEnabled_ReturnsEnabledState()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetGoogleTokens("access-token", "refresh-token");
        user.EnableCalendarAutoSync();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new GetCalendarAutoSyncStateQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Enabled.Should().BeTrue();
        result.Value.Status.Should().Be(GoogleCalendarAutoSyncStatus.Idle);
    }
}
