using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using ApiServices = Orbit.Api.Extensions.ServiceCollectionExtensions;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthControllerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CookiePipeline_GoogleCallback_RequiresOriginalCookieAndCompletesConsent(bool cancel)
    {
        await using var host = new OAuthPipelineHost(_controller);
        var cookie = await host.AuthorizeAsync();
        _mediator.Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "token", "User", "user@example.com")));
        var start = await host.SendAsync("/oauth/google/start?" + OAuthPipelineHost.Parameters, cookie);
        start.Response.StatusCode.Should().Be(302);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(start.Response.Headers.Location.ToString()).Query)["state"]!;
        var callback = await host.SendAsync("/oauth/google/callback?code=code&state=" + state, cookie,
            origin: "https://accounts.google.com");
        callback.Response.StatusCode.Should().Be(302);
        callback.Response.Headers.SetCookie.Should().ContainSingle().Which.Should().Contain("samesite=lax");
        await _mediator.Received(1).Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
        await AssertPipelineConsent(host, callback.Response.Headers.Location.ToString(), cookie, cancel);
    }

    [Fact]
    public async Task CookiePipeline_GoogleCallback_WithoutCookieRejectsBeforeExchange()
    {
        await using var host = new OAuthPipelineHost(_controller);
        var cookie = await host.AuthorizeAsync();
        var start = await host.SendAsync("/oauth/google/start?" + OAuthPipelineHost.Parameters, cookie);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(start.Response.Headers.Location.ToString()).Query)["state"]!;
        var callback = await host.SendAsync("/oauth/google/callback?code=code&state=" + state,
            origin: "https://accounts.google.com");
        AssertPipelineBindingRejected(callback);
        await _mediator.DidNotReceive().Send(Arg.Any<GoogleCodeAuthCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CookiePipeline_EmailCode_RequiresCookieAndCompletesConsent(bool cancel)
    {
        await using var host = new OAuthPipelineHost(_controller);
        var cookie = await host.AuthorizeAsync();
        _mediator.Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new LoginResponse(UserId, "token", "User", "user@example.com")));
        var body = JsonSerializer.Serialize(new Orbit.Api.Controllers.OAuthController.VerifyCodeRequest(
            "user@example.com", "123456", "client-state", "challenge", OAuthTestClient.RedirectUri, OAuthTestClient.ClientId));
        AssertPipelineBindingRejected(await host.SendAsync("/oauth/verify-code", method: "POST", body: body,
            contentType: "application/json"));
        await _mediator.DidNotReceive().Send(Arg.Any<VerifyCodeCommand>(), Arg.Any<CancellationToken>());
        var verified = await host.SendAsync("/oauth/verify-code", cookie, "POST", body, "application/json");
        verified.Response.StatusCode.Should().Be(200);
        verified.Response.Headers.SetCookie.Should().ContainSingle().Which.Should().Contain("samesite=lax");
        using var document = JsonDocument.Parse(PipelineBody(verified));
        await AssertPipelineConsent(host, document.RootElement.GetProperty("redirectUrl").GetString()!, cookie, cancel);
    }

    [Theory]
    [InlineData("other_cookie", "/oauth")]
    [InlineData("orbit_oauth_unrelated", "/oauth")]
    [InlineData("orbit_oauth_0000000000000000000000000000000000000000000000000000000000000000", "/")]
    public async Task CookiePipeline_OtherCookies_RetainStrictSecureHttpOnly(string name, string path)
    {
        var builder = WebApplication.CreateBuilder();
        ApiServices.AddCookieAndKestrelLimits(builder);
        await using var application = builder.Build();
        var pipeline = new ApplicationBuilder(application.Services);
        pipeline.UseCookiePolicy();
        pipeline.Run(context =>
        {
            context.Response.Cookies.Append(name, "value", new CookieOptions { SameSite = SameSiteMode.Lax, Path = path });
            return Task.CompletedTask;
        });
        var response = new DefaultHttpContext { RequestServices = application.Services };
        await pipeline.Build()(response);
        response.Response.Headers.SetCookie.Should().ContainSingle().Which.Should()
            .Contain("samesite=strict").And.Contain("secure").And.Contain("httponly");
    }

    internal static async Task AssertPipelineConsent(OAuthPipelineHost host, string location, string cookie, bool cancel)
    {
        location.Should().StartWith("/oauth/consent?ticket=");
        AssertPipelineBindingRejected(await host.SendAsync(location));
        var consent = await host.SendAsync(location, cookie);
        consent.Response.StatusCode.Should().Be(200);
        consent.Response.Headers.SetCookie.Should().ContainSingle().Which.Should().Contain("samesite=lax");
        var ticket = System.Web.HttpUtility.ParseQueryString(new Uri("https://api.useorbit.org" + location).Query)["ticket"]!;
        var post = cancel ? "/oauth/consent/cancel" : "/oauth/consent";
        var body = "ticket=" + Uri.EscapeDataString(ticket) + "&access=requested";
        AssertPipelineBindingRejected(await host.SendAsync(post, method: "POST", body: body, origin: "https://attacker.example"));
        var completed = await host.SendAsync(post, cookie, "POST", body);
        completed.Response.StatusCode.Should().Be(302);
        completed.Response.Headers.Location.ToString().Should().StartWith(OAuthTestClient.RedirectUri + "?")
            .And.Contain("state=client-state").And.Contain(cancel ? "error=access_denied" : "code=");
    }

    private static void AssertPipelineBindingRejected(HttpContext response)
    {
        response.Response.StatusCode.Should().Be(400);
        using var document = JsonDocument.Parse(PipelineBody(response));
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_request");
    }

    private static string PipelineBody(HttpContext response) => Encoding.UTF8.GetString(((MemoryStream)response.Response.Body).ToArray());
}
