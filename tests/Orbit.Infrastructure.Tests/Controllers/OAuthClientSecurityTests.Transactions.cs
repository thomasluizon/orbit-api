using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthClientSecurityTests
{
    [Theory]
    [InlineData("view")]
    [InlineData("grant")]
    [InlineData("cancel")]
    public async Task Consent_SecondBrowserAuthorizesSameState_CannotCompleteOriginalTransaction(string action)
    {
        var client = OAuthClient.Create("Claude", [RedirectUri]);
        _db.Context.Add(client);
        await _db.Context.SaveChangesAsync();
        var request = new OAuthController.AuthorizeRequest
        {
            ClientId = client.Id.ToString(),
            RedirectUri = RedirectUri,
            State = "shared-state",
            ResponseType = "code",
            CodeChallenge = "challenge",
            CodeChallengeMethod = "S256"
        };
        await _controller.Authorize(request);
        var originalCookie = _controller.Response.Headers.SetCookie.Last()!.Split(';')[0];
        _controller.Request.Headers.Cookie = originalCookie;
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>()).Returns(
            Result.Success(new LoginResponse(Guid.NewGuid(), "token", "User", "user@example.com")));
        var verified = (await _controller.VerifyCode(new("user@example.com", "123456", request.State,
            request.CodeChallenge, RedirectUri, request.ClientId), default)).Should().BeOfType<OkObjectResult>().Subject;
        var url = JsonSerializer.SerializeToElement(verified.Value).GetProperty("redirectUrl").GetString()!;
        var ticket = System.Web.HttpUtility.ParseQueryString(new Uri("https://api.example.com" + url).Query)["ticket"]!;
        _controller.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        Error(Act()).Should().Be("invalid_request");
        await _controller.Authorize(request);
        _controller.Request.Headers.Cookie = _controller.Response.Headers.SetCookie.Last()!.Split(';')[0];
        Error(Act()).Should().Be("invalid_request");
        _store.ReadPendingConsent(ticket)!.IsConsumed.Should().BeFalse();
        _controller.Request.Headers.Cookie = originalCookie;
        _controller.GrantConsent(ticket, "requested").Should().BeOfType<RedirectResult>();

        IActionResult Act() => action switch
        {
            "view" => _controller.Consent(ticket),
            "cancel" => _controller.CancelConsent(ticket),
            _ => _controller.GrantConsent(ticket, "requested")
        };
    }

    [Fact]
    public async Task GoogleStart_InvalidProtocolStillValidatesClient()
    {
        Error(await _controller.GoogleStart("unknown", RedirectUri, "state", "challenge", "plain"))
            .Should().Be("invalid_client");
    }
}
