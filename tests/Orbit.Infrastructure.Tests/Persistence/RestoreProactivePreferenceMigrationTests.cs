using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class RestoreProactivePreferenceMigrationTests
{
    internal static void AssertRequiredPreference(IReadOnlyList<MigrationOperation> operations)
    {
        var column = operations.OfType<AlterColumnOperation>().Should().ContainSingle(operation =>
            operation.Table == "Users" && operation.Name == nameof(User.ProactiveAstraEnabled)).Subject;
        column.ClrType.Should().Be(typeof(bool));
        column.ColumnType.Should().Be("boolean");
        column.IsNullable.Should().BeFalse();
        column.OldColumn.IsNullable.Should().BeTrue();
        column.DefaultValue.Should().Be(true);
    }

    [Fact]
    public void PostgresScript_NormalizesOldWritersAndBackfillsBeforeRequiringTheColumn()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var migration = new RestoreRequiredProactiveAstraPreference { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        var operations = migration.UpOperations.ToList();
        var column = operations.OfType<AlterColumnOperation>().Should().ContainSingle().Subject;
        column.Table.Should().Be("Users");
        column.Name.Should().Be("ProactiveAstraEnabled");
        column.IsNullable.Should().BeFalse();
        column.OldColumn.IsNullable.Should().BeTrue();
        column.DefaultValue.Should().Be(true);
        var script = context.GetService<IMigrator>().GenerateScript(
            "PersistConversationTurns", "RestoreRequiredProactiveAstraPreference");

        script.Should().Contain("BEFORE INSERT OR UPDATE ON \"Users\"")
            .And.Contain("COALESCE(NEW.\"ProactiveAstraEnabled\", TRUE)")
            .And.Contain("ALTER TABLE \"Users\" ALTER COLUMN \"ProactiveAstraEnabled\" SET NOT NULL")
            .And.Contain("ALTER TABLE \"Users\" ALTER COLUMN \"ProactiveAstraEnabled\" SET DEFAULT TRUE");
        script.IndexOf("CREATE TRIGGER", StringComparison.Ordinal).Should()
            .BeLessThan(script.IndexOf("UPDATE \"Users\"", StringComparison.Ordinal));
        script.IndexOf("UPDATE \"Users\"", StringComparison.Ordinal).Should()
            .BeLessThan(script.IndexOf("SET NOT NULL", StringComparison.Ordinal));
        operations.OfType<SqlOperation>().Should().OnlyContain(operation => !operation.SuppressTransaction);
    }

    [Fact]
    public async Task Migration_BackfillsNullPreservesChoicesAndRestoresRequiredDefault()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Users'";
            var create = (string)(await command.ExecuteScalarAsync())!;
            create.Should().Contain("\"ProactiveAstraEnabled\" INTEGER NOT NULL DEFAULT 1");
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"Users\"");
            await context.Database.ExecuteSqlRawAsync(create.Replace(
                "\"ProactiveAstraEnabled\" INTEGER NOT NULL DEFAULT 1", "\"ProactiveAstraEnabled\" INTEGER NULL", StringComparison.Ordinal));
        }
        var unset = User.Create("Unset", "unset@example.com").Value;
        unset.Deactivate(DateTime.UtcNow.AddDays(7));
        var disabled = User.Create("Disabled", "disabled@example.com").Value;
        disabled.SetProactiveAstraEnabled(false);
        var enabled = User.Create("Enabled", "enabled@example.com").Value;
        context.Users.AddRange(unset, disabled, enabled);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Users\" SET \"ProactiveAstraEnabled\" = NULL WHERE \"Id\" = {unset.Id}");
        var migration = new RestoreRequiredProactiveAstraPreference { ActiveProvider = "Microsoft.EntityFrameworkCore.Sqlite" };
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            migration.UpOperations, context.GetService<IDesignTimeModel>().Model);
        foreach (var command in commands)
            await context.Database.ExecuteSqlRawAsync(command.CommandText);
        context.ChangeTracker.Clear();

        var users = await context.Users.IgnoreQueryFilters().ToDictionaryAsync(user => user.Id);
        users[unset.Id].ProactiveAstraEnabled.Should().BeTrue();
        users[disabled.Id].ProactiveAstraEnabled.Should().BeFalse();
        users[enabled.Id].ProactiveAstraEnabled.Should().BeTrue();
        var nullWrite = () => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Users\" SET \"ProactiveAstraEnabled\" = NULL WHERE \"Id\" = {enabled.Id}");
        await nullWrite.Should().ThrowAsync<SqliteException>().WithMessage("*NOT NULL constraint failed: Users.ProactiveAstraEnabled*");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Users\" SET \"ProactiveAstraEnabled\" = false WHERE \"Id\" = {enabled.Id}");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Users\" SET \"Name\" = 'Updated' WHERE \"Id\" = {enabled.Id}");
        context.ChangeTracker.Clear();
        (await context.Users.SingleAsync(user => user.Id == enabled.Id)).ProactiveAstraEnabled.Should().BeFalse();

        await using var metadata = context.Database.GetDbConnection().CreateCommand();
        metadata.CommandText = "SELECT \"notnull\", dflt_value FROM pragma_table_info('Users') WHERE name = 'ProactiveAstraEnabled'";
        await using var reader = await metadata.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(1);
        reader.GetString(1).Should().Be("1");
    }

    [Fact]
    public void Down_RelaxesTheColumnBeforeRemovingTheCompatibilityTrigger()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "RestoreRequiredProactiveAstraPreference", "PersistConversationTurns");
        script.Should().Contain("DROP NOT NULL").And.Contain("DROP DEFAULT")
            .And.Contain("DROP TRIGGER IF EXISTS \"NormalizeProactiveAstraEnabledOnWrite\" ON \"Users\"")
            .And.Contain("DROP FUNCTION IF EXISTS \"NormalizeUsersProactiveAstraEnabled\"()");
        script.IndexOf("DROP NOT NULL", StringComparison.Ordinal).Should()
            .BeLessThan(script.IndexOf("DROP TRIGGER", StringComparison.Ordinal));
    }
}
