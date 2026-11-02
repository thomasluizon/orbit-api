using FluentAssertions;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class RefreshSessionCommandHandlerTests
{
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly RefreshSessionCommandHandler _handler;

    public RefreshSessionCommandHandlerTests()
    {
        _handler = new RefreshSessionCommandHandler(_authSessionService);
    }

    [Fact]
    public async Task Handle_InvalidToken_ReturnsFailure()
    {
        _authSessionService.RefreshSessionAsync("expired-token", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SessionTokens>("Session expired", "SESSION_EXPIRED"));

        var result = await _handler.Handle(new RefreshSessionCommand("expired-token"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Session expired");
        result.ErrorCode.Should().Be("SESSION_EXPIRED");
    }
}
