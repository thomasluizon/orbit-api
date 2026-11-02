using FluentAssertions;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Orbit.Infrastructure.BackgroundJobs;

namespace Orbit.Infrastructure.Tests.BackgroundJobs;

public sealed class HangfireHistoryRetentionJobTests
{
    private static readonly DateTime NowAtUtc = new(2030, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Run_ThirtyOneAndTwentyNineDayFailures_OnlyOldFailureExpires()
    {
        var storage = Substitute.For<JobStorage>();
        var monitor = Substitute.For<IMonitoringApi>();
        var connection = Substitute.For<IStorageConnection>();
        var transaction = Substitute.For<IWriteOnlyTransaction>();
        var jobs = Substitute.For<IBackgroundJobClient>();
        storage.GetMonitoringApi().Returns(monitor);
        storage.GetConnection().Returns(connection);
        connection.CreateWriteTransaction().Returns(transaction);
        connection.GetStateData("old").Returns(new StateData { Name = DeletedState.StateName });
        jobs.ChangeState("old", Arg.Any<DeletedState>(), FailedState.StateName).Returns(true);
        monitor.FailedJobs(0, 500).Returns(new JobList<FailedJobDto>([
            new("old", new FailedJobDto { FailedAt = NowAtUtc.AddDays(-31) }),
            new("recent", new FailedJobDto { FailedAt = NowAtUtc.AddDays(-29) })]));
        monitor.SucceededJobs(1000, 500).Returns(new JobList<SucceededJobDto>([]));
        monitor.DeletedJobs(1000, 500).Returns(new JobList<DeletedJobDto>([]));
        var job = new HangfireHistoryRetentionJob(storage, jobs, new Clock(), new ConfigurationBuilder().Build());

        await job.RunAsync(default);

        jobs.Received(1).ChangeState("old", Arg.Any<DeletedState>(), FailedState.StateName);
        jobs.DidNotReceive().ChangeState("recent", Arg.Any<IState>(), Arg.Any<string>());
        transaction.Received(1).ExpireJob("old", TimeSpan.Zero);
        transaction.Received(1).Commit();
    }

    [Fact]
    public async Task Run_ListLimits_ExpiresOverflowOnlyWhileExpectedStateStillHolds()
    {
        var storage = Substitute.For<JobStorage>();
        var monitor = Substitute.For<IMonitoringApi>();
        var connection = Substitute.For<IStorageConnection>();
        var transaction = Substitute.For<IWriteOnlyTransaction>();
        storage.GetMonitoringApi().Returns(monitor);
        storage.GetConnection().Returns(connection);
        connection.CreateWriteTransaction().Returns(transaction);
        connection.GetStateData("overflow").Returns(new StateData { Name = SucceededState.StateName });
        connection.GetStateData("retried").Returns(new StateData { Name = EnqueuedState.StateName });
        monitor.FailedJobs(0, 500).Returns(new JobList<FailedJobDto>([]));
        monitor.SucceededJobs(2, 500).Returns(new JobList<SucceededJobDto>([
            new("overflow", new SucceededJobDto()), new("retried", new SucceededJobDto())]));
        monitor.DeletedJobs(3, 500).Returns(new JobList<DeletedJobDto>([]));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["BackgroundServices:SucceededListSize"] = "2", ["BackgroundServices:DeletedListSize"] = "3" }).Build();

        await new HangfireHistoryRetentionJob(storage, Substitute.For<IBackgroundJobClient>(), new Clock(), config).RunAsync(default);

        transaction.Received(1).ExpireJob("overflow", TimeSpan.Zero);
        transaction.DidNotReceive().ExpireJob("retried", Arg.Any<TimeSpan>());
        monitor.Received(1).SucceededJobs(2, 500);
        monitor.Received(1).DeletedJobs(3, 500);
    }

    [Fact]
    public async Task Run_Canceled_DoesNotReadStorage()
    {
        var storage = Substitute.For<JobStorage>();
        var job = new HangfireHistoryRetentionJob(storage, Substitute.For<IBackgroundJobClient>(), new Clock(), new ConfigurationBuilder().Build());
        var run = () => job.RunAsync(new CancellationToken(true));
        await run.Should().ThrowAsync<OperationCanceledException>();
        storage.DidNotReceive().GetConnection();
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(NowAtUtc); }
}
