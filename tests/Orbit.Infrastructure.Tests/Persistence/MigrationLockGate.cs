using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Orbit.Infrastructure.Tests.Persistence;

internal sealed record MigrationLockEvidence(string Rule, long RowCount, int ExpectedLockMilliseconds, string EvidenceUrl, string Reason);
internal sealed record MigrationLockPolicy(string BaselineId, string Reason, Dictionary<string, MigrationLockEvidence[]> Exceptions);

internal sealed class MigrationLockGate(string id, MigrationLockPolicy policy)
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private readonly List<string> failures = [];
    private readonly HashSet<string> createdTables = new(StringComparer.Ordinal);
    private readonly HashSet<string> notValid = new(StringComparer.Ordinal);
    private readonly HashSet<string> validated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> nullChecks = new(StringComparer.Ordinal);
    private bool lockTimeout;

    internal static IReadOnlyList<string> Check(string id, IReadOnlyList<MigrationOperation> operations, MigrationLockPolicy policy)
    {
        if (string.CompareOrdinal(id, policy.BaselineId) <= 0) return [];
        var gate = new MigrationLockGate(id, policy);
        foreach (var operation in operations) gate.Inspect(operation);
        foreach (var constraint in gate.notValid.Except(gate.validated))
            gate.Fail("not-valid-constraint", $"Constraint {constraint} needs a separate VALIDATE CONSTRAINT step.");
        return gate.failures;
    }

    private void Fail(string rule, string fix)
    {
        if (policy.Exceptions.TryGetValue(id, out var entries) && entries.Any(entry => entry.Rule == rule && ValidEvidence(entry))) return;
        failures.Add($"{id}: {rule}: {fix} Or record this rule, row count, expected lock milliseconds and reviewed evidence in .github/migration-locks.json.");
    }

    private static bool ValidEvidence(MigrationLockEvidence entry) => entry.RowCount >= 0 && entry.ExpectedLockMilliseconds > 0
        && !string.IsNullOrWhiteSpace(entry.Reason) && Uri.TryCreate(entry.EvidenceUrl, UriKind.Absolute, out var url) && url.Scheme == "https";
    private static bool Matches(string sql, string pattern) => Regex.IsMatch(sql, pattern, Options);
    private void RequireTimeout(string operation)
    {
        if (!lockTimeout) Fail("lock-timeout", $"{operation} requires preceding SET lock_timeout.");
    }
    private void Index(string name, bool concurrent, bool suppressed)
    {
        if (!concurrent || !suppressed) Fail("concurrent-index", $"Index {name} on an existing table must use CONCURRENTLY with SuppressTransaction.");
        if (suppressed) lockTimeout = false;
    }

    private void Inspect(MigrationOperation operation)
    {
        switch (operation)
        {
            case CreateTableOperation table: createdTables.Add(table.Name); break;
            case CreateIndexOperation index when !createdTables.Contains(index.Table):
                Index(index.Table + "." + index.Name, index.FindAnnotation("Npgsql:CreatedConcurrently")?.Value is true, true); break;
            case DropIndexOperation index when !createdTables.Contains(index.Table ?? ""):
                Fail("concurrent-index", $"Drop index {index.Name} using raw DROP INDEX CONCURRENTLY with SuppressTransaction; the provider does not suppress fluent drops."); break;
            case AddForeignKeyOperation key when !createdTables.Contains(key.Table):
                Fail("not-valid-constraint", $"Foreign key {key.Table}.{key.Name} must be raw ADD CONSTRAINT NOT VALID, then a separate VALIDATE CONSTRAINT step."); break;
            case AddCheckConstraintOperation check when !createdTables.Contains(check.Table):
                Fail("not-valid-constraint", $"Check {check.Table}.{check.Name} must be added NOT VALID, then validated in a separate step."); break;
            case AlterColumnOperation column when !createdTables.Contains(column.Table): InspectColumn(column); break;
            case ColumnOperation column when !createdTables.Contains(column.Table): RequireTimeout($"ALTER TABLE {column.Table}"); break;
            case ITableMigrationOperation table when !createdTables.Contains(table.Table): RequireTimeout($"ALTER TABLE {table.Table}"); break;
            case UpdateDataOperation or DeleteDataOperation:
                Fail("batched-data", "Data UPDATE/DELETE must use keyed WHERE batches, commits per batch and SuppressTransaction."); break;
            case SqlOperation sql: InspectSql(sql); break;
        }
    }

    private void InspectColumn(AlterColumnOperation column)
    {
        if (column.ColumnType != column.OldColumn.ColumnType || column.ClrType != column.OldColumn.ClrType)
            Fail("column-rewrite", $"Type change {column.Table}.{column.Name} can rewrite the table; use a NOT VALID length check instead.");
        if (!column.IsNullable && column.OldColumn.IsNullable)
            Fail("validated-not-null", $"Use a validated CHECK ({column.Name} IS NOT NULL) before a raw SET NOT NULL step.");
        RequireTimeout($"ALTER TABLE {column.Table}");
    }

    private void InspectSql(SqlOperation sql)
    {
        var masked = RollingMigrationGate.MaskQuotedSqlAndComments(sql.Sql);
        if (masked is null || RollingMigrationGate.SplitTopLevel(masked, ';') is not { } statements)
        {
            Fail("review-sql", "Unparseable SQL requires reviewed lock evidence.");
            return;
        }
        if (Matches(masked, @"(?:^|;)\s*(?:DO|EXECUTE|CALL)\b"))
            Fail("batched-data", "Procedural or dynamic SQL requires reviewed evidence proving keyed batches and a commit per batch.");
        foreach (var statement in statements)
        {
            var raw = statement.Trim();
            if (Matches(raw, @"^SET\s+(?:LOCAL\s+|SESSION\s+)?lock_timeout\s*(?:=|TO)\s*'literal'$")) SetTimeout(sql.Sql);
            if (Matches(raw, @"\b(?:CREATE\s+(?:UNIQUE\s+)?INDEX|DROP\s+INDEX)\b"))
                Index(raw, Matches(raw, @"\bINDEX\s+CONCURRENTLY\b"), sql.SuppressTransaction);
            if (Matches(raw, @"\b(?:UPDATE|DELETE\s+FROM)\b")) InspectBatch(sql, statements);
            if (Matches(raw, @"^ALTER\s+TABLE\b")) InspectAlter(raw, sql.Sql);
        }
        if (sql.SuppressTransaction || Matches(masked, @"\bRESET\s+lock_timeout\b")) lockTimeout = false;
    }

    private void SetTimeout(string sql)
    {
        lockTimeout = Matches(sql, @"lock_timeout\s*(?:=|TO)\s*'(?:[1-9]\d{0,2}ms|[1-5]s)'");
        if (!lockTimeout) Fail("lock-timeout", "Use a nonzero lock_timeout no greater than five seconds.");
    }

    private void InspectBatch(SqlOperation sql, List<string> statements)
    {
        var keyed = Matches(sql.Sql, "WHERE\\s+\"Id\"\\s*>\\s*[^;]+\\s+AND\\s+\"Id\"\\s*<=\\s*[^;]+");
        var writes = statements.Count(statement => Matches(statement, @"\b(?:UPDATE|DELETE\s+FROM)\b"));
        if (!sql.SuppressTransaction || !keyed || writes != 1 || Matches(sql.Sql, @"\b(?:BEGIN|START\s+TRANSACTION)\b"))
            Fail("batched-data", "Use a keyed Id WHERE range per Sql operation and SuppressTransaction so each bounded batch commits independently.");
    }

    private void InspectAlter(string masked, string sql)
    {
        RequireTimeout("ALTER TABLE");
        if (Matches(masked, @"\bTYPE\b")) Fail("column-rewrite", "ALTER TYPE can rewrite a table; use a NOT VALID length check instead.");
        if (Matches(masked, @"\bADD\s+(?:CONSTRAINT\s+""identifier""\s+)?(?:FOREIGN\s+KEY|CHECK)\b") && !Matches(masked, @"\bNOT\s+VALID\b"))
            Fail("not-valid-constraint", "Add foreign keys and checks NOT VALID; VALIDATE CONSTRAINT in a separate Sql operation.");
        if (Matches(masked, @"\bNOT\s+VALID\b")) AddConstraint(sql);
        if (Matches(masked, @"\bVALIDATE\s+CONSTRAINT\b")) ValidateConstraint(sql);
        if (Matches(masked, @"\bSET\s+NOT\s+NULL\b")) RequireValidatedCheck(sql);
    }

    private static string Constraint(string sql) => Regex.Match(sql, "CONSTRAINT\\s+\"(?<name>[^\"]+)\"", Options).Groups["name"].Value;
    private void AddConstraint(string sql)
    {
        var identity = Constraint(sql);
        if (identity.Length == 0) Fail("review-sql", "Use quoted constraint identifiers so validation evidence can be matched.");
        notValid.Add(identity);
        var check = Regex.Match(sql, "CHECK\\s*\\(\\s*\"(?<column>[^\"]+)\"\\s+IS\\s+NOT\\s+NULL\\s*\\)", Options);
        if (check.Success) nullChecks[check.Groups["column"].Value] = identity;
    }
    private void ValidateConstraint(string sql)
    {
        var identity = Constraint(sql);
        if (!notValid.Contains(identity) || Matches(sql, @"\bADD\s+CONSTRAINT\b"))
            Fail("not-valid-constraint", "VALIDATE CONSTRAINT must follow a separate NOT VALID operation.");
        validated.Add(identity);
    }
    private void RequireValidatedCheck(string sql)
    {
        var column = Regex.Match(sql, "ALTER\\s+(?:COLUMN\\s+)?\"(?<column>[^\"]+)\"\\s+SET\\s+NOT\\s+NULL", Options).Groups["column"].Value;
        if (!nullChecks.TryGetValue(column, out var constraint) || !validated.Contains(constraint))
            Fail("validated-not-null", "SET NOT NULL requires a previously validated check for the same column.");
    }
}
