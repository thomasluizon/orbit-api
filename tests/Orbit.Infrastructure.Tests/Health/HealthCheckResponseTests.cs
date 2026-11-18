using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Health;

public class HealthCheckResponseTests
{
    private static readonly string[] CloverEndpointVersions = ["2026-02-25.clover"];

    private static HealthReportEntry Entry(
        HealthStatus status,
        string description,
        Exception? exception = null)
    {
        return new HealthReportEntry(status, description, TimeSpan.Zero, exception, data: null);
    }

    private static HealthReport Report(HealthStatus status, params (string Name, HealthReportEntry Entry)[] entries)
    {
        var map = entries.ToDictionary(e => e.Name, e => e.Entry);
        return new HealthReport(map, status, TimeSpan.Zero);
    }

    private static async Task<(int StatusCode, string ContentType, JsonDocument Body, string RawBody)> InvokeAsync(
        HealthReport report)
    {
        var context = new DefaultHttpContext();
        using var stream = new MemoryStream();
        context.Response.Body = stream;

        await WebApplicationExtensions.WriteHealthCheckResponseAsync(context, report);

        var raw = Encoding.UTF8.GetString(stream.ToArray());
        return (context.Response.StatusCode, context.Response.ContentType ?? string.Empty, JsonDocument.Parse(raw), raw);
    }

    [Fact]
    public async Task HealthEndpoints_StaleJobDoesNotFailRenderProbe_JobReportReturns503()
    {
        var jobCalls = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new System.Diagnostics.DiagnosticListener("HealthCheckTests"));
        services.AddRouting();
        services.AddHealthChecks()
            .AddCheck("database", () => HealthCheckResult.Healthy("Database reachable"))
            .AddCheck("background-services", () =>
            {
                jobCalls++;
                return HealthCheckResult.Unhealthy("Stale services: reminder-scheduler");
            }, tags: ["jobs"]);
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseRouting();
        app.UseEndpoints(endpoints => endpoints.MapOrbitHealthChecks());
        var pipeline = app.Build();
        var liveness = new DefaultHttpContext { RequestServices = provider };
        liveness.Request.Path = "/health";
        liveness.Request.Method = "GET";
        using var liveBody = new MemoryStream();
        liveness.Response.Body = liveBody;
        await pipeline(liveness);
        liveness.Response.StatusCode.Should().Be(200);
        jobCalls.Should().Be(0);
        using var liveJson = JsonDocument.Parse(liveBody.ToArray());
        liveJson.RootElement.TryGetProperty("commit", out _).Should().BeTrue();

        var jobs = new DefaultHttpContext { RequestServices = provider };
        jobs.Request.Path = "/health/jobs";
        jobs.Request.Method = "GET";
        using var jobBody = new MemoryStream();
        jobs.Response.Body = jobBody;
        await pipeline(jobs);
        jobs.Response.StatusCode.Should().Be(503);
        jobCalls.Should().Be(1);
        Encoding.UTF8.GetString(jobBody.ToArray()).Should().Contain("reminder-scheduler");
    }

    [Fact]
    public async Task HealthEndpoints_StripeVersionMismatchDoesNotFailRenderProbe_StripeReportReturns503WithVersions()
    {
        var stripeCalls = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new System.Diagnostics.DiagnosticListener("HealthCheckTests"));
        services.AddRouting();
        services.AddHealthChecks()
            .AddCheck("database", () => HealthCheckResult.Healthy("Database reachable"))
            .AddCheck("background-services", () => HealthCheckResult.Healthy(
                "All background services running",
                new Dictionary<string, object> { ["reminder-scheduler"] = "Last tick: 1m ago" }), tags: ["jobs"])
            .AddCheck(WebApplicationExtensions.StripeHealthCheckName, () =>
            {
                stripeCalls++;
                return HealthCheckResult.Unhealthy("No enabled webhook endpoint", data: new Dictionary<string, object>
                {
                    ["libraryApiVersion"] = "2026-08-26.dahlia",
                    ["endpointApiVersions"] = CloverEndpointVersions,
                });
            }, tags: [WebApplicationExtensions.StripeHealthTag]);
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseRouting();
        app.UseEndpoints(endpoints => endpoints.MapOrbitHealthChecks());
        var pipeline = app.Build();

        async Task<(int Status, JsonDocument Body)> GetAsync(string path)
        {
            var context = new DefaultHttpContext { RequestServices = provider };
            context.Request.Path = path;
            context.Request.Method = "GET";
            using var body = new MemoryStream();
            context.Response.Body = body;
            await pipeline(context);
            return (context.Response.StatusCode, JsonDocument.Parse(body.ToArray()));
        }

        var (liveStatus, liveBody) = await GetAsync("/health");
        liveStatus.Should().Be(200);
        stripeCalls.Should().Be(0);
        liveBody.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .Should().Equal("database");

        var (jobsStatus, jobsBody) = await GetAsync("/health/jobs");
        jobsStatus.Should().Be(200);
        jobsBody.RootElement.GetProperty("checks")[0].TryGetProperty("data", out _).Should().BeFalse();

        var (stripeStatus, stripeBody) = await GetAsync("/health/stripe");
        stripeStatus.Should().Be(503);
        stripeCalls.Should().Be(1);
        var stripe = stripeBody.RootElement.GetProperty("checks").EnumerateArray().Single();
        stripe.GetProperty("name").GetString().Should().Be(WebApplicationExtensions.StripeHealthCheckName);
        stripe.GetProperty("data").GetProperty("libraryApiVersion").GetString().Should().Be("2026-08-26.dahlia");
        stripe.GetProperty("data").GetProperty("endpointApiVersions")[0].GetString().Should().Be("2026-02-25.clover");
        stripeBody.RootElement.TryGetProperty("commit", out _).Should().BeTrue();
    }

    [Fact]
    public async Task HealthyReport_ExposesRenderCommit()
    {
        const string commit = "1234567890abcdef1234567890abcdef12345678";
        var previous = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT");
        try
        {
            Environment.SetEnvironmentVariable("RENDER_GIT_COMMIT", commit);
            var (_, _, body, _) = await InvokeAsync(Report(HealthStatus.Healthy));

            body.RootElement.GetProperty("commit").GetString().Should().Be(commit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RENDER_GIT_COMMIT", previous);
        }
    }

    [Fact]
    public async Task HealthyReport_Returns200WithJsonContentType()
    {
        var report = Report(
            HealthStatus.Healthy,
            ("database", Entry(HealthStatus.Healthy, "Database reachable")),
            ("background-services", Entry(HealthStatus.Healthy, "All background services running")));

        var (statusCode, contentType, body, _) = await InvokeAsync(report);

        statusCode.Should().Be(StatusCodes.Status200OK);
        contentType.Should().StartWith("application/json");
        body.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task HealthyReport_BodySurfacesEveryCheckNameStatusAndDescription()
    {
        var report = Report(
            HealthStatus.Healthy,
            ("database", Entry(HealthStatus.Healthy, "Database reachable")),
            ("background-services", Entry(HealthStatus.Healthy, "All background services running")));

        var (_, _, body, _) = await InvokeAsync(report);

        var checks = body.RootElement.GetProperty("checks");
        checks.GetArrayLength().Should().Be(2);

        var database = checks.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "database");
        database.GetProperty("status").GetString().Should().Be("Healthy");
        database.GetProperty("description").GetString().Should().Be("Database reachable");

        var background = checks.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "background-services");
        background.GetProperty("status").GetString().Should().Be("Healthy");
        background.GetProperty("description").GetString().Should().Be("All background services running");
    }

    [Fact]
    public async Task UnhealthyReport_Returns503AndSurfacesFailingCheck()
    {
        var report = Report(
            HealthStatus.Unhealthy,
            ("database", Entry(HealthStatus.Unhealthy, "Database unreachable")),
            ("background-services", Entry(HealthStatus.Healthy, "All background services running")));

        var (statusCode, _, body, _) = await InvokeAsync(report);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");

        var checks = body.RootElement.GetProperty("checks");
        var database = checks.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "database");
        database.GetProperty("status").GetString().Should().Be("Unhealthy");
        database.GetProperty("description").GetString().Should().Be("Database unreachable");
    }

    [Fact]
    public async Task DegradedReport_Returns503()
    {
        var report = Report(
            HealthStatus.Degraded,
            ("background-services", Entry(HealthStatus.Degraded, "Stale services: ReminderScheduler")));

        var (statusCode, _, body, _) = await InvokeAsync(report);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
    }

    [Fact]
    public async Task FailingCheckWithException_DoesNotLeakExceptionDetail()
    {
        Exception captured;
        try
        {
            throw new InvalidOperationException("Host=db.internal;Password=SUPERSECRET_TOKEN");
        }
        catch (InvalidOperationException ex)
        {
            captured = ex;
        }

        var report = Report(
            HealthStatus.Unhealthy,
            ("database", Entry(HealthStatus.Unhealthy, "Database unreachable", captured)));

        var (statusCode, _, _, raw) = await InvokeAsync(report);

        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        raw.Should().Contain("Database unreachable");
        raw.Should().NotContain("SUPERSECRET_TOKEN");
        raw.Should().NotContain("InvalidOperationException");
        raw.Should().NotContain("stackTrace");
    }

    [Fact]
    public async Task EmptyReport_Returns200WithNoChecks()
    {
        var report = Report(HealthStatus.Healthy);

        var (statusCode, _, body, _) = await InvokeAsync(report);

        statusCode.Should().Be(StatusCodes.Status200OK);
        body.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        body.RootElement.GetProperty("checks").GetArrayLength().Should().Be(0);
    }
}
