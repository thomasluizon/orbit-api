using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AuthUserStoreTests
{
    [Fact]
    public async Task NewAndExistingAccounts_ExecuteIdenticalSqlInOrder()
    {
        var commands = new CommandRecorder();
        using var database = new SqliteOrbitDbContextFactory(commands);
        var context = database.Context;
        var store = new AuthUserStore(context);
        var candidate = Candidate("New", "pt-BR");
        commands.Sql.Clear();

        var first = await store.UpsertAsync(candidate, CancellationToken.None);
        await context.SaveChangesAsync();
        var signupSql = commands.Sql.ToArray();
        commands.Sql.Clear();
        var returning = await store.UpsertAsync(Candidate("Replacement", "en"), CancellationToken.None);
        await context.SaveChangesAsync();

        commands.Sql.Should().Equal(signupSql);
        signupSql.Should().HaveCount(3);
        signupSql[0].Should().Contain("ON CONFLICT (\"Email\") DO UPDATE");
        signupSql[1].Should().StartWith("SELECT");
        signupSql[2].Should().StartWith("UPDATE");
        returning.Should().BeEquivalentTo(first);
        returning.Should().BeEquivalentTo(candidate);
        (await context.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Conflict_PreservesDeactivatedAccountAndItsPreferences()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var context = database.Context;
        var original = Candidate("Original", "pt-BR");
        original.SetGoogleTokens("calendar-access", "calendar-refresh");
        original.Deactivate(DateTime.UtcNow.AddDays(7));
        context.Users.Add(original);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var user = await new AuthUserStore(context).UpsertAsync(Candidate("Replacement", "en"), CancellationToken.None);

        user.Should().BeEquivalentTo(original);
        context.Entry(user).Property(u => u.Email).IsModified.Should().BeTrue();
    }

    private static User Candidate(string name, string language)
    {
        var user = User.Create(name, "person@example.com").Value;
        user.SetLanguage(language);
        user.SeedDefaultHandle();
        return user;
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
