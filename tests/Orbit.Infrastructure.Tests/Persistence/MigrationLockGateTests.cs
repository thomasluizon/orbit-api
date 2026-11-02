using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class MigrationLockGateTests
{
    private const string Id = "20261102000000_Fixture";
    private static MigrationLockPolicy Policy => new("20261101210800_PersistServerConversations", "applied in production before this gate", []);
    private static SqlOperation Sql(string text, bool suppressed = false) => new() { Sql = text, SuppressTransaction = suppressed };

    [Fact]
    public void PlainIndexOnExistingTableFails_ConcurrentProviderCommandsSuppressTransactions()
    {
        var index = new CreateIndexOperation { Name = "IX_Habits_Test", Table = "Habits", Columns = ["UserId"] };
        MigrationLockGate.Check(Id, [index], Policy).Should().ContainSingle().Which.Should().Contain("Habits").And.Contain("CONCURRENTLY");
        index.AddAnnotation("Npgsql:CreatedConcurrently", true);
        MigrationLockGate.Check(Id, [index], Policy).Should().BeEmpty();
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate([index]);
        commands.Should().ContainSingle().Which.TransactionSuppressed.Should().BeTrue();
        commands[0].CommandText.Should().Contain("CONCURRENTLY");
        var drop = new DropIndexOperation { Name = index.Name, Table = index.Table };
        MigrationLockGate.Check(Id, [drop], Policy).Should().NotBeEmpty();
        drop.AddAnnotation("Npgsql:CreatedConcurrently", true);
        MigrationLockGate.Check(Id, [drop], Policy).Should().NotBeEmpty();
        db.GetService<IMigrationsSqlGenerator>().Generate([drop]).Should().ContainSingle().Which.TransactionSuppressed.Should().BeFalse();
        var rawDrop = Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Habits_Test\";", true);
        MigrationLockGate.Check(Id, [rawDrop], Policy).Should().BeEmpty();
        db.GetService<IMigrationsSqlGenerator>().Generate([rawDrop]).Should().ContainSingle().Which.TransactionSuppressed.Should().BeTrue();
    }

    [Theory]
    [InlineData("CREATE INDEX IF NOT EXISTS ix ON \"Habits\" (\"UserId\");", true, false)]
    [InlineData("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix ON \"Habits\" (\"UserId\");", false, false)]
    [InlineData("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix ON \"Habits\" (\"UserId\");", true, true)]
    [InlineData("DROP INDEX CONCURRENTLY IF EXISTS ix;", true, true)]
    [InlineData("UPDATE \"Users\" SET \"Name\" = 'a';", true, false)]
    [InlineData("UPDATE \"Users\" SET \"Name\" = 'a' WHERE \"Id\" > 0 AND \"Id\" <= 100;", false, false)]
    [InlineData("UPDATE \"Users\" SET \"Name\" = 'a' WHERE \"Id\" > 0 AND \"Id\" <= 100;", true, true)]
    [InlineData("DELETE FROM \"Users\" WHERE \"Id\" > 0 AND \"Id\" <= 100;", true, true)]
    [InlineData("DELETE FROM \"Users\" WHERE true;", true, false)]
    [InlineData("DO $$ BEGIN UPDATE \"Users\" SET \"Name\" = 'a'; END $$;", true, false)]
    [InlineData("ALTER TABLE \"Users\" DROP COLUMN \"Old\";", false, false)]
    [InlineData("SET LOCAL lock_timeout = '5s'; ALTER TABLE \"Users\" DROP COLUMN \"Old\";", false, true)]
    [InlineData("SET LOCAL lock_timeout = '0s'; ALTER TABLE \"Users\" DROP COLUMN \"Old\";", false, false)]
    [InlineData("SET LOCAL lock_timeout = '5s'; ALTER TABLE \"Users\" ALTER COLUMN \"Name\" TYPE varchar(30);", false, false)]
    [InlineData("SET LOCAL lock_timeout = '5s'; ALTER TABLE \"Habits\" ADD CONSTRAINT \"FK\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\" (\"Id\");", false, false)]
    public void RawSqlRules(string sql, bool suppressed, bool accepted)
        => MigrationLockGate.Check(Id, [Sql(sql, suppressed)], Policy).Any().Should().Be(!accepted);

    [Fact]
    public void ConstraintsNeedSeparateValidation_AndNotNullNeedsItsValidatedCheck()
    {
        var add = Sql("SET LOCAL lock_timeout = '5s'; ALTER TABLE \"Habits\" ADD CONSTRAINT \"RequiredTitle\" CHECK (\"Title\" IS NOT NULL) NOT VALID;");
        var validate = Sql("ALTER TABLE \"Habits\" VALIDATE CONSTRAINT \"RequiredTitle\";");
        var required = Sql("ALTER TABLE \"Habits\" ALTER COLUMN \"Title\" SET NOT NULL;");
        MigrationLockGate.Check(Id, [add, validate, required], Policy).Should().BeEmpty();
        MigrationLockGate.Check(Id, [add, required], Policy).Should().NotBeEmpty();
        MigrationLockGate.Check(Id, [required], Policy).Should().NotBeEmpty();
        MigrationLockGate.Check(Id, [Sql(add.Sql + validate.Sql)], Policy).Should().NotBeEmpty();
        var fk = Sql("SET LOCAL lock_timeout = '5s'; ALTER TABLE \"Habits\" ADD CONSTRAINT \"FK\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\" (\"Id\") NOT VALID;");
        MigrationLockGate.Check(Id, [fk, Sql("ALTER TABLE \"Habits\" VALIDATE CONSTRAINT \"FK\";")], Policy).Should().BeEmpty();
    }

    [Fact]
    public void FluentConstraintsTypeRewritesAndDropsFail_NewTableIndexesPass()
    {
        MigrationLockGate.Check(Id, [new AlterColumnOperation { Table = "Users", Name = "Name", ColumnType = "varchar(30)", OldColumn = new AddColumnOperation { ColumnType = "text" } }], Policy).Should().Contain(failure => failure.Contains("column-rewrite"));
        MigrationLockGate.Check(Id, [new AddForeignKeyOperation { Table = "Habits", Name = "FK" }], Policy).Should().NotBeEmpty();
        MigrationLockGate.Check(Id, [new AddCheckConstraintOperation { Table = "Habits", Name = "CK" }], Policy).Should().NotBeEmpty();
        MigrationLockGate.Check(Id, [new DropColumnOperation { Table = "Habits", Name = "Old" }], Policy).Should().NotBeEmpty();
        MigrationLockGate.Check(Id, [new CreateTableOperation { Name = "New" }, new CreateIndexOperation { Table = "New", Name = "ix" }], Policy).Should().BeEmpty();
    }

    [Fact]
    public void ExceptionRequiresReviewedEvidenceForTheNamedRuleOnly()
    {
        var operation = Sql("ALTER TABLE \"Users\" ALTER COLUMN \"Name\" TYPE varchar(30);");
        var policy = Policy;
        policy.Exceptions[Id] = [new("column-rewrite", 12, 10, "https://example.test/review", "Reviewed small lookup table"), new("lock-timeout", 12, 10, "https://example.test/review", "Reviewed lookup lock")];
        MigrationLockGate.Check(Id, [operation], policy).Should().BeEmpty();
        policy.Exceptions.Remove(Id);
        MigrationLockGate.Check(Id, [operation], policy).Should().NotBeEmpty();
        policy.Exceptions[Id] = [new("column-rewrite", -1, 0, "unreviewed", "")];
        MigrationLockGate.Check(Id, [operation], policy).Should().NotBeEmpty();
    }

    [Fact]
    public void EveryMigrationAfterTheAppliedBaselineMeetsLockRules()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Orbit.slnx"))) root = root.Parent;
        var policy = JsonSerializer.Deserialize<MigrationLockPolicy>(File.ReadAllText(Path.Combine(root!.FullName, ".github/migration-locks.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        policy.BaselineId.Should().Be(Policy.BaselineId, "raising the applied baseline must not exempt new migrations");
        policy.Reason.Should().Be("applied in production before this gate");
        var migrations = typeof(CompleteGeneralTasks).Assembly.GetTypes().Where(type => typeof(Migration).IsAssignableFrom(type) && !type.IsAbstract);
        var failures = migrations.SelectMany(type =>
        {
            var migration = (Migration)Activator.CreateInstance(type)!;
            migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
            return MigrationLockGate.Check(type.GetCustomAttribute<MigrationAttribute>()!.Id, migration.UpOperations, policy);
        });
        failures.Should().BeEmpty();
    }
}
