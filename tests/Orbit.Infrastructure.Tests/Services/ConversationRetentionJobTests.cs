using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Entities;
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
        await database.Context.SaveChangesAsync();
        var services = new ServiceCollection().AddScoped<OrbitDbContext>(_ => database.CreateContext());
        await using var provider = services.BuildServiceProvider();
        var job = new ConversationRetentionJob(provider.GetRequiredService<IServiceScopeFactory>());
        job.Name.Should().Be("conversation-retention");
        job.CronExpression.Should().Be("15 * * * *");
        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None);
        (await database.Context.ConversationTurns.AsNoTracking().ToListAsync()).Should().ContainSingle().Which.Id.Should().Be(recent.Id);
    }
}
