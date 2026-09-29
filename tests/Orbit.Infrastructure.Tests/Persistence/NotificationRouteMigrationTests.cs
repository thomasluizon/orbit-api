using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
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
    public async Task LinkGamificationNotifications_UpAndDown_RewritesEveryHistoricalCopyAndRestoresOriginalRows()
    {
        var upSql = GetOperations<LinkGamificationNotificationsToProgress>("Up")
            .OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        var downSql = GetOperations<LinkGamificationNotificationsToProgress>("Down")
            .OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        upSql.Should().Contain("LIMIT 1000").And.Contain("RAISE NOTICE");
        downSql.Should().Contain("LIMIT 1000");

        var rows = new (string Title, string Body, string? Url, string ExpectedTitle, string ExpectedBody, string? ExpectedUrl)[]
        {
            ("Conquista Desbloqueada: Primeira Órbita", "Crie seu primeiro hábito (+25 XP)", null,
                "Nova conquista: Primeira Órbita", "Crie seu primeiro hábito (+25 XP)", "/progress"),
            ("Achievement Unlocked: First Orbit", "Create your first habit (+25 XP)", null,
                "New achievement: First Orbit", "Create your first habit (+25 XP)", "/progress"),
            ("Subiu de nivel! Agora voce e Nivel 3", "Voce alcancou Orbiter! Continue assim!", null,
                "Você chegou ao nível 3", "O nível 3 se chama Orbiter.", "/progress"),
            ("Subiu de nível! Agora você é Nível 4", "Você alcançou Navigator! Continue assim!", null,
                "Você chegou ao nível 4", "O nível 4 se chama Navigator.", "/progress"),
            ("Subiu de nível! Agora você está no nível 7", "Você alcançou Comandante! Continue assim!", null,
                "Você chegou ao nível 7", "O nível 7 se chama Comandante.", "/progress"),
            ("Level Up! You're now Level 5", "You've reached Pilot! Keep going!", null,
                "You reached level 5", "Level 5 is called Pilot.", "/progress"),
            ("Subiu de nivel! Agora voce e Nivel 8", "Voce alcancou Admiral! Continue assim!", "/existing",
                "Subiu de nivel! Agora voce e Nivel 8", "Voce alcancou Admiral! Continue assim!", "/existing"),
            ("A different notification", "Leave this alone", null,
                "A different notification", "Leave this alone", null)
        };

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.CreateFunction<string, string, string, string>("regexp_replace", (input, pattern, replacement) =>
            Regex.Replace(input, pattern, replacement.Replace("\\1", "$1")));
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE \"Notifications\" (\"Id\" TEXT PRIMARY KEY, \"Title\" TEXT NOT NULL, \"Body\" TEXT NOT NULL, \"Url\" TEXT);");

        for (var index = 0; index < rows.Length; index++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO \"Notifications\" (\"Id\", \"Title\", \"Body\", \"Url\") VALUES ($id, $title, $body, $url);";
            insert.Parameters.AddWithValue("$id", index.ToString());
            insert.Parameters.AddWithValue("$title", rows[index].Title);
            insert.Parameters.AddWithValue("$body", rows[index].Body);
            insert.Parameters.AddWithValue("$url", (object?)rows[index].Url ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }

        var candidateWhere = Slice(upSql, "WHERE \"Url\" IS NULL", "ORDER BY \"Id\"");
        await ExecuteAsync(connection,
            $"CREATE TABLE \"__LinkGamificationNotificationsToProgress\" AS SELECT \"Id\", \"Title\", \"Body\" FROM \"Notifications\" {candidateWhere};");
        await ExecuteAsync(connection,
            "CREATE TEMP VIEW archived AS SELECT \"Id\" FROM \"__LinkGamificationNotificationsToProgress\";");

        var upUpdate = Slice(upSql, "UPDATE \"Notifications\" AS notification", "GET DIAGNOSTICS")
            .Replace("substring(notification.\"Title\" FROM length(", "substr(notification.\"Title\", length(");
        await ExecuteAsync(connection, upUpdate);

        for (var index = 0; index < rows.Length; index++)
        {
            var actual = await ReadNotificationAsync(connection, index);
            actual.Should().Be((rows[index].ExpectedTitle, rows[index].ExpectedBody, rows[index].ExpectedUrl));
        }

        await ExecuteAsync(connection,
            "CREATE TEMP VIEW restored AS SELECT \"Id\", \"Title\", \"Body\" FROM \"__LinkGamificationNotificationsToProgress\";");
        var downUpdate = Slice(downSql, "UPDATE \"Notifications\" AS notification", "RETURNING notification.\"Id\"");
        await ExecuteAsync(connection, downUpdate);

        for (var index = 0; index < rows.Length; index++)
        {
            var actual = await ReadNotificationAsync(connection, index);
            actual.Should().Be((rows[index].Title, rows[index].Body, rows[index].Url));
        }
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        startIndex.Should().BeGreaterThanOrEqualTo(0);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        endIndex.Should().BeGreaterThan(startIndex);
        return source[startIndex..endIndex];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string Title, string Body, string? Url)> ReadNotificationAsync(SqliteConnection connection, int index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Title\", \"Body\", \"Url\" FROM \"Notifications\" WHERE \"Id\" = $id;";
        command.Parameters.AddWithValue("$id", index.ToString());
        using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
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
