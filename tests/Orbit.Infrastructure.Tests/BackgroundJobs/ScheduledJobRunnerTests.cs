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
        var runner = new ScheduledJobRunner([first, second]);

        await runner.RunAsync("beta", CancellationToken.None);

        second.RunCount.Should().Be(1);
        first.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_UnknownName_Throws()
    {
        var runner = new ScheduledJobRunner([new FakeScheduledJob("alpha")]);

        var act = () => runner.RunAsync("missing", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing*");
    }

    [Fact]
    public async Task RunAsync_ForwardsCancellationToken()
    {
        var job = new FakeScheduledJob("alpha");
        var runner = new ScheduledJobRunner([job]);
        using var cts = new CancellationTokenSource();

        await runner.RunAsync("alpha", cts.Token);

        job.ObservedToken.Should().Be(cts.Token);
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
