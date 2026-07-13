using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Health;

/// <summary>
/// The <c>/health</c> ResponseWriter is what a load balancer or uptime monitor
/// actually reads. These tests pin the two behaviours that matter operationally:
/// the HTTP status code maps a non-healthy report to 503 (so a degraded process
/// is never mistaken for live), and the JSON body surfaces each check's
/// name/status/description without ever serializing the raw exception object.
/// The writer is invoked directly against an in-memory <see cref="HealthReport"/>.
/// </summary>
public class HealthCheckResponseTests
{
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
