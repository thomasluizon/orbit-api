using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.BackgroundJobs;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class BackfillProgressTests
{
    [Fact]
    public async Task XpBackfill_FailsOnUserSixHundred_ResumesAfterSecondFinishedPage()
    {
        var failure = new FailUserSave(Id(600));
        var reads = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(failure, reads);
        using (var seed = factory.CreateContext())
        {
            for (var index = 1; index <= 1000; index++)
            {
                var user = User.Create($"User {index}", $"user{index}@example.com").Value;
                seed.Entry(user).Property(row => row.Id).CurrentValue = Id(index);
                user.AddXp(1);
                seed.Add(user);
            }
            await seed.SaveChangesAsync();
        }
        using var provider = new ServiceCollection().AddScoped(_ => factory.CreateContext())
            .AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>))
            .AddSingleton(new DatabaseConnectionSettings()).AddScoped<IUnitOfWork, UnitOfWork>()
            .AddScoped<XpAwardLogBackfillService>().BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        using var job = new XpAwardLogBackfillHostedService(scopes, NullLogger<XpAwardLogBackfillHostedService>.Instance, new TestScheduledJobLeaseProvider());

        await job.RunBackfillAsync(default);

        using (var checkpoint = factory.CreateContext())
        {
            (await checkpoint.AppConfigs.SingleAsync(row => row.Key == "XpAwardLogBackfillCursor")).Value.Should().Be(Id(400).ToString());
            (await checkpoint.AppConfigs.AnyAsync(row => row.Key == "XpAwardLogBackfillComplete")).Should().BeFalse();
            (await checkpoint.XpAwardLogs.CountAsync()).Should().Be(599);
        }
        failure.Users.Clear();
        reads.Reset();
        await job.RunBackfillAsync(default);

        using var read = factory.CreateContext();
        (await read.XpAwardLogs.CountAsync()).Should().Be(1000);
        (await read.AppConfigs.AnyAsync(row => row.Key == "XpAwardLogBackfillComplete")).Should().BeTrue();
        failure.Users.Should().HaveCount(401).And.OnlyContain(id => id.CompareTo(Id(400)) > 0);
        reads.Commands.Where(command => command.Sql.Contains("ORDER BY", StringComparison.Ordinal))
            .Should().OnlyContain(command => command.Rows <= 200 && !command.Sql.Contains("OFFSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EncryptionMigration_TwoStarts_SecondSkipsWhileFirstHoldsLease()
    {
        var blocked = new BlockingRead();
        using var factory = new SqliteOrbitDbContextFactory(blocked);
        using var provider = new ServiceCollection().AddScoped(_ => factory.CreateContext()).BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var encryption = Substitute.For<IEncryptionService>();
        encryption.IsConfigured.Returns(true);
        var leases = new TestScheduledJobLeaseProvider();
        using var first = new DataEncryptionMigrationService(scopes, encryption, NullLogger<DataEncryptionMigrationService>.Instance, leases);
        using var second = new DataEncryptionMigrationService(scopes, encryption, NullLogger<DataEncryptionMigrationService>.Instance, leases);
        var run = first.RunMigrationAsync(default);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await second.RunMigrationAsync(default).WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { blocked.Release.SetResult(); }
        await run;
        using var read = factory.CreateContext();
        (await read.AppConfigs.CountAsync(row => row.Key == "EncryptionMigrationComplete_v2")).Should().Be(1);
        blocked.MigrationChecks.Should().Be(1);
    }

    private static Guid Id(int index) => Guid.Parse($"00000000-0000-0000-0000-{index:D12}");
    private sealed class FailUserSave(Guid failedId) : SaveChangesInterceptor
    {
        private bool _failed;
        internal List<Guid> Users { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var rows = eventData.Context!.ChangeTracker.Entries<XpAwardLog>().Where(entry => entry.State == EntityState.Added).ToList();
            Users.AddRange(rows.Select(row => row.Entity.UserId));
            if (!_failed && rows.Any(row => row.Entity.UserId == failedId))
            { _failed = true; throw new InvalidOperationException("interrupted user"); }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
    private sealed class BlockingRead : DbCommandInterceptor
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int MigrationChecks { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("EXISTS", StringComparison.Ordinal) && command.CommandText.Contains("AppConfigs", StringComparison.Ordinal))
            {
                MigrationChecks++;
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
