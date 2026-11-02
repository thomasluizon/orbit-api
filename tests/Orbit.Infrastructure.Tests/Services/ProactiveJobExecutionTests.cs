using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Proactive.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ProactiveJobExecutionTests
{
    [Fact]
    public async Task Evaluate_ProductionComposition_BoundsFourWorkersAndQueuesPushAfterCommit()
    {
        using var factory = new SqliteOrbitDbContextFactory(Path.Combine(Path.GetTempPath(), $"proactive-workers-{Guid.NewGuid():N}.db"));
        var today = new DateOnly(2030, 10, 14);
        using (var seed = factory.CreateContext())
        {
            for (var index = 0; index < 5; index++)
            {
                var user = User.Create($"User {index}", $"u{index}@example.com").Value;
                user.GrantLifetimePro();
                user.SetTimeZone("UTC");
                seed.Add(user);
                seed.Add(Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read", FrequencyUnit.Day, 1, today)).Value);
            }
            await seed.SaveChangesAsync();
        }
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(today);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUsersAsync(Arg.Any<IReadOnlyCollection<User>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<User>>().ToDictionary(user => user.Id, _ => (IReadOnlyList<string>)Array.Empty<string>()));
        var messages = Substitute.For<IProactiveCheckinMessageService>();
        var active = 0;
        var maximum = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messages.GenerateAsync(Arg.Any<ProactiveFacts>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var current = Interlocked.Increment(ref active);
                lock (release) maximum = Math.Max(maximum, current);
                if (current == 4) started.TrySetResult();
                try { await release.Task.WaitAsync(call.Arg<CancellationToken>()); }
                finally { Interlocked.Decrement(ref active); }
                return new ProactiveMessage("Read tonight.", "Read", "Help me read.");
            });
        var jobs = Substitute.For<IBackgroundJobClient>();
        var queued = 0;
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            var job = call.Arg<Job>();
            using var observer = factory.CreateContext();
            observer.ProactiveItems.Any(item => item.UserId == (Guid)job.Args[0] && item.PushedAtUtc != null).Should().BeTrue();
            Interlocked.Increment(ref queued);
            return Guid.NewGuid().ToString();
        });
        using var provider = new ServiceCollection().AddLogging().AddScoped(_ => factory.CreateContext())
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).AddSingleton(dates).AddSingleton(flags)
            .AddSingleton(messages).AddSingleton(jobs).AddSingleton(Substitute.For<ICalendarEventFetcher>())
            .AddSingleton(Substitute.For<IGoogleTokenService>()).AddSingleton(Substitute.For<IProductAnalytics>())
            .AddSingleton<TimeProvider>(new ProactiveEngineServiceTests.FixedClock(today.ToDateTime(new TimeOnly(19, 0), DateTimeKind.Utc)))
            .AddScoped<IProactiveEngine, ProactiveEngineService>().BuildServiceProvider();
        using var scope = provider.CreateScope();
        var run = scope.ServiceProvider.GetRequiredService<IProactiveEngine>().EvaluateAsync(default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            maximum.Should().Be(4);
        }
        finally { release.TrySetResult(); }
        await run;
        maximum.Should().Be(4);
        queued.Should().Be(5);
        using var read = factory.CreateContext();
        (await read.ProactiveItems.CountAsync()).Should().Be(5);
    }
}
