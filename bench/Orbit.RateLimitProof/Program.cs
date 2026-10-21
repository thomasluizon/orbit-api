using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.RateLimitProof;

try
{
    var settings = ProofSettings.Parse(
        Environment.GetEnvironmentVariable("ORBIT_RATE_LIMIT_PG17_CONNECTION_STRING"),
        Environment.GetEnvironmentVariable("ORBIT_RATE_LIMIT_PG17_EXPECTED_DATABASE"),
        Environment.GetEnvironmentVariable("ORBIT_RATE_LIMIT_PG17_DISPOSABLE"));
    Environment.ExitCode = await new PostgresProof(settings).RunAsync();
}
catch (Exception error)
{
    PostgresProof.PrintFailure("proof-terminated", error);
    Console.WriteLine("PG17_ASSERT failures=1; expected=0");
    Environment.ExitCode = 1;
}

internal sealed class PostgresProof(ProofSettings settings)
{
    private static readonly DateTimeOffset Initial = new(2026, 1, 15, 12, 30, 30, TimeSpan.Zero);
    private readonly ProofClock _clock = new(Initial);
    private readonly string _prefix = $"limiter-proof:{Guid.NewGuid():N}:";
    private int _failures;

    private DbContextOptions<OrbitDbContext> Options(params IInterceptor[] interceptors)
        => new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql(settings.ConnectionString, provider => provider.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
            .AddInterceptors(interceptors).Options;

    public async Task<int> RunAsync()
    {
        await VerifyIdentityAsync();
        try
        {
            await BurstAsync("cold-fixed", "auth", "cold-auth", 32, 10, 0);
            await BurstAsync("warm-fixed", "auth", "warm-auth", 16, 10, 8);
            await BurstAsync("cold-segmented", "chat", "cold-chat", 32, 20, 0);
            await BurstAsync("warm-segmented", "chat", "warm-chat", 16, 20, 18);
            await BurstAsync("boundary-segmented", "chat", "boundary-chat", 16, 20, 19, Initial.UtcDateTime.AddSeconds(-15));
            _clock.Set(Initial.AddSeconds(15));
            var boundary = await AcquireAsync("chat", "boundary-chat");
            Check("segment-boundary-does-not-reset-quota", !boundary.Allowed && boundary.CurrentCount == 20);
            _clock.Set(Initial.AddMinutes(1));
            var expired = await AcquireAsync("chat", "boundary-chat");
            Check("exact-expiry-reopens-quota", expired.Allowed && expired.CurrentCount == 1);
            _clock.Set(Initial);
            await IndependentAsync();
            await LeasesAsync();
            _clock.Set(Initial);
            await QueuedCallerAsync(false);
            await QueuedCallerAsync(true);
            await ReleasedAfterFailureAsync();
        }
        finally
        {
            await using var cleanup = new OrbitDbContext(Options());
            await cleanup.DistributedRateLimitBuckets.Where(bucket => bucket.PartitionKey.StartsWith(_prefix)).ExecuteDeleteAsync();
        }
        Console.WriteLine($"PG17_ASSERT failures={_failures}; expected=0");
        return _failures == 0 ? 0 : 1;
    }

    private async Task VerifyIdentityAsync()
    {
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var identity = new NpgsqlCommand("SELECT current_database(), current_setting('server_version_num')::integer", connection);
        await using var reader = await identity.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != settings.ExpectedDatabase || reader.GetInt32(1) / 10000 != 17)
            throw new InvalidOperationException("Expected disposable PostgreSQL 17 database identity was not confirmed.");
        Console.WriteLine("PG17_IDENTITY expected_database=true; major=17");
    }

    private async Task<DistributedRateLimitDecision> AcquireAsync(string policy, string suffix, CancellationToken token = default)
    {
        await using var context = new OrbitDbContext(Options());
        return await new DistributedRateLimitService(context, _clock).TryAcquireAsync(policy, _prefix + suffix, token);
    }

    private async Task SeedAsync(string policy, string partition, int count, DateTime? seedStart)
    {
        if (count == 0) return;
        await using var context = new OrbitDbContext(Options());
        var segment = policy == "auth" ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(15);
        var start = seedStart ?? new DateTime(Initial.UtcDateTime.Ticks / segment.Ticks * segment.Ticks, DateTimeKind.Utc);
        var bucket = DistributedRateLimitBucket.Create(policy, partition, start, start.AddMinutes(1));
        for (var i = 1; i < count; i++) bucket.Increment();
        context.DistributedRateLimitBuckets.Add(bucket);
        await context.SaveChangesAsync();
    }

    private async Task<AcquisitionSample[]> RunBurstAsync(string label, (string Policy, string Suffix)[] inputs)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = inputs.Select(async input =>
        {
            var attempts = new AcquisitionObserver();
            await using var observed = new OrbitDbContext(Options(attempts));
            await ready.Task;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await new DistributedRateLimitService(observed, _clock)
                    .TryAcquireAsync(input.Policy, _prefix + input.Suffix, deadline.Token);
                return new AcquisitionSample(result.Allowed, false, attempts.Starts, stopwatch.Elapsed);
            }
            catch (Exception error)
            {
                PrintFailure(label, error);
                return new AcquisitionSample(false, true, attempts.Starts, stopwatch.Elapsed);
            }
        }).ToArray();
        var total = Stopwatch.StartNew();
        ready.SetResult();
        var samples = await Task.WhenAll(calls);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Case = label,
            requests = inputs.Length,
            allowed = samples.Count(sample => sample.Allowed),
            denied = samples.Count(sample => !sample.Allowed && !sample.Error),
            errors = samples.Count(sample => sample.Error),
            maxAttempts = samples.Max(sample => sample.Attempts),
            timing = ProofTiming.Measure(total.Elapsed, samples.Select(sample => sample.Elapsed))
        }));
        Check(label + "-attempt-ceiling", samples.All(sample => sample.Attempts is >= 1 and <= 3));
        Check(label + "-no-terminal-errors", samples.All(sample => !sample.Error));
        return samples;
    }

    private async Task BurstAsync(string label, string policy, string suffix, int requests, int limit, int seedCount, DateTime? seedStart = null)
    {
        var partition = _prefix + suffix;
        await SeedAsync(policy, partition, seedCount, seedStart);
        var samples = await RunBurstAsync(label, Enumerable.Repeat((policy, suffix), requests).ToArray());
        var allowed = samples.Count(sample => sample.Allowed);
        await using var readback = new OrbitDbContext(Options());
        var buckets = await readback.DistributedRateLimitBuckets.AsNoTracking()
            .Where(bucket => bucket.PolicyName == policy && bucket.PartitionKey == partition).ToListAsync();
        var stored = buckets.Sum(bucket => bucket.Count);
        Console.WriteLine(JsonSerializer.Serialize(new { Case = label + "-readback", seedCount, stored, limit }));
        Check(label + "-no-overgrant", allowed <= limit - seedCount && stored <= limit);
        Check(label + "-one-count-per-allowed", stored == seedCount + allowed);
        Check(label + "-fills-available-quota", allowed == limit - seedCount);
        Check(label + "-unique-segments", buckets.GroupBy(bucket => bucket.WindowStartUtc).All(group => group.Count() == 1));
        var denied = await AcquireAsync(policy, suffix);
        Check(label + "-quota-exhausted", !denied.Allowed && denied.CurrentCount == limit && denied.WindowEndsAtUtc > _clock.GetUtcNow().UtcDateTime);
    }

    private async Task IndependentAsync()
    {
        var inputs = Enumerable.Range(0, 8).Select(i => ("auth", $"independent-{i}")).ToArray();
        var samples = await RunBurstAsync("independent-partitions", inputs);
        Check("independent-partitions-all-allowed", samples.All(sample => sample.Allowed));
        await using var context = new OrbitDbContext(Options());
        var buckets = await context.DistributedRateLimitBuckets.AsNoTracking()
            .Where(bucket => bucket.PartitionKey.StartsWith(_prefix + "independent-")).ToListAsync();
        Check("independent-partitions-readback", buckets.Count == 8 && buckets.All(bucket => bucket.Count == 1));
        var policyInputs = new[] { "auth", "refresh", "chat", "mcp", "mcp-ai", "support", "tags", "uploads" }
            .Select(policy => (policy, "mixed-policy")).ToArray();
        var mixed = await RunBurstAsync("independent-policies", policyInputs);
        Check("independent-policies-all-allowed", mixed.All(sample => sample.Allowed));
        var mixedBuckets = await context.DistributedRateLimitBuckets.AsNoTracking()
            .Where(bucket => bucket.PartitionKey == _prefix + "mixed-policy").ToListAsync();
        Check("independent-policies-readback", mixedBuckets.Count == policyInputs.Length && mixedBuckets.All(bucket => bucket.Count == 1));
        var samePartition = await AcquireAsync("refresh", "cold-auth");
        Check("independent-policy-on-exhausted-partition", samePartition.Allowed && samePartition.CurrentCount == 1);
    }

    private async Task LeasesAsync()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var calls = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = new OrbitDbContext(Options());
            await start.Task;
            return await new DistributedRateLimitService(context, _clock)
                .TryAcquireLeaseAsync("chat-in-flight", _prefix + "lease", 1, TimeSpan.FromMinutes(5), deadline.Token);
        }).ToArray();
        start.SetResult();
        var leases = await Task.WhenAll(calls);
        Check("lease-exclusive", leases.Count(lease => lease.Allowed) == 1);
        var oldLease = leases.Single(lease => lease.Allowed);
        _clock.Set(Initial.AddMinutes(5).AddSeconds(1));
        await using var context = new OrbitDbContext(Options());
        var service = new DistributedRateLimitService(context, _clock);
        var successor = await service.TryAcquireLeaseAsync("chat-in-flight", _prefix + "lease", 1, TimeSpan.FromMinutes(5));
        await service.ReleaseLeaseAsync("chat-in-flight", _prefix + "lease", oldLease.WindowEndsAtUtc);
        var blocked = await service.TryAcquireLeaseAsync("chat-in-flight", _prefix + "lease", 1, TimeSpan.FromMinutes(5));
        Check("stale-release-preserves-successor", successor.Allowed && !blocked.Allowed && blocked.WindowEndsAtUtc == successor.WindowEndsAtUtc);
        await service.ReleaseLeaseAsync("chat-in-flight", _prefix + "lease", successor.WindowEndsAtUtc);
        Check("lease-release-reopens", (await service.TryAcquireLeaseAsync("chat-in-flight", _prefix + "lease", 1, TimeSpan.FromMinutes(5))).Allowed);
    }

    private async Task QueuedCallerAsync(bool cancel)
    {
        var label = cancel ? "queued-cancel" : "queued-release";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var holder = new NpgsqlConnection(settings.ConnectionString);
        await holder.OpenAsync(deadline.Token);
        await using var held = await holder.BeginTransactionAsync(IsolationLevel.Serializable, deadline.Token);
        await using (var command = new NpgsqlCommand("LOCK TABLE \"DistributedRateLimitBuckets\" IN SHARE ROW EXCLUSIVE MODE", holder, held))
            await command.ExecuteNonQueryAsync(deadline.Token);
        var attempts = new AcquisitionObserver();
        await using var context = new OrbitDbContext(Options(attempts));
        await context.Database.OpenConnectionAsync(deadline.Token);
        var waiter = (NpgsqlConnection)context.Database.GetDbConnection();
        var pending = new DistributedRateLimitService(context, _clock).TryAcquireAsync("auth", _prefix + label, caller.Token);
        try
        {
            await WaitForBlockedAsync(waiter.ProcessID, holder.ProcessID, deadline.Token);
            Check(label + "-actually-queued", !pending.IsCompleted);
            var release = Stopwatch.StartNew();
            if (cancel)
            {
                caller.Cancel();
                try
                {
                    await pending.WaitAsync(deadline.Token);
                    Check(label + "-canceled", false);
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    Check(label + "-canceled", true);
                }
                Check(label + "-one-attempt-no-save", attempts.Starts == 1 && attempts.Saves == 0);
                Check(label + "-transaction-disposed", context.Database.CurrentTransaction == null);
                await held.CommitAsync(deadline.Token);
            }
            else
            {
                await held.CommitAsync(deadline.Token);
                var decision = await pending.WaitAsync(deadline.Token);
                Check(label + "-allowed-after-release", decision.Allowed && decision.CurrentCount == 1);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { Case = label, completionAfterSignalMs = release.Elapsed.TotalMilliseconds }));
        }
        finally
        {
            caller.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
        await using (var readback = new OrbitDbContext(Options()))
        {
            var count = await readback.DistributedRateLimitBuckets.Where(bucket => bucket.PartitionKey == _prefix + label)
                .SumAsync(bucket => bucket.Count, deadline.Token);
            Check(label + "-persisted-count", count == (cancel ? 0 : 1));
        }
        Check(label + "-subsequent-acquisition", (await AcquireAsync("auth", label, deadline.Token)).CurrentCount == (cancel ? 1 : 2));
    }

    private async Task WaitForBlockedAsync(int waiter, int holder, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(settings.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT @holder = ANY(pg_blocking_pids(@waiter))", observer);
        command.Parameters.AddWithValue("holder", holder);
        command.Parameters.AddWithValue("waiter", waiter);
        while (await command.ExecuteScalarAsync(token) is not true)
            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
        Console.WriteLine("PG17_QUEUE actual_blocker_confirmed=true");
    }

    private async Task ReleasedAfterFailureAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var context = new OrbitDbContext(Options(new FailSave())))
        {
            try
            {
                await new DistributedRateLimitService(context, _clock).TryAcquireAsync("auth", _prefix + "failed-save", deadline.Token);
                Check("failed-save-propagates", false);
            }
            catch (ProofSaveException)
            {
                Check("failed-save-propagates", true);
            }
            Check("failed-save-disposes-transaction", context.Database.CurrentTransaction == null);
            Check("failed-save-stores-nothing", !await context.DistributedRateLimitBuckets.AsNoTracking()
                .AnyAsync(bucket => bucket.PartitionKey == _prefix + "failed-save", deadline.Token));
            Check("failed-save-releases-lock", (await AcquireAsync("auth", "after-failed-save", deadline.Token)).Allowed);
        }
    }

    private void Check(string name, bool success)
    {
        Console.WriteLine($"CHECK {name}={(success ? "PASS" : "FAIL")}");
        if (!success) _failures++;
    }

    public static void PrintFailure(string label, Exception error)
    {
        var chain = new List<object>();
        for (Exception? item = error; item is not null; item = item.InnerException)
            chain.Add(new { exceptionType = item.GetType().FullName, sqlState = (item as PostgresException)?.SqlState });
        Console.WriteLine(JsonSerializer.Serialize(new { Case = label, chain }));
    }

    private sealed class ProofClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _value = initial;
        public override DateTimeOffset GetUtcNow() => _value;
        public void Set(DateTimeOffset value) => _value = value;
    }

    private sealed class ProofSaveException : Exception;

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw new ProofSaveException();
    }
}
