using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class ConversationRetentionJobTests
{
    [Fact]
    public async Task RunAsync_RemovesExpiredRowsAndPreservesRecentRowsForEveryUser()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var user = User.Create("Person", "retention@example.com").Value;
        database.Context.Users.Add(user);
        var expired = ConversationTurn.Create(user.Id, Guid.NewGuid(), "user", DateTime.UtcNow.AddDays(-31), null, "Expired", "[]").Value;
        var recent = ConversationTurn.Create(user.Id, Guid.NewGuid(), "assistant", DateTime.UtcNow.AddDays(-29), "model", "Recent", "[]").Value;
        database.Context.ConversationTurns.AddRange(expired, recent);
        var expiredReplay = ChatTurn.Create(user.Id, new string('A', 64), new string('B', 64), DateTime.UtcNow.AddDays(-31));
        expiredReplay.Complete(ChatStreamEvent.Final(new ChatResponse("Expired response", [])).ToJson());
        var expiredReservation = ChatTurn.Create(user.Id, new string('C', 64), new string('D', 64), DateTime.UtcNow.AddDays(-31));
        var recentReplay = ChatTurn.Create(user.Id, new string('E', 64), new string('F', 64), DateTime.UtcNow.AddDays(-29));
        recentReplay.Complete(ChatStreamEvent.Final(new ChatResponse("Recent response", [])).ToJson());
        database.Context.ChatTurns.AddRange(expiredReplay, expiredReservation, recentReplay);
        var linkedReplay = ChatTurn.Create(user.Id, new string('1', 64), new string('2', 64), DateTime.UtcNow.AddDays(-29));
        linkedReplay.Complete(ChatStreamEvent.Final(new ChatResponse("Expired conversation response", [])).ToJson());
        database.Context.ChatTurns.Add(linkedReplay);
        database.Context.ConversationTurns.Add(ConversationTurn.Create(user.Id, linkedReplay.Id, "assistant",
            DateTime.UtcNow.AddDays(-31), "model", "Expired conversation", "[]").Value);
        var other = User.Create("Other", "other-retention@example.com").Value;
        database.Context.Users.Add(other);
        var otherExpired = ChatTurn.Create(other.Id, new string('A', 64), new string('B', 64), DateTime.UtcNow.AddDays(-31));
        var otherRecent = ChatTurn.Create(other.Id, new string('C', 64), new string('D', 64), DateTime.UtcNow.AddDays(-29));
        database.Context.ChatTurns.AddRange(otherExpired, otherRecent);
        await database.Context.SaveChangesAsync();
        var services = new ServiceCollection().AddScoped<OrbitDbContext>(_ => database.CreateContext())
            .AddScoped<IUnitOfWork>(provider => new UnitOfWork(provider.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()));
        await using var provider = services.BuildServiceProvider();
        var job = new ConversationRetentionJob(provider.GetRequiredService<IServiceScopeFactory>());
        job.Name.Should().Be("conversation-retention");
        job.CronExpression.Should().Be("15 * * * *");
        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None);
        (await database.Context.ConversationTurns.AsNoTracking().ToListAsync()).Should().ContainSingle().Which.Id.Should().Be(recent.Id);
        (await database.Context.ChatTurns.AsNoTracking().Select(turn => turn.Id).ToListAsync())
            .Should().BeEquivalentTo([recentReplay.Id, otherRecent.Id]);
    }

    [Fact]
    public async Task RunAsync_ConversationDeleteFails_RollsBackReplayDeletion()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var user = User.Create("Person", "rollback-retention@example.com").Value;
        var replay = ChatTurn.Create(user.Id, new string('A', 64), new string('B', 64), DateTime.UtcNow.AddDays(-31));
        replay.Complete(ChatStreamEvent.Final(new ChatResponse("Expired response", [])).ToJson());
        database.Context.Users.Add(user);
        database.Context.ChatTurns.Add(replay);
        database.Context.ConversationTurns.Add(ConversationTurn.Create(user.Id, replay.Id, "user",
            DateTime.UtcNow.AddDays(-31), null, "Expired conversation", "[]").Value);
        await database.Context.SaveChangesAsync();
        await database.Context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailConversationDelete BEFORE DELETE ON "ConversationTurns"
            BEGIN
                SELECT RAISE(ABORT, 'Conversation deletion failed');
            END;
            """);
        var services = new ServiceCollection().AddScoped<OrbitDbContext>(_ => database.CreateContext())
            .AddScoped<IUnitOfWork>(provider => new UnitOfWork(provider.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()));
        await using var provider = services.BuildServiceProvider();
        var job = new ConversationRetentionJob(provider.GetRequiredService<IServiceScopeFactory>());

        var run = () => job.RunAsync(CancellationToken.None);

        await run.Should().ThrowAsync<SqliteException>();
        (await database.Context.ChatTurns.AsNoTracking().SingleAsync()).TerminalEvent.Should().Be(replay.TerminalEvent);
        (await database.Context.ConversationTurns.CountAsync()).Should().Be(1);
    }
}
