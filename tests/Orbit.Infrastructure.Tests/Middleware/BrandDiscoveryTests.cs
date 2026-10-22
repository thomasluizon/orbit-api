using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Middleware;

public class BrandDiscoveryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("/favicon.ico", "image/x-icon")]
    [InlineData("/favicon.svg", "image/svg+xml")]
    [InlineData("/apple-touch-icon.png", "image/png")]
    [InlineData("/orbit-icon-512.png", "image/png")]
    public async Task IconRoute_ServesAnonymousImageBeforeAuthenticationAndRateLimits(string path, string contentType)
    {
        await using var app = await CreateApplication();
        var invoke = ((IApplicationBuilder)app).Build();
        var context = CreateRequest(app, path);
        context.Request.Headers["X-App-Version"] = "0.0.1";

        await invoke(context);

        context.Response.StatusCode.Should().Be(200);
        context.Response.ContentType.Should().Be(contentType);
        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=604800");
        context.Response.Headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        bytes.Should().NotBeEmpty();
        if (contentType == "image/png")
        {
            bytes.Take(8).Should().Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var size = path == "/orbit-icon-512.png" ? 512 : 180;
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)).Should().Be(size);
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)).Should().Be(size);
        }
        else if (contentType == "image/x-icon")
            bytes.Take(6).Should().Equal(new byte[] { 0, 0, 1, 0, 3, 0 });
        else
            Encoding.UTF8.GetString(bytes).Should().Contain("<svg").And.Contain("orbit-title");
    }

    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("/favicon.svg")]
    [InlineData("/apple-touch-icon.png")]
    [InlineData("/orbit-icon-512.png")]
    public async Task IconHead_ReturnsHeadersWithoutABody(string path)
    {
        await using var app = await CreateApplication();
        var context = CreateRequest(app, path);
        context.Request.Method = "HEAD";
        await ((IApplicationBuilder)app).Build()(context);
        context.Response.StatusCode.Should().Be(200);
        context.Response.ContentLength.Should().BeGreaterThan(0);
        context.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=604800");
        context.Response.Body.Length.Should().Be(0);
    }

    [Theory]
    [InlineData("GET", "/favicon.svg/extra")]
    [InlineData("GET", "/appsettings.json")]
    [InlineData("POST", "/favicon.ico")]
    public async Task OtherRequests_StillReachTheRejectingRateLimiter(string method, string path)
    {
        await using var app = await CreateApplication();
        var context = CreateRequest(app, path);
        context.Request.Method = method;
        await ((IApplicationBuilder)app).Build()(context);
        context.Response.StatusCode.Should().Be(429);
        context.Response.Headers.CacheControl.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task SvgRoute_AllowsOnlyItsExactEmbeddedStylesheet()
    {
        await using var app = await CreateApplication();
        var context = CreateRequest(app, "/favicon.svg");
        await ((IApplicationBuilder)app).Build()(context);
        var svg = XDocument.Parse(Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        var style = svg.Descendants(XName.Get("style", "http://www.w3.org/2000/svg")).Single().Value;
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(style)));
        context.Response.Headers["Content-Security-Policy"].ToString()
            .Should().Be($"default-src 'none'; style-src 'sha256-{hash}'; frame-ancestors 'none'");
    }

    [Fact]
    public async Task GoogleMarkRoute_ServesTheSharedFourColourPaths()
    {
        await using var app = await CreateApplication();
        var context = CreateRequest(app, "/oauth/assets/google-mark.svg");
        await ((IApplicationBuilder)app).Build()(context);
        context.Response.StatusCode.Should().Be(200);
        context.Response.ContentType.Should().Be("image/svg+xml");
        var svg = XDocument.Parse(Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        var root = svg.Root!;
        root.Attribute("viewBox")!.Value.Should().Be("0 0 48 48");
        root.Attribute("width")!.Value.Should().Be("20");
        root.Attribute("height")!.Value.Should().Be("20");
        root.Elements().Select(path => path.Attribute("fill")!.Value)
            .Should().Equal("#EA4335", "#4285F4", "#FBBC05", "#34A853");
        root.Elements().Should().OnlyContain(path => path.Name.LocalName == "path");
    }

    [Fact]
    public async Task Initialize_AdvertisesOrbitMetadataThroughTheRealSdkResponse()
    {
        await using var app = await CreateApplication(blockDownstream: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = CreateRequest(app, "/mcp");
        context.RequestAborted = timeout.Token;
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"brand-test","version":"1"}}}
            """));

        await ((IApplicationBuilder)app).Build()(context);

        context.Response.StatusCode.Should().Be(200);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        var data = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        output.WriteLine(data);
        using var response = JsonDocument.Parse(data);
        var info = response.RootElement.GetProperty("result").GetProperty("serverInfo");
        info.GetProperty("title").GetString().Should().Be("Orbit");
        info.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        info.GetProperty("websiteUrl").GetString().Should().Be("https://useorbit.org");
        var icons = info.GetProperty("icons").EnumerateArray().ToArray();
        icons.Should().HaveCount(2);
        icons[0].GetProperty("src").GetString().Should().Be("https://api.useorbit.org/orbit-icon-512.png");
        icons[0].GetProperty("mimeType").GetString().Should().Be("image/png");
        icons[0].GetProperty("sizes")[0].GetString().Should().Be("512x512");
        icons[1].GetProperty("src").GetString().Should().Be("https://api.useorbit.org/favicon.svg");
        icons[1].GetProperty("mimeType").GetString().Should().Be("image/svg+xml");
        icons[1].GetProperty("sizes")[0].GetString().Should().Be("any");
        foreach (var icon in icons)
        {
            var image = CreateRequest(app, new Uri(icon.GetProperty("src").GetString()!).AbsolutePath);
            await ((IApplicationBuilder)app).Build()(image);
            image.Response.StatusCode.Should().Be(200);
            image.Response.ContentType.Should().Be(icon.GetProperty("mimeType").GetString());
        }
    }

    private static DefaultHttpContext CreateRequest(WebApplication app, string path)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.useorbit.org");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<WebApplication> CreateApplication(bool blockDownstream = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration["Database:MigrateOnStartup"] = "false";
        builder.Services.AddResponseCompression();
        builder.Services.AddExceptionHandler(options => options.ExceptionHandler = _ => Task.CompletedTask);
        builder.Services.AddCors(options => options.AddPolicy("ThirdParty", policy => policy.WithOrigins("https://claude.ai")));
        builder.Services.AddRateLimiter(options =>
        {
            if (!blockDownstream) return;
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetFixedWindowLimiter("all", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 1,
                    Window = TimeSpan.FromHours(1),
                    AutoReplenishment = false
                }));
            options.GlobalLimiter.AttemptAcquire(new DefaultHttpContext()).Dispose();
        });
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(options =>
        {
            if (blockDownstream)
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        builder.Services.AddControllers();
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddMcpToolServer(builder);
        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(Substitute.For<IAppConfigService>());
        var app = builder.Build();
        await app.ConfigureOrbitPipeline();
        app.UseEndpoints(_ => { });
        app.Run(context => { context.Response.StatusCode = 404; return Task.CompletedTask; });
        return app;
    }
}
