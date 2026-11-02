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
        var add = Sql("SET lock_timeout = '5s'; ALTER TABLE \"Habits\" ADD CONSTRAINT \"RequiredTitle\" CHECK (\"Title\" IS NOT NULL) NOT VALID;", true);
        var validate = Sql("SET lock_timeout = '5s'; ALTER TABLE \"Habits\" VALIDATE CONSTRAINT \"RequiredTitle\";", true);
        var required = Sql("SET lock_timeout = '5s'; ALTER TABLE \"Habits\" ALTER COLUMN \"Title\" SET NOT NULL;");
        MigrationLockGate.Check(Id, [add, validate, required], Policy).Should().BeEmpty();
        MigrationLockGate.Check(Id, [add, required], Policy).Should().NotBeEmpty();
        MigrationLockGate.Check(Id, [required], Policy).Should().NotBeEmpty();
        MigrationLockGate.Check(Id, [Sql(add.Sql + validate.Sql)], Policy).Should().NotBeEmpty();
        var fk = Sql("SET lock_timeout = '5s'; ALTER TABLE \"Habits\" ADD CONSTRAINT \"FK\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\" (\"Id\") NOT VALID;", true);
        MigrationLockGate.Check(Id, [fk, Sql("SET lock_timeout = '5s'; ALTER TABLE \"Habits\" VALIDATE CONSTRAINT \"FK\";", true)], Policy).Should().BeEmpty();
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

    [Theory]
    [InlineData("UPDATE \"Users\" SET \"Name\" = 'a' WHERE \"Id\" > 0 AND \"Id\" <= 100 OR true;")]
    [InlineData("UPDATE \"Users\" SET \"Name\" = 'a' WHERE true /* WHERE \"Id\" > 0 AND \"Id\" <= 100 */;")]
    public void UnboundedBatchCannotUseOrOrCommentsAsKeyEvidence(string sql)
        => MigrationLockGate.Check(Id, [Sql(sql, true)], Policy).Should().NotBeEmpty();

    [Fact]
    public void ValidationCannotBorrowACheckFromAnotherTable()
    {
        var operations = new[] {
            Sql("SET LOCAL lock_timeout = '5s'; ALTER TABLE \"Tags\" ADD CONSTRAINT \"RequiredTitle\" CHECK (\"Title\" IS NOT NULL) NOT VALID;"),
            Sql("ALTER TABLE \"Habits\" VALIDATE CONSTRAINT \"RequiredTitle\";"),
            Sql("ALTER TABLE \"Habits\" ALTER COLUMN \"Title\" SET NOT NULL;") };
        MigrationLockGate.Check(Id, operations, Policy).Should().NotBeEmpty();
    }

    [Fact]
    public void ReleaseSafetyConfigSeedFollowsAppliedQuotaSeedWithoutLockExceptions()
    {
        var migration = new ReleaseSafetyAppConfig();
        var id = migration.GetType().GetCustomAttribute<MigrationAttribute>()!.Id;
        var appliedId = typeof(SeedPublicEndpointAbuseQuotas).GetCustomAttribute<MigrationAttribute>()!.Id;
        string.CompareOrdinal(id, appliedId).Should().BePositive("the unapplied seed must follow the applied production migration");
        ReadPolicy().Exceptions.Should().NotContainKey(id);
        MigrationLockGate.Check(id, migration.UpOperations, Policy).Should().BeEmpty();
    }

    [Fact]
    public void AppliedQuotaSeedUsesOnlyItsRecordedLockTimeoutException()
    {
        var migration = new SeedPublicEndpointAbuseQuotas();
        var id = migration.GetType().GetCustomAttribute<MigrationAttribute>()!.Id;
        var policy = ReadPolicy();
        var evidence = policy.Exceptions.Should().ContainKey(id).WhoseValue.Should().ContainSingle().Subject;
        evidence.Rule.Should().Be("lock-timeout");
        evidence.RowCount.Should().Be(26);
        evidence.ExpectedLockMilliseconds.Should().BePositive();
        evidence.EvidenceUrl.Should().Be("https://github.com/thomasluizon/orbit-api/actions/runs/41427050907");
        evidence.Reason.Should().Be("applied in production by Release API run 41427050907 before this gate; AppConfigs held 26 rows in the 2026-11-02T12:28Z export");
        MigrationLockGate.Check(id, migration.UpOperations, policy).Should().BeEmpty();
        MigrationLockGate.Check(id, [new CreateIndexOperation { Table = "AppConfigs", Name = "IX_Fixture", Columns = ["Value"] }], policy)
            .Should().ContainSingle().Which.Should().Contain("concurrent-index");
        policy.Exceptions.Remove(id);
        MigrationLockGate.Check(id, migration.UpOperations, policy).Should().ContainSingle().Which.Should().Contain("lock-timeout");
    }

    [Theory]
    [InlineData(typeof(PersistPlayCouponCancellation), "lock-timeout", 0, "41465351944", "ProcessedPlayNotifications", 2)]
    [InlineData(typeof(AddReminderScheduleProbes), "batched-data", 4499, "41465351944", "Habits", 1)]
    [InlineData(typeof(PersistNotificationRecovery), "lock-timeout", 0, "41465351944", "ProcessedPlayNotifications", 2)]
    [InlineData(typeof(PersistStripeCustomerErasureRecovery), "lock-timeout", 187, "41449803649", "Users", 2)]
    public void AppliedRecoveryMigrationsUseOnlyTheirRecordedRuleExceptions(Type migrationType, string rule, long rowCount, string releaseRun, string table, int findingCount)
    {
        var migration = (Migration)Activator.CreateInstance(migrationType)!;
        migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
        var id = migrationType.GetCustomAttribute<MigrationAttribute>()!.Id;
        var policy = ReadPolicy();
        var evidence = policy.Exceptions.Should().ContainKey(id).WhoseValue.Should().ContainSingle().Subject;
        evidence.Rule.Should().Be(rule);
        evidence.RowCount.Should().Be(rowCount);
        evidence.ExpectedLockMilliseconds.Should().BePositive();
        evidence.EvidenceUrl.Should().Be($"https://github.com/thomasluizon/orbit-api/actions/runs/{releaseRun}");
        var reason = $"applied in production by Release API run {releaseRun} before this gate; {table} held {rowCount} rows in the dpg-dasotg8473hc739a5gpg-a/2026-11-02T14:46Z export";
        if (migration is AddReminderScheduleProbes)
            reason += "; ReminderScheduleProbes was created by this migration and held 0 rows when it ran";
        evidence.Reason.Should().Be(reason);
        MigrationLockGate.Check(id, migration.UpOperations, policy).Should().BeEmpty();
        MigrationLockGate.Check(id, [new CreateIndexOperation { Table = table, Name = "IX_Fixture", Columns = ["Id"] }], policy)
            .Should().ContainSingle().Which.Should().Contain("concurrent-index");
        policy.Exceptions.Remove(id);
        var failures = MigrationLockGate.Check(id, migration.UpOperations, policy);
        failures.Should().HaveCount(findingCount).And.OnlyContain(failure => failure.Contains($": {rule}:"));
    }

    [Fact]
    public void EveryMigrationAfterTheAppliedBaselineMeetsLockRules()
    {
        var policy = ReadPolicy();
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

    private static MigrationLockPolicy ReadPolicy()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Orbit.slnx"))) root = root.Parent;
        return JsonSerializer.Deserialize<MigrationLockPolicy>(File.ReadAllText(Path.Combine(root!.FullName, ".github/migration-locks.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
