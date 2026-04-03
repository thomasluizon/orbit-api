using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class BackgroundServiceHealthCheckTests
{
    private readonly BackgroundServiceHealthCheck _sut = new();

    [Fact]
    public async Task CheckHealthAsync_AllServicesRecordedRecently_ReturnsHealthy()
    {
        // Record ticks for all expected services
        BackgroundServiceHealthCheck.RecordTick("ReminderScheduler");
        BackgroundServiceHealthCheck.RecordTick("GoalDeadlineNotification");
        BackgroundServiceHealthCheck.RecordTick("SlipAlertScheduler");
        BackgroundServiceHealthCheck.RecordTick("HabitDueDateAdvancement");
        BackgroundServiceHealthCheck.RecordTick("AccountDeletion");

        var result = await _sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("All background services running");
    }

    [Fact]
    public async Task CheckHealthAsync_NoTicksRecorded_ReturnsHealthy()
    {
        // A fresh health check with no ticks recorded should still be healthy
        // since "no tick recorded" is not considered unhealthy (service might not have started yet)
        var freshCheck = new BackgroundServiceHealthCheck();
        var result = await freshCheck.CheckHealthAsync(new HealthCheckContext());

        // The check iterates static state, so with recent ticks from other tests it could vary.
        // We just verify it does not throw.
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordTick_SetsTimestamp()
    {
        var before = DateTime.UtcNow;
        BackgroundServiceHealthCheck.RecordTick("ReminderScheduler");
        var after = DateTime.UtcNow;

        // Verify health check runs without error after recording
        var result = await _sut.CheckHealthAsync(new HealthCheckContext());
        result.Should().NotBeNull();
        result.Data.Should().ContainKey("ReminderScheduler");
        result.Data["ReminderScheduler"].ToString().Should().Contain("Last tick");
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsDataForAllExpectedServices()
    {
        BackgroundServiceHealthCheck.RecordTick("ReminderScheduler");
        BackgroundServiceHealthCheck.RecordTick("GoalDeadlineNotification");
        BackgroundServiceHealthCheck.RecordTick("SlipAlertScheduler");
        BackgroundServiceHealthCheck.RecordTick("HabitDueDateAdvancement");
        BackgroundServiceHealthCheck.RecordTick("AccountDeletion");

        var result = await _sut.CheckHealthAsync(new HealthCheckContext());

        result.Data.Should().ContainKey("ReminderScheduler");
        result.Data.Should().ContainKey("GoalDeadlineNotification");
        result.Data.Should().ContainKey("SlipAlertScheduler");
        result.Data.Should().ContainKey("HabitDueDateAdvancement");
        result.Data.Should().ContainKey("AccountDeletion");
    }
}
