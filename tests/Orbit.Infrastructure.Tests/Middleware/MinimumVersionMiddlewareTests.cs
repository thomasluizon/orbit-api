using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Middleware;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// The minimum-supported-version gate. Returns 426 only when the client's
/// X-App-Version is provably below the configured floor; a missing or
/// unparseable header always falls through (fail-safe allow) so clients that
/// predate the header are never stranded.
/// </summary>
public class MinimumVersionMiddlewareTests
{
    private const string FloorVersion = "1.0.0";

    private static IAppConfigService BuildConfigService(string floor)
    {
        var configService = Substitute.For<IAppConfigService>();
        configService
            .GetAsync(AppConfigKeys.MinSupportedVersion, "0.0.0", Arg.Any<CancellationToken>())
            .Returns(floor);
        return configService;
    }

    private static DefaultHttpContext BuildContext(string? clientVersion)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (clientVersion is not null)
            context.Request.Headers["X-App-Version"] = clientVersion;
        return context;
    }

    private static async Task<bool> InvokeMiddleware(DefaultHttpContext context, IAppConfigService configService)
    {
        var nextCalled = false;
        var middleware = new MinimumVersionMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<MinimumVersionMiddleware>.Instance);

        await middleware.InvokeAsync(context, configService);
        return nextCalled;
    }

    private static async Task<(bool NextCalled, DefaultHttpContext Context)> InvokeWithVersion(
        string? clientVersion,
        string floor = FloorVersion)
    {
        var context = BuildContext(clientVersion);
        var nextCalled = await InvokeMiddleware(context, BuildConfigService(floor));
        return (nextCalled, context);
    }

    [Fact]
    public async Task InvokeAsync_VersionBelowFloor_Returns426WithUpgradeBody()
    {
        var (nextCalled, context) = await InvokeWithVersion("0.9.0");

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var root = document.RootElement;
        root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.UpgradeRequired);
        root.GetProperty("upgradeRequired").GetBoolean().Should().BeTrue();
        root.GetProperty("minVersion").GetString().Should().Be(FloorVersion);
        root.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InvokeAsync_VersionEqualToFloor_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion("1.0.0");

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_VersionAboveFloor_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion("1.2.0");

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_MissingHeader_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion(clientVersion: null);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_BlankHeader_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion("   ");

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_UnparseableHeader_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion("not-a-version");

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_IgnoresPrereleaseSuffixWhenComparing()
    {
        var (nextCalled, _) = await InvokeWithVersion("1.0.0-beta");

        nextCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("vNext")]
    public async Task InvokeAsync_NonComparableHeader_SkipsConfigReadBeforeAuth(string? clientVersion)
    {
        var configService = BuildConfigService(FloorVersion);
        var context = BuildContext(clientVersion);

        var nextCalled = await InvokeMiddleware(context, configService);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await configService.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("1.2.0")]
    [InlineData("2.0.0-rc1")]
    public async Task InvokeAsync_ComparableHeader_ReadsConfigExactlyOnce(string clientVersion)
    {
        var configService = BuildConfigService(FloorVersion);
        var context = BuildContext(clientVersion);

        await InvokeMiddleware(context, configService);

        await configService.Received(1).GetAsync(
            AppConfigKeys.MinSupportedVersion, "0.0.0", Arg.Any<CancellationToken>());
    }
}
