using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class DropUserHandleColumnMigrationTests
{
    [Fact]
    public void UpAndDown_UseOnlyIdempotentHandleSchemaSqlInDependencyOrder()
    {
        var migration = new DropUserHandleColumn();

        migration.UpOperations.Should().HaveCount(2).And.OnlyContain(operation => operation is SqlOperation);
        migration.UpOperations.Cast<SqlOperation>().Select(operation => operation.Sql.Trim()).Should().Equal(
            "DROP INDEX IF EXISTS \"IX_Users_Handle_Lower\";",
            "ALTER TABLE \"Users\" DROP COLUMN IF EXISTS \"Handle\";");
        migration.DownOperations.Should().HaveCount(2).And.OnlyContain(operation => operation is SqlOperation);
        migration.DownOperations.Cast<SqlOperation>().Select(operation => operation.Sql.Trim()).Should().Equal(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"Handle\" character varying(20) NULL;",
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_Handle_Lower\" ON \"Users\" USING btree (lower((\"Handle\")::text)) WHERE (\"Handle\" IS NOT NULL);");
        migration.UpOperations.Concat(migration.DownOperations).Cast<SqlOperation>()
            .Should().OnlyContain(operation => !operation.SuppressTransaction);
        RollingMigrationGate.Check("drop-handle", migration.UpOperations).Should().ContainSingle()
            .Which.Should().Contain("deployment barrier");
    }

    [Fact]
    public void TargetModel_PreservesPreviousModelAndSnapshot()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var target = initializer.Initialize(new DropUserHandleColumn().TargetModel, designTime: true);
        var differ = context.GetService<IMigrationsModelDiffer>();

        var previous = initializer.Initialize(new AddDesktopHandoffs().TargetModel, designTime: true);
        differ.GetDifferences(previous.GetRelationalModel(), target.GetRelationalModel()).Should().BeEmpty();
        var snapshot = initializer.Initialize(new OrbitDbContextModelSnapshot().Model, designTime: true);
        ChatTurnMigrationTests.AssertExpansion(differ.GetDifferences(target.GetRelationalModel(), snapshot.GetRelationalModel()));
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task Up_RemovesColumnAndIndex_AndFullUserSelectStillRuns()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        await CreateLegacyHandleSchemaAsync(context);
        var user = User.Create("Legacy User", "legacy@example.com").Value;
        context.Users.Add(user);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("UPDATE \"Users\" SET \"Handle\" = 'legacy_handle'");

        await ApplyAsync(context, new DropUserHandleColumn().UpOperations);

        (await ScalarAsync(context, "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name = 'Handle'"))
            .Should().Be(0);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_Users_Handle_Lower'"))
            .Should().Be(0);
        context.ChangeTracker.Clear();
        var query = context.Users.AsNoTracking();
        query.ToQueryString().Should().NotContain("\"Handle\"");
        (await query.SingleAsync()).Should().BeEquivalentTo(user);
    }

    [Fact]
    public async Task Down_RestoresNullableColumnAndPartialUniqueExpressionIndex_WithoutLegacyValues()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        await CreateLegacyHandleSchemaAsync(context);
        context.Users.AddRange(
            User.Create("First User", "first@example.com").Value,
            User.Create("Second User", "second@example.com").Value);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("UPDATE \"Users\" SET \"Handle\" = 'legacy_handle' WHERE \"Email\" = 'first@example.com'");
        var migration = new DropUserHandleColumn();
        await ApplyAsync(context, migration.UpOperations);

        await ApplyAsync(context, migration.DownOperations);

        (await ScalarAsync(context, "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name = 'Handle' AND type = 'character varying(20)' AND \"notnull\" = 0"))
            .Should().Be(1);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM pragma_index_list('Users') WHERE name = 'IX_Users_Handle_Lower' AND \"unique\" = 1 AND partial = 1"))
            .Should().Be(1);
        (await ScalarAsync(context, "SELECT COUNT(*) FROM \"Users\" WHERE \"Handle\" IS NULL"))
            .Should().Be(2);
        await context.Database.ExecuteSqlRawAsync("UPDATE \"Users\" SET \"Handle\" = 'Restored' WHERE \"Email\" = 'first@example.com'");
        var duplicate = () => context.Database.ExecuteSqlRawAsync(
            "UPDATE \"Users\" SET \"Handle\" = 'restored' WHERE \"Email\" = 'second@example.com'");
        await duplicate.Should().ThrowAsync<SqliteException>().WithMessage("*UNIQUE constraint failed*");
        context.ChangeTracker.Clear();
        (await context.Users.AsNoTracking().ToListAsync()).Should().HaveCount(2);
    }

    private static async Task CreateLegacyHandleSchemaAsync(OrbitDbContext context)
    {
        var legacy = new AddSocialFoundation().UpOperations;
        var column = legacy.OfType<AddColumnOperation>().Single(operation => operation.Table == "Users" && operation.Name == "Handle");
        var generator = context.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate([column], context.Model))
            await context.Database.ExecuteSqlRawAsync(command.CommandText);
        var index = legacy.OfType<SqlOperation>().Single(operation => operation.Sql.Contains("IX_Users_Handle_Lower", StringComparison.Ordinal));
        await context.Database.ExecuteSqlRawAsync(index.Sql);
    }

    private static async Task ApplyAsync(OrbitDbContext context, IReadOnlyList<MigrationOperation> operations)
    {
        foreach (var operation in operations.Cast<SqlOperation>())
        {
            // https://github.com/thomasluizon/orbit-tickets/issues/1654 uses PostgreSQL guards and casts that SQLite cannot execute.
            var sql = operation.Sql.Replace("COLUMN IF EXISTS", "COLUMN", StringComparison.Ordinal)
                .Replace("COLUMN IF NOT EXISTS", "COLUMN", StringComparison.Ordinal)
                .Replace("USING btree ", "", StringComparison.Ordinal)
                .Replace("(\"Handle\")::text", "CAST(\"Handle\" AS TEXT)", StringComparison.Ordinal);
            await context.Database.ExecuteSqlRawAsync(sql);
        }
    }

    private static async Task<int> ScalarAsync(OrbitDbContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
