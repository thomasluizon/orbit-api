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

    [Fact]
    public void LinkGamificationNotifications_UpAndDown_UseBoundedReversibleRewrites()
    {
        var upSql = GetOperations<LinkGamificationNotificationsToProgress>("Up")
            .OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        upSql.Should().Contain("\"Url\" IS NULL");
        upSql.Should().Contain("Conquista Desbloqueada: %");
        upSql.Should().Contain("Achievement Unlocked: %");
        upSql.Should().Contain("Subiu de nível! Agora você está no nível %");
        upSql.Should().Contain("Level Up! You''re now Level %");
        upSql.Should().Contain("\"Url\" = '/progress'");
        upSql.Should().Contain("'Nova conquista: '");
        upSql.Should().Contain("'New achievement: '");
        upSql.Should().Contain("'O nível '");
        upSql.Should().Contain("'Level '");
        upSql.Should().Contain("^Você alcançou (.*)! Continue assim!$");
        upSql.Should().Contain("^You''ve reached (.*)! Keep going!$");
        upSql.Should().Contain("LIMIT 1000");
        upSql.Should().Contain("RAISE NOTICE");
        upSql.Should().Contain("INSERT INTO \"__LinkGamificationNotificationsToProgress\"");

        var downSql = GetOperations<LinkGamificationNotificationsToProgress>("Down")
            .OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        downSql.Should().Contain("DELETE FROM \"__LinkGamificationNotificationsToProgress\"");
        downSql.Should().Contain("RETURNING \"Id\", \"Title\", \"Body\"");
        downSql.Should().Contain("\"Url\" = NULL");
        downSql.Should().Contain("\"Title\" = restored.\"Title\"");
        downSql.Should().Contain("\"Body\" = restored.\"Body\"");
        downSql.Should().Contain("LIMIT 1000");
        downSql.Should().Contain("DROP TABLE \"__LinkGamificationNotificationsToProgress\"");
    }

    [Fact]
    public void HistoricalGamificationTitles_AppearOnlyInMigrations()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Orbit.slnx")))
            root = root.Parent;

        root.Should().NotBeNull();
        var sourceFiles = Directory.GetFiles(Path.Combine(root!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        foreach (var path in sourceFiles)
        {
            var source = File.ReadAllText(path);
            source.Should().NotContainAny("Conquista Desbloqueada", "Achievement Unlocked", "Subiu de nível!", "Level Up!");
        }
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
