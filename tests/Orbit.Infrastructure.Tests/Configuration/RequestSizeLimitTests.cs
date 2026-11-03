using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Uploads.Common;
using OrbitServiceCollectionExtensions = Orbit.Api.Extensions.ServiceCollectionExtensions;

namespace Orbit.Infrastructure.Tests.Configuration;

/// <summary>
/// Guards the request-body size ceiling the API registers with Kestrel and its
/// relationship to the stricter per-upload content-type limit. A regression that
/// drops the transport ceiling or lets it fall to/below the upload max must fail here.
/// </summary>
public class RequestSizeLimitTests
{
    [Theory]
    [InlineData(typeof(Orbit.Api.Controllers.SubscriptionController), "HandleWebhook", 1048576)]
    [InlineData(typeof(Orbit.Api.Controllers.SubscriptionController), "HandlePlayNotification", 1048576)]
    [InlineData(typeof(Orbit.Api.Controllers.SubscriptionController), "HandleAppStoreNotification", 1048576)]
    [InlineData(typeof(Orbit.Api.Controllers.EmailController), "SesEvents", 1048576)]
    [InlineData(typeof(Orbit.Api.Controllers.ConnectionsController), "CompleteNotion", 65536)]
    public void ProviderRoute_DeclaresItsBodyLimit(Type controller, string method, long expected)
    {
        var attribute = controller.GetMethod(method)!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), true)
            .Cast<Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute>().SingleOrDefault();
        attribute.Should().NotBeNull();
        ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)attribute!).MaxRequestBodySize.Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RouteLimit_RejectsOnlyOversizedBodiesAndRestoresTheStream(bool declared)
    {
        foreach (var delta in new[] { 0, 1 })
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            http.Request.Path = "/mcp";
            var body = new MemoryStream(new byte[1048576 + delta]);
            http.Request.Body = body;
            if (declared) http.Request.ContentLength = body.Length;
            var called = false;
            var middleware = new Orbit.Api.Middleware.RouteBodyLimitMiddleware(async context =>
            {
                await context.Request.Body.CopyToAsync(Stream.Null);
                called = true;
            }, Microsoft.Extensions.Logging.Abstractions.NullLogger<Orbit.Api.Middleware.RouteBodyLimitMiddleware>.Instance);
            await middleware.InvokeAsync(http);
            http.Response.StatusCode.Should().Be(delta == 0 ? 200 : 413);
            called.Should().Be(delta == 0);
            http.Request.Body.Should().BeSameAs(body);
            body.Position.Should().BeLessThanOrEqualTo(1048577);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RouteLimit_LogsOnlyTheServerRouteForAnUntrustedPath(bool routed, bool declared)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = (routed ? "/api/provider/" : "/mcp/") + "attacker\r\nforged-warning\u0085\u2028\u2029";
        var body = new MemoryStream(new byte[1048577]);
        http.Request.Body = body;
        if (declared) http.Request.ContentLength = body.Length;
        if (routed)
            http.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
                RoutePatternFactory.Parse("/api/provider/{id}"), 0,
                new EndpointMetadataCollection(new RequestSizeLimitAttribute(1048576)), "Provider"));
        var logger = new CapturingLogger();
        var middleware = new Orbit.Api.Middleware.RouteBodyLimitMiddleware(
            context => context.Request.Body.CopyToAsync(Stream.Null), logger);

        await middleware.InvokeAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        http.Request.Body.Should().BeSameAs(body);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Fields["Route"].Should().Be(routed ? "/api/provider/{id}" : "/mcp");
        entry.Fields["DeclaredLength"].Should().Be(declared ? body.Length : null);
        entry.Fields["Limit"].Should().Be(1048576L);
        entry.Message.Should().NotContainAny("attacker", "forged-warning", "\r", "\n", "\u0085", "\u2028", "\u2029");
    }

    private sealed class CapturingLogger : ILogger<Orbit.Api.Middleware.RouteBodyLimitMiddleware>
    {
        public List<(LogLevel Level, string Message, Dictionary<string, object?> Fields)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), state is IEnumerable<KeyValuePair<string, object?>> fields
                ? fields.ToDictionary() : throw new InvalidOperationException("Expected structured log fields.")));
    }

    private const long ExpectedMaxRequestBodyBytes = 10 * 1024 * 1024;

    private static long RegisteredMaxRequestBodyBytes()
    {
        var builder = WebApplication.CreateBuilder();

        OrbitServiceCollectionExtensions.AddCookieAndKestrelLimits(builder);

        using var provider = builder.Services.BuildServiceProvider();
        var configuredLimit = provider
            .GetRequiredService<IOptions<KestrelServerOptions>>()
            .Value.Limits.MaxRequestBodySize;

        return configuredLimit
            ?? throw new InvalidOperationException("Kestrel MaxRequestBodySize was left unbounded.");
    }

    [Fact]
    public void AddCookieAndKestrelLimits_RegistersTenMegabyteRequestBodyCeiling()
    {
        RegisteredMaxRequestBodyBytes().Should().Be(ExpectedMaxRequestBodyBytes);
    }

    [Fact]
    public void RegisteredRequestBodyCeiling_LeavesHeadroomAboveUploadContentTypeMax()
    {
        RegisteredMaxRequestBodyBytes().Should().BeGreaterThan(
            UploadContentTypes.MaxSizeBytes,
            "the request-body ceiling must sit above the upload content-type max so an oversize upload is rejected by the validator, not truncated by the transport");
    }
}
