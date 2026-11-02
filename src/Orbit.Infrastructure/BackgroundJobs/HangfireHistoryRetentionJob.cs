using Hangfire;
using Hangfire.States;
using Microsoft.Extensions.Configuration;

namespace Orbit.Infrastructure.BackgroundJobs;

public sealed class HangfireHistoryRetentionJob(JobStorage storage, IBackgroundJobClient jobs, TimeProvider clock,
    IConfiguration configuration) : IScheduledJob
{
    public string Name => "hangfire-history-retention";
    public string CronExpression => "45 3 * * *";

    public Task RunAsync(CancellationToken cancellationToken)
    {
        var monitor = storage.GetMonitoringApi();
        var cutoffAtUtc = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = monitor.FailedJobs(offset, 500);
            var removed = 0;
            foreach (var pair in page.Where(pair => pair.Value.FailedAt < cutoffAtUtc))
            {
                if (jobs.ChangeState(pair.Key, new DeletedState(), FailedState.StateName))
                {
                    Expire(pair.Key, DeletedState.StateName);
                    removed++;
                }
            }
            offset += page.Count - removed;
            if (page.Count < 500) break;
        }
        TrimFinished(SucceededState.StateName, configuration.GetValue("BackgroundServices:SucceededListSize", 1000),
            (from, count) => monitor.SucceededJobs(from, count).Select(pair => pair.Key).ToList(), cancellationToken);
        TrimFinished(DeletedState.StateName, configuration.GetValue("BackgroundServices:DeletedListSize", 1000),
            (from, count) => monitor.DeletedJobs(from, count).Select(pair => pair.Key).ToList(), cancellationToken);
        return Task.CompletedTask;
    }

    private void TrimFinished(string state, int limit, Func<int, int, List<string>> readPage, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var offset = limit;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var ids = readPage(offset, 500);
            foreach (var id in ids) Expire(id, state);
            offset += ids.Count;
            if (ids.Count < 500) return;
        }
    }

    private void Expire(string id, string expectedState)
    {
        using var connection = storage.GetConnection();
        using var stateLock = connection.AcquireDistributedLock($"job:{id}:state-lock", TimeSpan.FromSeconds(10));
        if (connection.GetStateData(id)?.Name != expectedState) return;
        using var transaction = connection.CreateWriteTransaction();
        transaction.ExpireJob(id, TimeSpan.Zero);
        transaction.Commit();
    }
}
