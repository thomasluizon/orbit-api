using FluentAssertions;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class LogoutAllSessionsCommandHandlerTests
{
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly LogoutAllSessionsCommandHandler _handler;

    public LogoutAllSessionsCommandHandlerTests()
    {
        _handler = new LogoutAllSessionsCommandHandler(_authSessionService);
    }

    [Fact]
    public async Task Handle_RevokesEverySessionForTheUser()
    {
        var userId = Guid.NewGuid();
        _authSessionService.RevokeAllSessionsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _handler.Handle(new LogoutAllSessionsCommand(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _authSessionService.Received(1).RevokeAllSessionsAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PropagatesFailure()
    {
        var userId = Guid.NewGuid();
        _authSessionService.RevokeAllSessionsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("User ID is required.", "USER_ID_REQUIRED"));

        var result = await _handler.Handle(new LogoutAllSessionsCommand(userId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("USER_ID_REQUIRED");
    }
}
