using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class AuthControllerTests
{
    [Fact]
    public async Task AppleNative_VerifiedLogin_ReturnsSessionWithoutCaching()
    {
        var login = new LoginResponse(UserId, "access", "Apple", "apple@example.com");
        _mediator.Send(Arg.Any<AppleNativeSignInCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(login));
        ((OkObjectResult)await _controller.AppleNative(new("identity", "code", "nonce"), default)).Value.Should().Be(login);
        _controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://appleid.apple.com/auth/authorize?state=opaque")]
    public async Task AppleStart_ConfiguredOrDisabled_ReturnsAvailability(string? authorizationUrl)
    {
        _mediator.Send(Arg.Any<StartAppleSignInCommand>(), Arg.Any<CancellationToken>()).Returns(authorizationUrl);
        var result = await _controller.AppleStart(new("state"), default);
        if (authorizationUrl is null)
            ((StatusCodeResult)result).StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        else
            ((AuthController.AppleStartResponse)((OkObjectResult)result).Value!).AuthorizationUrl.Should().Be(authorizationUrl);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppleCallback_MissingCodeOrAppleError_RejectsBeforeDispatch(bool appleError)
    {
        var result = await _controller.AppleCallback(new() { State = "state", Code = appleError ? "code" : null, Error = appleError ? "access_denied" : null }, default);
        result.Should().BeOfType<UnauthorizedResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<CompleteAppleCallbackCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppleCallback_InvalidServerState_DoesNotRedirect()
    {
        _mediator.Send(Arg.Any<CompleteAppleCallbackCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<string>(ErrorMessages.InvalidSession));
        ((ObjectResult)await _controller.AppleCallback(new() { State = "unknown", Code = "code" }, default)).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _controller.Response.Headers.Location.Should().BeEmpty();
    }

    [Fact]
    public async Task AppleHandoff_Valid_ReturnsSession()
    {
        var login = new LoginResponse(UserId, "access", "Apple", "apple@example.com");
        _mediator.Send(Arg.Any<TakeAppleHandoffCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(login));
        ((OkObjectResult)await _controller.AppleHandoff(new("handoff", "state"), default)).Value.Should().Be(login);
    }
}
