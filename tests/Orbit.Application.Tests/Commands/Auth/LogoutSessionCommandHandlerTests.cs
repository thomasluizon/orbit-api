using FluentAssertions;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Application.Common;

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
    public async Task Handle_InvalidToken_ReturnsFailure()
    {
        _authSessionService.RevokeSessionAsync("invalid-token", Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid refresh token"));

        var result = await _handler.Handle(new LogoutSessionCommand("invalid-token"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Invalid refresh token");
    }
}
