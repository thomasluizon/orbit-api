using System.Data.Common;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Tests.AI;
using NSubstitute;
using Orbit.Api.Authentication;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class BackgroundJobFailureTests
{
    [Fact]
    public async Task LegacyApiKeyInventory_CountThrows_LogsAndStarts()
    {
        var repository = Substitute.For<IGenericRepository<ApiKey>>();
        repository.CountAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("count failed")));
        using var provider = new ServiceCollection().AddSingleton(repository).BuildServiceProvider();
        var logger = Substitute.For<ILogger<LegacyApiKeyInventory>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var service = new LegacyApiKeyInventory(provider.GetRequiredService<IServiceScopeFactory>(), logger);

        var start = () => service.StartAsync(default);

        await start.Should().NotThrowAsync();
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning);
    }

    [Fact]
    public async Task PlayCleanup_PlayPurgeFails_StripePurgeStillRuns()
    {
        using var factory = new SqliteOrbitDbContextFactory(new FailPlayDelete());
        using (var db = factory.CreateContext())
        {
            var old = ProcessedStripeEvent.Create("old");
            typeof(ProcessedExternalEvent).GetProperty(nameof(ProcessedExternalEvent.ProcessedAtUtc))!
                .SetValue(old, TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-91));
            db.Add(old);
            await db.SaveChangesAsync();
        }
        using var provider = new ServiceCollection().AddScoped(_ => factory.CreateContext()).BuildServiceProvider();
        var logger = Substitute.For<ILogger<PlayNotificationCleanupService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var service = new PlayNotificationCleanupService(provider.GetRequiredService<IServiceScopeFactory>(), logger);

        var run = () => service.RunAsync(default);

        await run.Should().NotThrowAsync();
        using var read = factory.CreateContext();
        (await read.ProcessedStripeEvents.CountAsync()).Should().Be(0);
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Error);
    }

    [Fact]
    public async Task PlayCleanup_LoopLogsAndRunsAgain()
    {
        var failures = new FailPlayDelete();
        using var factory = new SqliteOrbitDbContextFactory(failures);
        using var provider = new ServiceCollection().AddScoped(_ => factory.CreateContext()).BuildServiceProvider();
        var logger = Substitute.For<ILogger<PlayNotificationCleanupService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var clock = new CompletionTestClock();
        using var service = new PlayNotificationCleanupService(provider.GetRequiredService<IServiceScopeFactory>(), logger, clock);
        await service.StartAsync(default);
        try
        {
            for (var attempt = 0; attempt < 100 && clock.ActiveTimers == 0; attempt++) await Task.Delay(10);
            clock.ActiveTimers.Should().Be(1);
            clock.Advance(TimeSpan.FromHours(24));
            await failures.SecondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await service.StopAsync(default); }
        logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Error).Should().Be(2);
    }

    [Fact]
    public async Task AiUsageStartup_SummaryThrows_LogsAndContinues()
    {
        var scopes = Substitute.For<IServiceScopeFactory>();
        scopes.CreateScope().Returns(_ => throw new InvalidOperationException("summary read failed"));
        var logger = Substitute.For<ILogger<AiUsageSummaryStartupService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        using var summary = new AiUsageSummaryService(scopes, Substitute.For<ILogger<AiUsageSummaryService>>(),
            new ConfigurationBuilder().Build(), Options.Create(new AiSettings()));
        using var startup = new AiUsageSummaryStartupService(summary, logger);
        await startup.StartAsync(default);
        await startup.ExecuteTask!;
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning);
    }

    private sealed class FailPlayDelete : DbCommandInterceptor
    {
        private int _attempts;
        internal TaskCompletionSource SecondAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)
                && command.CommandText.Contains("ProcessedPlayNotifications", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _attempts) == 2) SecondAttempt.TrySetResult();
                throw new InvalidOperationException("Play delete failed");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
