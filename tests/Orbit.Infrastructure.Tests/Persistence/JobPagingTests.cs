using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class JobPagingTests
{
    [Fact]
    public async Task Pages_TwelveHundredCandidates_ReadAtMostFourHundredWithoutOffsets()
    {
        var commands = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        var db = factory.Context;
        db.Users.AddRange(Enumerable.Range(0, 1200).Select(index => User.Create($"User {index}", $"user{index}@example.com").Value));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        commands.Reset();
        var ids = new List<Guid>();
        Guid? cursor = null;
        while (true)
        {
            var page = await db.Users.AsNoTracking().Page(cursor).Select(row => row.Id).ToListAsync();
            if (page.Count == 0) break;
            page.Should().HaveCountLessThanOrEqualTo(400);
            ids.AddRange(page);
            cursor = page[^1];
        }
        ids.Should().HaveCount(1200).And.OnlyHaveUniqueItems();
        commands.Commands.Should().OnlyContain(command => command.Rows <= 400 && !command.Sql.Contains("OFFSET", StringComparison.Ordinal));
    }
}
