using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Infrastructure.Services;
using FluentAssertions;
using Orbit.Infrastructure.BackgroundJobs;

namespace Orbit.Infrastructure.Tests.BackgroundJobs;

public class ScheduledJobRunnerTests
{
    [Fact]
    public async Task RunAsync_DispatchesToJobMatchingName()
    {
        var first = new FakeScheduledJob("alpha");
        var second = new FakeScheduledJob("beta");
        var runner = CreateRunner([first, second]);

        await runner.RunAsync("beta", CancellationToken.None);

        second.RunCount.Should().Be(1);
        first.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_UnknownName_Throws()
    {
        var runner = CreateRunner([new FakeScheduledJob("alpha")]);

        var act = () => runner.RunAsync("missing", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing*");
    }

    [Fact]
    public async Task RunAsync_PassesCancellableDeadlineToken()
    {
        var job = new FakeScheduledJob("alpha");
        var runner = CreateRunner([job]);
        using var cts = new CancellationTokenSource();

        await runner.RunAsync("alpha", cts.Token);

        job.ObservedToken.CanBeCanceled.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ActiveOccurrence_SecondTriggerSkips()
    {
        var job = new BlockingJob();
        var runner = CreateRunner([job]);
        var otherScope = CreateRunner([job], SharedLeases);
        var first = runner.RunAsync(job.Name, default);
        await job.Started.Task;
        try
        {
            var second = otherScope.RunAsync(job.Name, default);
            await Task.WhenAny(second, Task.Delay(100));
            job.RunCount.Should().Be(1);
            second.IsCompletedSuccessfully.Should().BeTrue();
        }
        finally
        {
            job.Release.TrySetResult();
            await first;
        }
    }

    private static readonly MemoryLeases SharedLeases = new();

    private static ScheduledJobRunner CreateRunner(IScheduledJob[] jobs, MemoryLeases? leases = null)
    {
        var scopeFactory = NSubstitute.Substitute.For<IServiceScopeFactory>();
        var health = new BackgroundServiceHealthCheck(scopeFactory, jobs, TimeProvider.System,
            NullLogger<BackgroundServiceHealthCheck>.Instance);
        return new(jobs, leases ?? SharedLeases, health, new ConfigurationBuilder().Build());
    }

    private sealed class MemoryLeases : IScheduledJobLeaseProvider
    {
        private readonly ConcurrentDictionary<string, byte> _held = new();
        public Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable?>(_held.TryAdd(name, 0) ? new Lease(() => _held.TryRemove(name, out _)) : null);
        private sealed class Lease(Action release) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class BlockingJob : IScheduledJob
    {
        public string Name => "blocking";
        public string CronExpression => "* * * * *";
        public int RunCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            RunCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeScheduledJob(string name) : IScheduledJob
    {
        public string Name => name;
        public string CronExpression => "* * * * *";
        public int RunCount { get; private set; }
        public CancellationToken ObservedToken { get; private set; }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            RunCount++;
            ObservedToken = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
