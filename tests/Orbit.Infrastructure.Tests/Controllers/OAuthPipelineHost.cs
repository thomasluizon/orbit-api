using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Services;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using ApiServices = Orbit.Api.Extensions.ServiceCollectionExtensions;

namespace Orbit.Infrastructure.Tests.Controllers;

internal sealed class OAuthPipelineHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly RequestDelegate _pipeline;

    internal OAuthPipelineHost(OAuthController controller, AppleAuthorizationFlow? apple = null)
    {
        var builder = WebApplication.CreateBuilder();
        ApiServices.AddCookieAndKestrelLimits(builder);
        builder.Services.AddControllers().AddApplicationPart(typeof(OAuthController).Assembly).AddControllersAsServices();
        builder.Services.AddSingleton(controller);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<BffClientIpResolver>();
        builder.Services.AddSingleton(Substitute.For<IAuthSessionService>());
        var limits = Substitute.For<IDistributedRateLimitService>();
        limits.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 100, 1, DateTime.MaxValue));
        builder.Services.AddSingleton(limits);
        if (apple is not null)
            builder.Services.AddSingleton(apple);
        _application = builder.Build();
        var routes = new ApplicationBuilder(_application.Services);
        routes.UseRouting();
        routes.UseCookiePolicy();
        routes.UseAuthorization();
        routes.UseEndpoints(endpoints => endpoints.MapControllers());
        _pipeline = routes.Build();
    }

    internal async Task<HttpContext> SendAsync(string url, string? cookie = null, string method = "GET",
        string? body = null, string contentType = "application/x-www-form-urlencoded", string? origin = null)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var uri = new Uri("https://api.useorbit.org" + url);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.Scheme = uri.Scheme;
        context.Request.Host = new HostString(uri.Host);
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        if (cookie is not null)
            context.Request.Headers.Cookie = cookie;
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = contentType;
        }
        context.Response.Body = new MemoryStream();
        await _pipeline(context);
        return context;
    }

    internal const string Parameters = "client_id=https%3A%2F%2Fclaude.ai%2Foauth%2Fmcp-oauth-client-metadata"
        + "&redirect_uri=https%3A%2F%2Fclaude.ai%2Fapi%2Fmcp%2Fauth_callback&state=client-state"
        + "&code_challenge=challenge&code_challenge_method=S256";

    internal async Task<string> AuthorizeAsync()
    {
        var response = await SendAsync("/oauth/authorize?response_type=code&" + Parameters, origin: "https://claude.ai");
        response.Response.StatusCode.Should().Be(200);
        var cookie = response.Response.Headers.SetCookie.Should().ContainSingle().Subject!;
        cookie.Should().StartWith(OAuthTestClient.CookieName("client-state") + "=");
        cookie.Should().Contain("samesite=lax").And.Contain("secure").And.Contain("httponly")
            .And.Contain("path=/oauth").And.Contain("max-age=1800");
        return cookie.Split(';')[0];
    }

    public ValueTask DisposeAsync() => _application.DisposeAsync();
}
