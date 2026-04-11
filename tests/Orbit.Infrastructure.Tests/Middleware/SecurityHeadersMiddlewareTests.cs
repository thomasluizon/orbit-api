using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Orbit.Api.Middleware;

namespace Orbit.Infrastructure.Tests.Middleware;

public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_SetsXContentTypeOptions()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.Headers[HeaderNames.XContentTypeOptions].ToString().Should().Be("nosniff");
    }

    [Fact]
    public async Task InvokeAsync_SetsXFrameOptions()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.Headers[HeaderNames.XFrameOptions].ToString().Should().Be("DENY");
    }

    [Fact]
    public async Task InvokeAsync_SetsReferrerPolicy()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.Headers["Referrer-Policy"].ToString().Should().Be("strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task InvokeAsync_SetsXXSSProtection()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.Headers[HeaderNames.XXSSProtection].ToString().Should().Be("0");
    }

    [Fact]
    public async Task InvokeAsync_SetsHSTS()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.Headers[HeaderNames.StrictTransportSecurity].ToString()
            .Should().Contain("max-age=31536000");
    }

    [Fact]
    public async Task InvokeAsync_SetsPermissionsPolicy()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.Headers["Permissions-Policy"].ToString()
            .Should().Contain("camera=()");
    }

    [Fact]
    public async Task InvokeAsync_RegularPath_SetsStrictCSP()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/habits";
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.Headers[HeaderNames.ContentSecurityPolicy].ToString()
            .Should().Contain("default-src 'none'")
            .And.Contain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task InvokeAsync_ScalarPath_DoesNotSetCSP()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/scalar/docs";
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.Headers.ContainsKey("Content-Security-Policy").Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_OpenApiPath_DoesNotSetCSP()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/openapi/v1";
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.Headers.ContainsKey("Content-Security-Policy").Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_OAuthPath_SetsGoogleCSP()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/oauth/callback";
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var csp = context.Response.Headers[HeaderNames.ContentSecurityPolicy].ToString();
        csp.Should().Contain("accounts.google.com");
        csp.Should().Contain("apis.google.com");
        csp.Should().Contain("fonts.googleapis.com");
    }

    [Fact]
    public async Task InvokeAsync_CallsNextMiddleware()
    {
        var nextCalled = false;
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }
}
