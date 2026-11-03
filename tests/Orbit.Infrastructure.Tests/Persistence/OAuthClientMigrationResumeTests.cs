using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class OAuthClientMigrationResumeTests
{
    private const string MigrationId = "20261102153013_PersistOAuthClients";
    private const string IndexName = "IX_OAuthStates_Kind_PartitionKey_ExpiresAtUtc";

    [PostgresFact]
    public Task Migration_InterruptedBeforeConcurrentIndex_Resumes() => AssertResumes("missing");

    [PostgresFact]
    public Task Migration_InterruptedWithInvalidConcurrentIndex_Resumes() => AssertResumes("invalid");

    [PostgresFact]
    public Task Migration_InterruptedAfterConcurrentIndex_Resumes() => AssertResumes("valid");

    private static Task AssertResumes(string indexState) => PostgresTestDatabase.WithDatabase(async db =>
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql(db.Database.GetConnectionString(), postgres => postgres.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
            .AddInterceptors(new InterruptIndex()).Options;
        await using (var interrupted = new OrbitDbContext(options))
        {
            var migrate = () => interrupted.GetService<IMigrator>().MigrateAsync(MigrationId);
            await migrate.Should().ThrowAsync<MigrationInterruptedException>();
        }

        (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(MigrationId);
        var client = OAuthClient.Create("Claude", ["https://claude.ai/api/mcp/auth_callback"]);
        db.OAuthClients.Add(client);
        db.OAuthStates.AddRange(
            OAuthState.Create(new string('a', 64), "google", "first", DateTime.UtcNow.AddMinutes(5)),
            OAuthState.Create(new string('b', 64), "google", "second", DateTime.UtcNow.AddMinutes(5)));
        await db.SaveChangesAsync();

        if (indexState == "invalid")
        {
            var build = () => db.Database.ExecuteSqlRawAsync($"CREATE UNIQUE INDEX CONCURRENTLY \"{IndexName}\" ON \"OAuthStates\" (\"Kind\");");
            (await build.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
            (await IsValidIndex(db)).Should().BeFalse();
        }
        else if (indexState == "valid")
        {
            await db.Database.ExecuteSqlRawAsync($"CREATE INDEX CONCURRENTLY \"{IndexName}\" ON \"OAuthStates\" (\"Kind\", \"PartitionKey\", \"ExpiresAtUtc\");");
            (await IsValidIndex(db)).Should().BeTrue();
        }

        await db.GetService<IMigrator>().MigrateAsync(MigrationId);

        (await db.Database.GetAppliedMigrationsAsync()).Should().ContainSingle(id => id == MigrationId);
        (await IsValidIndex(db)).Should().BeTrue();
        (await db.OAuthClients.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(client);
        (await db.OAuthStates.AsNoTracking().CountAsync()).Should().Be(2);
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }, "20261102123402_ReleaseSafetyAppConfig");

    private static Task<bool> IsValidIndex(OrbitDbContext db) => db.Database.SqlQueryRaw<bool>(
        $"SELECT indisvalid AS \"Value\" FROM pg_index WHERE indexrelid = '\"{IndexName}\"'::regclass").SingleAsync();

    private sealed class MigrationInterruptedException : Exception;

    private sealed class InterruptIndex : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(IndexName, StringComparison.Ordinal)
                && command.CommandText.Contains("INDEX CONCURRENTLY", StringComparison.Ordinal))
            {
                command.Transaction.Should().BeNull();
                throw new MigrationInterruptedException();
            }
            return ValueTask.FromResult(result);
        }
    }
}
