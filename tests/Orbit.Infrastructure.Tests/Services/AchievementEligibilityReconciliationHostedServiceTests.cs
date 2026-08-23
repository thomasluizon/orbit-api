using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AchievementEligibilityReconciliationHostedServiceTests : IDisposable
{
    private const string CompletionKey = "AchievementEligibilityReconciliationComplete";

    private readonly string _dbName = $"AchievementReconciliationHosted_{Guid.NewGuid()}";
    private readonly IAchievementEligibilityReconciliationService _reconciliationService =
        Substitute.For<IAchievementEligibilityReconciliationService>();
    private readonly RecordingLogger _logger = new();
    private readonly AchievementReconciliationState _reconciliationState = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;

    public AchievementEligibilityReconciliationHostedServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DatabaseConnectionSettings());
        services.AddScoped(_ => CreateContext(_dbName));
        services.AddScoped(_ => _reconciliationService);
        _provider = services.BuildServiceProvider();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(new AchievementEligibilityReconciliationResult(2, 5));
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task RunReconciliationAsync_CompletionMarkerExists_SkipsSweep()
    {
        await using (var seed = CreateContext(_dbName))
        {
            seed.AppConfigs.Add(AppConfig.Create(CompletionKey, "true"));
            await seed.SaveChangesAsync();
        }

        var result = await CreateSut().RunReconciliationAsync(CancellationToken.None);

        result.Should().Be(ReconciliationRunStatus.Complete);
        _reconciliationState.IsComplete.Should().BeTrue();
        await _reconciliationService.DidNotReceive().ReconcileAllAsync(Arg.Any<CancellationToken>());
        _logger.Entries.Should().ContainSingle()
            .Which.Should().Match<LogEntry>(entry => entry.Level == LogLevel.Information && entry.EventId == 1);
    }

    [Fact]
    public async Task RunReconciliationAsync_RunsSweepWritesMarkerAndLogsBothCounts()
    {
        var completed = await CreateSut().RunReconciliationAsync(CancellationToken.None);

        await using var verify = CreateContext(_dbName);
        completed.Should().Be(ReconciliationRunStatus.Complete);
        _reconciliationState.IsComplete.Should().BeTrue();
        (await verify.AppConfigs.CountAsync(config => config.Key == CompletionKey)).Should().Be(1);
        await _reconciliationService.Received(1).ReconcileAllAsync(Arg.Any<CancellationToken>());
        var completion = _logger.Entries.Should()
            .ContainSingle(entry => entry.Level == LogLevel.Information && entry.EventId == 2).Subject;
        completion.Message.Should().Contain("5").And.Contain("2");
    }

    [Fact]
    public async Task RunReconciliationAsync_SweepFails_LogsErrorAndLeavesMarkerUnset()
    {
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("reconciliation failed"));

        var completed = await CreateSut().RunReconciliationAsync(CancellationToken.None);

        completed.Should().Be(ReconciliationRunStatus.Failed);
        _reconciliationState.IsComplete.Should().BeFalse();
        await using var verify = CreateContext(_dbName);
        (await verify.AppConfigs.AnyAsync(config => config.Key == CompletionKey)).Should().BeFalse();
        _logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.EventId == 4);
    }

    [Fact]
    public async Task StartAsync_FirstSweepFails_RetriesAndCompletesBeforeReturning()
    {
        var attempts = 0;
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("transient failure");

                return Task.FromResult(new AchievementEligibilityReconciliationResult(2, 5));
            });
        var sut = CreateSut();

        await sut.StartAsync(CancellationToken.None);

        attempts.Should().Be(2);
        await using var verify = CreateContext(_dbName);
        (await verify.AppConfigs.CountAsync(config => config.Key == CompletionKey)).Should().Be(1);
        _reconciliationState.IsComplete.Should().BeTrue();
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && entry.EventId == 4);
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Information && entry.EventId == 2);
    }

    [Fact]
    public async Task RunReconciliationAsync_DeferredAccounts_LeavesMarkerAndStateUnset()
    {
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(new AchievementEligibilityReconciliationResult(1, 2, 3));

        var result = await CreateSut().RunReconciliationAsync(CancellationToken.None);

        result.Should().Be(ReconciliationRunStatus.Deferred);
        _reconciliationState.IsComplete.Should().BeFalse();
        await using var verify = CreateContext(_dbName);
        (await verify.AppConfigs.AnyAsync(config => config.Key == CompletionKey)).Should().BeFalse();
        _logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Debug && entry.EventId == 3)
            .Which.Message.Should().Contain("3");
    }

    [Fact]
    public async Task StartAsync_DeferredAccounts_RetriesInBackgroundAndCompletes()
    {
        var attempts = 0;
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new AchievementEligibilityReconciliationResult(0, 0, 1)
                    : new AchievementEligibilityReconciliationResult(1, 2));
            });
        var sut = CreateSut(TimeSpan.FromMilliseconds(10));

        await sut.StartAsync(CancellationToken.None);
        await sut.DeferredRetryTask!;

        attempts.Should().Be(2);
        _reconciliationState.IsComplete.Should().BeTrue();
        await using var verify = CreateContext(_dbName);
        (await verify.AppConfigs.CountAsync(config => config.Key == CompletionKey)).Should().Be(1);
        await sut.StopAsync(CancellationToken.None);
    }

    private AchievementEligibilityReconciliationHostedService CreateSut(TimeSpan? retryDelay = null) =>
        new(_scopeFactory, _reconciliationState, _logger)
        {
            RetryDelay = retryDelay ?? TimeSpan.Zero
        };

    private static OrbitDbContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(dbName).Options);

    private sealed record LogEntry(LogLevel Level, int EventId, string Message);

    private sealed class RecordingLogger : ILogger<AchievementEligibilityReconciliationHostedService>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
