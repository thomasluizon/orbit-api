using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class BackgroundServiceHealthCheckTests
{
    [Fact]
    public async Task NeverTicked_AfterExpectedInterval_ReturnsUnhealthyAndNamesJob()
    {
        using var db = new SqliteOrbitDbContextFactory();
        using var services = new ServiceCollection().AddScoped(_ => db.CreateContext()).BuildServiceProvider();
        var clock = new Clock();
        var check = Create(services, clock);
        await check.RegisterAsync(default);
        (await check.CheckHealthAsync(new())).Status.Should().Be(HealthStatus.Healthy);
        clock.Now = clock.Now.AddMinutes(4);
        var result = await check.CheckHealthAsync(new());
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("reminder-scheduler");
    }

    [Fact]
    public async Task TickWrittenByAnotherScope_IsSharedAndRegistrationDoesNotResetGrace()
    {
        using var db = new SqliteOrbitDbContextFactory();
        using var services = new ServiceCollection().AddScoped(_ => db.CreateContext()).BuildServiceProvider();
        var clock = new Clock();
        var first = Create(services, clock);
        await first.RegisterAsync(default);
        clock.Now = clock.Now.AddMinutes(4);
        var second = Create(services, clock);
        await second.RegisterAsync(default);
        (await second.CheckHealthAsync(new())).Status.Should().Be(HealthStatus.Unhealthy);
        await first.RecordTick("reminder-scheduler");
        (await second.CheckHealthAsync(new())).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task RecordTick_WriteFails_DoesNotFailTheJob()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(_ => throw new InvalidOperationException("database unavailable"));
        var check = new BackgroundServiceHealthCheck(scopeFactory, [], TimeProvider.System,
            NullLogger<BackgroundServiceHealthCheck>.Instance);
        await check.RecordTick("reminder-scheduler");
    }

    internal static BackgroundServiceHealthCheck Create(IServiceProvider services, TimeProvider? clock = null, params IScheduledJob[] jobs)
    {
        if (jobs.Length == 0)
        {
            var job = Substitute.For<IScheduledJob>();
            job.Name.Returns("reminder-scheduler");
            jobs = [job];
        }
        return new(services.GetRequiredService<IServiceScopeFactory>(), jobs, clock ?? TimeProvider.System,
            NullLogger<BackgroundServiceHealthCheck>.Instance);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 2, 1, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
