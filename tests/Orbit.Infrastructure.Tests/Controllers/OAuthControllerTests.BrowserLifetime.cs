using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthControllerTests
{
    [Theory]
    [InlineData(6, true)]
    [InlineData(30, false)]
    [InlineData(31, false)]
    public async Task VerifyCode_AfterAuthorize_RespectsWholeSignInLifetime(int elapsedMinutes, bool succeeds)
    {
        const string state = "late-email-state";
        (await _controller.Authorize(new OAuthController.AuthorizeRequest
        {
            ClientId = OAuthTestClient.ClientId,
            RedirectUri = OAuthTestClient.RedirectUri,
            State = state,
            ResponseType = "code",
            CodeChallenge = "challenge",
            CodeChallengeMethod = "S256"
        })).Should().BeOfType<ContentResult>();
        var cookie = _controller.Response.Headers.SetCookie.Last()!;
        _controller.Request.Headers.Cookie = cookie.Split(';')[0];
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "token", "User", "user@example.com")));
        _timeProvider.Advance(TimeSpan.FromMinutes(elapsedMinutes));

        var result = await _controller.VerifyCode(new("user@example.com", "123456", state, "challenge",
            OAuthTestClient.RedirectUri, OAuthTestClient.ClientId), default);

        if (succeeds)
        {
            result.Should().BeOfType<OkObjectResult>();
            ExtractRedirectUrl(result).Should().Contain("state=" + state);
            cookie.Should().Contain("max-age=1800");
            _controller.Response.Headers.SetCookie.Last()!.Should().Contain("max-age=1800");
            await _mediator.Received(1).Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>());
        }
        else
        {
            result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeEquivalentTo(new { error = "invalid_request" });
            await _mediator.DidNotReceive().Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>());
        }
    }
}
