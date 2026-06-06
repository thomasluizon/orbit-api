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
        var freshCheck = new BackgroundServiceHealthCheck();
        var result = await freshCheck.CheckHealthAsync(new HealthCheckContext());

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordTick_SetsTimestamp()
    {
        var before = DateTime.UtcNow;
        BackgroundServiceHealthCheck.RecordTick("ReminderScheduler");
        var after = DateTime.UtcNow;

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
