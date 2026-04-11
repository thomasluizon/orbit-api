using FluentAssertions;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

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
    public async Task Handle_ValidToken_DelegatesToAuthSessionService()
    {
        _authSessionService.RevokeSessionAsync("valid-refresh-token", Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _handler.Handle(new LogoutSessionCommand("valid-refresh-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _authSessionService.Received(1).RevokeSessionAsync("valid-refresh-token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidToken_ReturnsFailure()
    {
        _authSessionService.RevokeSessionAsync("invalid-token", Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid refresh token"));

        var result = await _handler.Handle(new LogoutSessionCommand("invalid-token"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid");
    }
}
