using FluentAssertions;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class LogoutSessionCommandHandlerTests
{
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly LogoutSessionCommandHandler _handler;

    public LogoutSessionCommandHandlerTests()
    {
        _handler = new LogoutSessionCommandHandler(_authSessionService);
    }

    [Fact]
    public async Task Handle_ValidRefreshToken_CallsRevokeSession()
    {
        _authSessionService.RevokeSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var command = new LogoutSessionCommand("refresh_token_123");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _authSessionService.Received(1).RevokeSessionAsync("refresh_token_123", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidRefreshToken_ReturnsFailure()
    {
        _authSessionService.RevokeSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Session not found."));

        var command = new LogoutSessionCommand("invalid_token");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}

public class RefreshSessionCommandHandlerTests
{
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly RefreshSessionCommandHandler _handler;

    public RefreshSessionCommandHandlerTests()
    {
        _handler = new RefreshSessionCommandHandler(_authSessionService);
    }

    [Fact]
    public async Task Handle_ValidRefreshToken_ReturnsNewTokens()
    {
        var tokens = new SessionTokens("new_access_token", "new_refresh_token");
        _authSessionService.RefreshSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(tokens));

        var command = new RefreshSessionCommand("old_refresh_token");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("new_access_token");
        result.Value.RefreshToken.Should().Be("new_refresh_token");
    }

    [Fact]
    public async Task Handle_InvalidRefreshToken_ReturnsFailure()
    {
        _authSessionService.RefreshSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SessionTokens>("Invalid refresh token.", "INVALID_REFRESH_TOKEN"));

        var command = new RefreshSessionCommand("expired_token");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Invalid refresh token.");
        result.ErrorCode.Should().Be("INVALID_REFRESH_TOKEN");
    }
}
