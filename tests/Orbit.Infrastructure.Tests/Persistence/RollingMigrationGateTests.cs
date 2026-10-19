using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class RollingMigrationGateTests
{
    private const string HistoricalId = "20260823203026_DailyAiQuotaByLocalDate";
    private const string ExpandCommit = "a1c666f1eac4c6544cb2a896345fd1a69cbe88fa";
    private const string DeployedCommit = "7472203c7450bdd914c68282fefa905d9b432653";

    [Fact]
    public void HistoricalQuotaMigration_WithoutBarrier_IsRejected()
    {
        var historicalCopy = new DailyAiQuotaByLocalDate
        {
            ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL"
        };

        var failure = RollingMigrationGate.Check(HistoricalId, historicalCopy.UpOperations)
            .Should().ContainSingle().Subject;
        failure.Should().Contain("deployment barrier").And.Contain("DropColumnOperation").And.Contain("RenameColumnOperation");
    }

    [Fact]
    public void HistoricalQuotaMigration_WithLaterDeploymentBarrier_IsAccepted()
    {
        var historicalCopy = new DailyAiQuotaByLocalDate { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };

        RollingMigrationGate.Check(HistoricalId, historicalCopy.UpOperations,
                Barriers(HistoricalId), (_, _) => true)
            .Should().BeEmpty();
    }

    [Fact]
    public void MergedHabitLogContract_RequiresRecordedDeploymentBarrier()
    {
        const string id = "20261019011031_ContractHabitLogSlip";
        var migration = new ContractHabitLogSlip { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };
        var root = RepositoryRoot();

        RollingMigrationGate.Check(id, migration.UpOperations).Should().NotBeEmpty();
        RollingMigrationGate.Check(id, migration.UpOperations, ReadBarriers(root),
                (commit, descendant) => Git(root, "merge-base", "--is-ancestor", commit, descendant).ExitCode == 0, "HEAD")
            .Should().BeEmpty();
    }

    [Fact]
    public void AdditiveExpandMigration_WithDestructiveDown_IsAccepted()
    {
        var migration = new RecordHabitLogSlip { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };

        RollingMigrationGate.Check("expand", migration.UpOperations).Should().BeEmpty();
        RollingMigrationGate.Check("rollback", migration.DownOperations).Should().NotBeEmpty();
    }

    [Fact]
    public void LaterContract_RequiresBothWebAndBackgroundDrainEvidence()
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.DropTable("SentProactiveCheckins");
        var barriers = Barriers("contract");

        RollingMigrationGate.Check("contract", builder.Operations).Should().NotBeEmpty();
        RollingMigrationGate.Check("contract", builder.Operations, barriers, (_, _) => true).Should().BeEmpty();
        barriers["contract"] = barriers["contract"] with { BackgroundDrainEvidence = "" };
        RollingMigrationGate.Check("contract", builder.Operations, barriers, (_, _) => true).Should().NotBeEmpty();
        barriers["contract"] = CompleteBarrier() with { WebDrainEvidence = "" };
        RollingMigrationGate.Check("contract", builder.Operations, barriers, (_, _) => true).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(ExpandCommit)]
    [InlineData(DeployedCommit)]
    public void CommitOutsideBase_CannotAuthorizeSameReleaseContract(string unmergedCommit)
    {
        var migration = new DailyAiQuotaByLocalDate { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };

        RollingMigrationGate.Check(HistoricalId, migration.UpOperations, Barriers(HistoricalId), (commit, _) => commit != unmergedCommit)
            .Should().ContainSingle().Which.Should().Contain("earlier release");
    }

    [Fact]
    public void DeployedCommitWithoutExpand_IsRejected()
    {
        var migration = new DailyAiQuotaByLocalDate { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };

        RollingMigrationGate.Check(HistoricalId, migration.UpOperations, Barriers(HistoricalId), (_, descendant) => descendant == "base")
            .Should().ContainSingle().Which.Should().Contain("must contain expandCommit");
    }

    [Theory]
    [InlineData("commit", "https://github.com/thomasluizon/orbit-api/pull/764", "condition")]
    [InlineData(ExpandCommit, "not-a-url", "condition")]
    [InlineData(ExpandCommit, "http://github.com/thomasluizon/orbit-api/pull/764", "condition")]
    [InlineData(ExpandCommit, "https://github.com/thomasluizon/orbit-api/pull/764", "")]
    public void IncompleteBarrier_IsRejected(string expandCommit, string evidenceUrl, string condition)
    {
        var barriers = Barriers("contract");
        barriers["contract"] = CompleteBarrier() with { ExpandCommit = expandCommit, EvidenceUrl = evidenceUrl, Condition = condition };
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.DropColumn("OldColumn", "Users");

        RollingMigrationGate.Check("contract", builder.Operations, barriers, (_, _) => true).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("DROP TRIGGER IF EXISTS \"SetHabitLogIsSlipOnInsert\" ON \"HabitLogs\";")]
    [InlineData("DROP FUNCTION IF EXISTS \"SetHabitLogIsSlipOnInsert\"();")]
    [InlineData("ALTER TABLE \"Users\" DROP \"OldColumn\";")]
    [InlineData("ALTER TABLE \"Users\" DROP COLUMN \"OldColumn\";")]
    [InlineData("ALTER TABLE \"Users\" RENAME COLUMN \"OldColumn\" TO \"NewColumn\";")]
    [InlineData("ALTER TABLE \"HabitLogs\" ALTER /* compatibility */ COLUMN \"IsSlip\" SET NOT NULL;")]
    [InlineData("ALTER TABLE \"HabitLogs\" ALTER \"IsSlip\" TYPE text;")]
    [InlineData("ALTER TABLE \"Users\" RENAME TO \"Accounts\";")]
    [InlineData("ALTER TABLE \"Users\" DROP\n-- compatibility\nCOLUMN \"OldColumn\";")]
    [InlineData("SELECT '--'; ALTER TABLE \"Users\" DROP COLUMN \"OldColumn\";")]
    [InlineData("TRUNCATE TABLE \"Users\";")]
    [InlineData("DO $$ BEGIN EXECUTE 'DR' || 'OP TABLE users'; END $$;")]
    [InlineData("CREATE OR REPLACE FUNCTION classify() RETURNS bool AS $$ SELECT true $$ LANGUAGE SQL;")]
    public void RawContractSql_RequiresBarrier(string sql)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.Sql(sql);

        RollingMigrationGate.Check("contract", builder.Operations).Should().NotBeEmpty();
        RollingMigrationGate.Check("contract", builder.Operations, Barriers("contract"), (_, _) => true).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"RequiredValue\" text NOT NULL;")]
    [InlineData("ALTER TABLE \"HabitLogs\" DISABLE TRIGGER \"SetHabitLogIsSlipOnInsert\";")]
    [InlineData("ALTER TABLE \"HabitLogs\" ADD CONSTRAINT \"RequireIsSlip\" CHECK (\"IsSlip\" IS NOT NULL) NOT VALID;")]
    [InlineData("ALTER TABLE \"Users\" SET SCHEMA archive;")]
    public void UnsafeAlterTableSql_RequiresBarrier(string sql)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.Sql(sql);

        RollingMigrationGate.Check("contract", builder.Operations).Should().NotBeEmpty();
        RollingMigrationGate.Check("contract", builder.Operations, Barriers("contract"), (_, _) => true).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"Optional\" text, DISABLE TRIGGER ALL;")]
    [InlineData("ALTER TABLE \"Users\" DISABLE TRIGGER ALL, ADD COLUMN \"Optional\" text;")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"Optional\" text; ALTER TABLE \"Users\" SET SCHEMA archive;")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"Optional\" text DEFAULT 'a,b', ADD COLUMN \"Required\" text NOT NULL;")]
    [InlineData("ALTER TABLE \"Users\" ENABLE TRIGGER ALL;")]
    [InlineData("ALTER TABLE \"Users\" ADD CONSTRAINT fk FOREIGN KEY (\"Id\") REFERENCES \"Accounts\" (\"Id\") NOT VALID;")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"Value\" text DEFAULT '' CHECK (length(\"Value\") > 0);")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"Value\" required_domain;")]
    [InlineData("ALTER TABLE \"Users--\" DISABLE /* compatibility */ TRIGGER ALL;")]
    [InlineData("SELECT '--'; ALTER TABLE \"Users\" DISABLE TRIGGER ALL;")]
    [InlineData("SELECT 'it''s /* text */'; ALTER TABLE \"Users\" SET SCHEMA archive;")]
    [InlineData("ALTER TABLE \"Users\" /* outer /* nested */ comment */ SET SCHEMA archive;")]
    public void UnclassifiedOrMixedAlterTableActions_RequireBarrier(string sql)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.Sql(sql);

        RollingMigrationGate.Check("contract", builder.Operations).Should().NotBeEmpty();
        RollingMigrationGate.Check("contract", builder.Operations, Barriers("contract"), (_, _) => true).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"NewColumn\" text NULL;")]
    [InlineData("ALTER\n  TABLE \"Users\" ADD COLUMN \"NewColumn\" text NULL;")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"NewColumn\" text;")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"NewColumn\" text NOT NULL DEFAULT '';")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"NewColumn\" boolean DEFAULT false NOT NULL;")]
    [InlineData("ALTER TABLE IF EXISTS ONLY public.\"Users\" ADD COLUMN IF NOT EXISTS \"NewColumn\" text;")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"First\" numeric(10, 2) DEFAULT round(1.25, 1), ADD COLUMN \"Second\" text;")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"First,Column\" text DEFAULT 'a,b;() -- /*', ADD COLUMN \"Second\" text;")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"A\"\",B\" text DEFAULT 'it''s,a', ADD COLUMN \"Second\" text;")]
    [InlineData("ALTER TABLE \"Users\" ADD COLUMN \"First\" text DEFAULT $value$a,b;()$value$, ADD COLUMN \"Second\" text;")]
    [InlineData("ALTER TABLE \"Users\" ADD /* outer /* nested */ comment */ COLUMN \"First\" text, -- comma,\n ADD COLUMN \"Second\" text;")]
    [InlineData("UPDATE \"HabitLogs\" SET \"IsSlip\" = false WHERE \"IsSlip\" IS NULL;")]
    [InlineData("CREATE INDEX IF NOT EXISTS ix_users ON \"Users\" (\"Id\");")]
    [InlineData("CREATE TRIGGER classify BEFORE INSERT ON \"HabitLogs\" FOR EACH ROW EXECUTE\n  FUNCTION classify();")]
    public void AdditiveSql_IsAccepted(string sql)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.Sql(sql);

        RollingMigrationGate.Check("expand", builder.Operations).Should().BeEmpty();
    }

    [Fact]
    public void FluentSchemaContractions_RequireBarrier()
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.RenameTable("Users", newName: "Accounts");
        builder.DropSchema("legacy");
        builder.AlterColumn<bool>("IsSlip", "HabitLogs", nullable: false, oldClrType: typeof(bool), oldNullable: true);
        builder.AddColumn<bool>("RequiredValue", "Users", nullable: false);

        foreach (var operation in builder.Operations)
            RollingMigrationGate.Check("contract", [operation]).Should().NotBeEmpty(operation.GetType().Name);
    }

    [Fact]
    public void NullableAndDefaultedAdditions_AreAccepted()
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.AddColumn<string>("OptionalValue", "Users", nullable: true);
        builder.AddColumn<bool>("DefaultedValue", "Users", nullable: false, defaultValue: false);
        builder.CreateTable("NewTable", columns: table => new { Id = table.Column<int>(nullable: false) });

        RollingMigrationGate.Check("expand", builder.Operations).Should().BeEmpty();
    }

    [Fact]
    public void ChangedFiles_SelectOnlyTheirMigrationsAndDeduplicateDesigner()
    {
        var paths = new[]
        {
            $"src/Orbit.Infrastructure/Migrations/{HistoricalId}.cs",
            $"src/Orbit.Infrastructure/Migrations/{HistoricalId}.Designer.cs",
            "src/Orbit.Infrastructure/Migrations/OrbitDbContextModelSnapshot.cs",
            "src/Orbit.Domain/Entities/User.cs"
        };

        RollingMigrationGate.MigrationIds(paths).Should().Equal(HistoricalId);
        RollingMigrationGate.MigrationIds([]).Should().BeEmpty();
    }

    [Fact]
    public void ChangedMigrations_HaveDeploymentBarriers()
    {
        var baseRef = Environment.GetEnvironmentVariable("ORBIT_MIGRATION_GUARD_BASE");
        if (string.IsNullOrWhiteSpace(baseRef))
            return;

        var root = RepositoryRoot();
        var changed = Git(root, "diff", "--name-only", "--diff-filter=ACMR", "-z", $"{baseRef}...HEAD", "--",
            "src/Orbit.Infrastructure/Migrations/*.cs");
        changed.ExitCode.Should().Be(0, changed.Error);
        var ids = RollingMigrationGate.MigrationIds(changed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        var barriers = ReadBarriers(root);
        var migrations = typeof(DailyAiQuotaByLocalDate).Assembly.GetTypes()
            .Where(type => typeof(Migration).IsAssignableFrom(type) && !type.IsAbstract)
            .ToDictionary(type => type.GetCustomAttribute<MigrationAttribute>()?.Id
                ?? throw new InvalidOperationException($"Migration {type.Name} has no ID."));
        var failures = new List<string>();
        foreach (var id in ids)
        {
            migrations.Should().ContainKey(id, "every changed migration must be compiled and inspected");
            var migration = Activator.CreateInstance(migrations[id]) as Migration
                ?? throw new InvalidOperationException($"Cannot construct migration {id}.");
            migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
            failures.AddRange(RollingMigrationGate.Check(id, migration.UpOperations, barriers,
                (commit, descendant) => Git(root, "merge-base", "--is-ancestor", commit, descendant).ExitCode == 0, baseRef));
        }

        failures.Should().BeEmpty("rolling deployment contracts require reviewed evidence: {0}", string.Join(Environment.NewLine, failures));
    }

    private static Dictionary<string, DeploymentBarrier> Barriers(string id) => new() { [id] = CompleteBarrier() };

    private static Dictionary<string, DeploymentBarrier> ReadBarriers(string root) =>
        JsonSerializer.Deserialize<Dictionary<string, DeploymentBarrier>>(
            File.ReadAllText(Path.Combine(root, ".github", "migration-contracts.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Migration contracts must be a JSON object.");

    private static DeploymentBarrier CompleteBarrier() => new(
        ExpandCommit, DeployedCommit,
        "https://github.com/thomasluizon/orbit-api/pull/764#issuecomment-6257602012",
        "Every writer supplies its own IsSlip classification.",
        "The previous Render web deploy is deactivated.",
        "Background services and Hangfire workers in the previous deploy have stopped; no separate old worker remains.");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot locate Orbit.slnx.");
    }

    private static (int ExitCode, string Output, string Error) Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
