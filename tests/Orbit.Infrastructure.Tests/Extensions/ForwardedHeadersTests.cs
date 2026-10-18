using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Extensions;

public class ForwardedHeadersTests
{
    [Theory]
    [InlineData("10.24.103.103", "203.0.113.10", "203.0.113.10")]
    [InlineData("10.24.103.103", "203.0.113.10, 10.0.0.1", "10.0.0.1")]
    [InlineData("198.51.100.7", "203.0.113.10", "198.51.100.7")]
    [InlineData("198.51.100.7", "203.0.113.10, 10.0.0.1", "198.51.100.7")]
    public async Task CommittedTrustConfiguration_UnwindsOnlyOneHopFromTrustedPeer(
        string peer, string forwardedFor, string expectedIp)
    {
        var apiDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api"));
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile(Path.Combine(apiDirectory, "appsettings.json"))
            .AddJsonFile(Path.Combine(apiDirectory, "appsettings.Production.json"));
        await using var app = builder.Build();
        var options = (ForwardedHeadersOptions)typeof(WebApplicationExtensions)
            .GetMethod("BuildForwardedHeadersOptions", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [app])!;
        options.ForwardLimit.Should().Be(1);
        options.KnownIPNetworks.Should().Contain(System.Net.IPNetwork.Parse("10.0.0.0/8"));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        var nextCalled = false;
        var middleware = new ForwardedHeadersMiddleware(
            http =>
            {
                nextCalled = true;
                http.GetClientIpAddress().Should().Be(expectedIp);
                return Task.CompletedTask;
            }, NullLoggerFactory.Instance, Options.Create(options));

        await middleware.Invoke(context);

        nextCalled.Should().BeTrue();
    }
}
