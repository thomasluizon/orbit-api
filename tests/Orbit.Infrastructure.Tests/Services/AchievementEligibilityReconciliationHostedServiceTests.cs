using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Gamification.Backfill;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AchievementEligibilityReconciliationHostedServiceTests : IDisposable
{
    private readonly IAchievementEligibilityReconciliationService _reconciliationService =
        Substitute.For<IAchievementEligibilityReconciliationService>();
    private readonly RecordingLogger _logger = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;

    public AchievementEligibilityReconciliationHostedServiceTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _reconciliationService);
        _provider = services.BuildServiceProvider();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(new AchievementEligibilityReconciliationResult(2, 5));
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task RunReconciliationAsync_CompletedSweep_LogsBothCounts()
    {
        var completed = await CreateSut().RunReconciliationAsync(CancellationToken.None);

        completed.Should().Be(ReconciliationRunStatus.Complete);
        await _reconciliationService.Received(1).ReconcileAllAsync(Arg.Any<CancellationToken>());
        var completion = _logger.Entries.Should()
            .ContainSingle(entry => entry.Level == LogLevel.Information && entry.EventId == 1).Subject;
        completion.Message.Should().Contain("5").And.Contain("2");
    }

    [Fact]
    public async Task RunReconciliationAsync_AfterCompletedSweep_RunsAgain()
    {
        var sut = CreateSut();

        await sut.RunReconciliationAsync(CancellationToken.None);
        await sut.RunReconciliationAsync(CancellationToken.None);

        await _reconciliationService.Received(2).ReconcileAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunReconciliationAsync_SweepFails_LogsError()
    {
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("reconciliation failed"));

        var completed = await CreateSut().RunReconciliationAsync(CancellationToken.None);

        completed.Should().Be(ReconciliationRunStatus.Failed);
        _logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.EventId == 3);
    }

    [Fact]
    public async Task StartAsync_DoesNotWaitForFirstSweep()
    {
        var sweepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSweep = new TaskCompletionSource<AchievementEligibilityReconciliationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                sweepStarted.TrySetResult();
                return releaseSweep.Task;
            });
        var sut = CreateSut(TimeSpan.FromMilliseconds(10));

        await sut.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await sweepStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        sut.WorkerTask.Should().NotBeNull();
        sut.WorkerTask!.IsCompleted.Should().BeFalse();
        releaseSweep.TrySetResult(new AchievementEligibilityReconciliationResult(0, 0));
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunReconciliationAsync_DeferredAccounts_ReturnsDeferred()
    {
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(new AchievementEligibilityReconciliationResult(1, 2, 3));

        var result = await CreateSut().RunReconciliationAsync(CancellationToken.None);

        result.Should().Be(ReconciliationRunStatus.Deferred);
        _logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Debug && entry.EventId == 2)
            .Which.Message.Should().Contain("3");
    }

    [Fact]
    public async Task RunReconciliationAsync_MoreCandidates_ReturnsPending()
    {
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(new AchievementEligibilityReconciliationResult(2, 5, HasMoreCandidates: true));

        var result = await CreateSut().RunReconciliationAsync(CancellationToken.None);

        result.Should().Be(ReconciliationRunStatus.Pending);
        _logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Information && entry.EventId == 1);
    }

    [Fact]
    public async Task StartAsync_DeferredAccounts_RetriesInBackground()
    {
        var attempts = 0;
        var secondPassCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reconciliationService.ReconcileAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                if (attempts >= 2)
                    secondPassCompleted.TrySetResult();
                return Task.FromResult(attempts == 1
                    ? new AchievementEligibilityReconciliationResult(0, 0, 1)
                    : new AchievementEligibilityReconciliationResult(1, 2));
            });
        var sut = CreateSut(TimeSpan.FromMilliseconds(10));

        await sut.StartAsync(CancellationToken.None);
        await secondPassCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await sut.StopAsync(CancellationToken.None);

        attempts.Should().BeGreaterThanOrEqualTo(2);
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Debug && entry.EventId == 2);
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Information && entry.EventId == 1);
    }

    private AchievementEligibilityReconciliationHostedService CreateSut(TimeSpan? retryDelay = null) =>
        new(_scopeFactory, _logger)
        {
            RetryDelay = retryDelay ?? TimeSpan.Zero
        };

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
