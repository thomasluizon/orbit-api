using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class NotificationRouteMigrationTests
{
    [Fact]
    public void AddDedupeKey_Up_BackfillsOnlyRecognizedKeysInBoundedRepeatSafeBatches()
    {
        var operations = GetOperations<AddNotificationDedupeKey>("Up");

        var column = operations.OfType<AddColumnOperation>().Should().ContainSingle().Subject;
        column.Name.Should().Be("DedupeKey");
        column.IsNullable.Should().BeTrue();

        var sql = operations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        sql.Should().Contain("\"DedupeKey\" IS NULL");
        sql.Should().Contain("\"Url\" NOT LIKE '/%'");
        sql.Should().Contain("^goal-deadline-");
        sql.Should().Contain("ROW_NUMBER() OVER");
        sql.Should().Contain("-duplicate-");
        sql.Should().Contain("LIMIT 1000");
        sql.Should().Contain("\"Url\" = '/progress'");
        sql.Should().Contain("RAISE NOTICE");

        var index = operations.OfType<CreateIndexOperation>().Should().ContainSingle().Subject;
        index.Columns.Should().Equal("DedupeKey");
        index.IsUnique.Should().BeTrue();
        index.Filter.Should().Be("\"DedupeKey\" IS NOT NULL");
    }

    [Fact]
    public void AddDedupeKey_Down_RestoresKeysBeforeDroppingColumn()
    {
        var operations = GetOperations<AddNotificationDedupeKey>("Down");

        var sql = operations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        sql.Should().Contain("regexp_replace");
        sql.Should().Contain("-duplicate-");
        sql.Should().Contain("\"DedupeKey\" = NULL");
        sql.Should().Contain("LIMIT 1000");
        operations.Last().Should().BeOfType<DropColumnOperation>()
            .Which.Name.Should().Be("DedupeKey");
    }

    [Fact]
    public void RewriteStreakUrls_UpAndDown_UseBoundedValueRewrites()
    {
        var upSql = GetOperations<RewriteStreakNotificationUrls>("Up")
            .OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        upSql.Should().Contain("WHERE \"Url\" = '/streak'");
        upSql.Should().Contain("SET \"Url\" = '/progress'");
        upSql.Should().Contain("\"DedupeKey\" = 'legacy-streak-url-'");
        upSql.Should().Contain("LIMIT 1000");
        upSql.Should().Contain("RAISE NOTICE");

        var downSql = GetOperations<RewriteStreakNotificationUrls>("Down")
            .OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        downSql.Should().Contain("WHERE \"Url\" = '/progress'");
        downSql.Should().Contain("\"DedupeKey\" = 'legacy-streak-url-'");
        downSql.Should().Contain("SET \"Url\" = '/streak'");
        downSql.Should().Contain("\"DedupeKey\" = NULL");
        downSql.Should().Contain("LIMIT 1000");
    }

    private static IReadOnlyList<MigrationOperation> GetOperations<TMigration>(string methodName)
        where TMigration : Migration, new()
    {
        var migration = new TMigration();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(TMigration)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);
        return builder.Operations;
    }
}
