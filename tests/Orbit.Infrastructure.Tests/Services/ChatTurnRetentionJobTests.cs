using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ChatTurnRetentionJobTests
{
    [Fact]
    public async Task ChatTurnRetention_TwelveThousandExpiredRows_DeletesInThreeBoundedStatements()
    {
        var commands = new Deletes();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        await Seed(factory, 12000);
        using var provider = Provider(factory);
        var job = new ChatTurnRetentionJob(provider.GetRequiredService<IServiceScopeFactory>());

        await job.RunAsync(default);

        commands.Count.Should().Be(3);
        commands.Sql.Should().OnlyContain(sql => sql.Contains("LIMIT", StringComparison.Ordinal));
        using var read = factory.CreateContext();
        (await read.ChatTurns.CountAsync()).Should().Be(100);
    }

    [Fact]
    public async Task ChatTurnRetention_DeleteThrows_PropagatesWithoutPartialDelete()
    {
        var commands = new Deletes { Fail = true };
        using var factory = new SqliteOrbitDbContextFactory(commands);
        await Seed(factory, 50);
        using var provider = Provider(factory);
        var job = new ChatTurnRetentionJob(provider.GetRequiredService<IServiceScopeFactory>());

        var run = () => job.RunAsync(default);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("delete failed");
        using var read = factory.CreateContext();
        (await read.ChatTurns.CountAsync()).Should().Be(150);
    }

    [Fact]
    public async Task ChatTurnRetention_TwoConcurrentRuns_LeaveNoExpiredRows()
    {
        using var factory = new SqliteOrbitDbContextFactory(Path.Combine(Path.GetTempPath(), $"retention-{Guid.NewGuid():N}.db"));
        await Seed(factory, 100);
        using var provider = Provider(factory);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        await Task.WhenAll(Task.Run(() => new ChatTurnRetentionJob(scopeFactory).RunAsync(default)),
            Task.Run(() => new ChatTurnRetentionJob(scopeFactory).RunAsync(default)));

        using var read = factory.CreateContext();
        (await read.ChatTurns.CountAsync()).Should().Be(100);
    }

    private static ServiceProvider Provider(SqliteOrbitDbContextFactory factory) => new ServiceCollection()
        .AddScoped(_ => factory.CreateContext())
        .AddScoped<IUnitOfWork>(scope => new UnitOfWork(scope.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()))
        .BuildServiceProvider();

    private static async Task Seed(SqliteOrbitDbContextFactory factory, int count)
    {
        using var db = factory.CreateContext();
        var user = User.Create("Retention", "retention@example.com").Value;
        db.Add(user);
        var nowAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        for (var index = 0; index < count + 100; index++)
            db.ChatTurns.Add(ChatTurn.Create(user.Id, index.ToString("x64"), new string('f', 64), nowAtUtc.AddDays(index < count ? -31 : 1)));
        await db.SaveChangesAsync();
    }

    private sealed class Deletes : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public bool Fail { get; init; }
        public List<string> Sql { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal) && command.CommandText.Contains("ChatTurns", StringComparison.Ordinal))
            {
                Count++;
                Sql.Add(command.CommandText);
                if (Fail) throw new InvalidOperationException("delete failed");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
