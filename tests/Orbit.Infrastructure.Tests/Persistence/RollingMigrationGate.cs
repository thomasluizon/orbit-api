using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Orbit.Infrastructure.Tests.Persistence;

internal static class RollingMigrationGate
{
    private static readonly Regex ContractSql = new(
        @"\b(?:DROP|RENAME|TRUNCATE)\b|\bEXECUTE(?!\s+(?:FUNCTION|PROCEDURE)\b)\b|\bALTER(?!\s+TABLE\b)\b|\bSET\s+NOT\s+NULL\b|\bCREATE\s+OR\s+REPLACE\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const RegexOptions SqlOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Identifier = "(?:\"identifier\"|[a-z_][a-z0-9_$]*)";
    private static readonly Regex AlterTable = new(
        @"\A\s*ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?" + Identifier
        + @"(?:\s*\.\s*" + Identifier + @")?\s*\*?\s+(?<actions>.+)\z", SqlOptions | RegexOptions.Singleline);
    private static readonly Regex AddColumn = new(
        @"\A\s*ADD\s+COLUMN\s+(?:IF\s+NOT\s+EXISTS\s+)?" + Identifier + @"\s+"
        + @"(?:text|boolean|bool|smallint|integer|int|bigint|real|double\s+precision|uuid|date|jsonb?|bytea"
        + @"|(?:numeric|decimal|varchar|character\s+varying)(?:\s*\(\s*\d+\s*(?:,\s*\d+\s*)?\))?"
        + @"|(?:timestamp|time)(?:\s*\(\s*\d+\s*\))?(?:\s+(?:with|without)\s+time\s+zone)?)"
        + @"(?:\s*\[\s*\])*(?<tail>\s.*|)\z", SqlOptions | RegexOptions.Singleline);

    private static bool RequiresSqlContract(string sql)
    {
        if (ContractSql.IsMatch(sql))
            return true;

        var masked = MaskQuotedSqlAndComments(sql);
        if (masked is null || SplitTopLevel(masked, ';') is not { } statements)
            return true;

        foreach (var statement in statements.Where(statement => Regex.IsMatch(statement, @"\bALTER\s+TABLE\b", SqlOptions)))
        {
            var table = AlterTable.Match(statement);
            if (!table.Success || SplitTopLevel(table.Groups["actions"].Value, ',') is not { } actions
                || actions.Any(action => !IsAdditiveColumn(action)))
                return true;
        }

        return false;
    }

    private static bool IsAdditiveColumn(string action)
    {
        var column = AddColumn.Match(action);
        if (!column.Success)
            return false;

        var tail = column.Groups["tail"].Value.Trim();
        if (tail.Length == 0 || tail.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Regex.IsMatch(tail, @"\b(?:CHECK|REFERENCES|UNIQUE|PRIMARY|CONSTRAINT|GENERATED|IDENTITY|COLLATE)\b", SqlOptions))
            return false;

        return Regex.IsMatch(tail, @"\A(?:(?:NOT\s+)?NULL\s+)?DEFAULT\s+\S", SqlOptions);
    }

    private static string? MaskQuotedSqlAndComments(string sql)
    {
        var masked = new StringBuilder();
        for (var i = 0; i < sql.Length;)
        {
            if (sql.AsSpan(i).StartsWith("--"))
            {
                while (i < sql.Length && sql[i] is not ('\r' or '\n'))
                    i++;
                masked.Append(' ');
            }
            else if (sql.AsSpan(i).StartsWith("/*"))
            {
                var depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql.AsSpan(i).StartsWith("/*")) { depth++; i += 2; }
                    else if (sql.AsSpan(i).StartsWith("*/")) { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0)
                    return null;
                masked.Append(' ');
            }
            else if (sql[i] is '\'' or '"')
            {
                var quote = sql[i++];
                var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i] == '\\' && quote == '\'')
                        return null;
                    if (sql[i++] != quote)
                        continue;
                    if (i < sql.Length && sql[i] == quote) { i++; continue; }
                    closed = true;
                    break;
                }
                if (!closed)
                    return null;
                masked.Append(quote == '"' ? "\"identifier\"" : "'literal'");
            }
            else if (sql[i] == '$' && Regex.Match(sql[i..], @"\A\$(?:[a-z_][a-z0-9_]*)?\$", SqlOptions) is { Success: true } tag)
            {
                var end = sql.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                if (end < 0)
                    return null;
                i = end + tag.Length;
                masked.Append("'literal'");
            }
            else
                masked.Append(sql[i++]);
        }

        return masked.ToString();
    }

    private static List<string>? SplitTopLevel(string sql, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')' && --depth < 0) return null;
            else if (sql[i] == separator && depth == 0)
            {
                parts.Add(sql[start..i]);
                start = i + 1;
            }
        }
        parts.Add(sql[start..]);
        return depth == 0 ? parts : null;
    }

    internal static IReadOnlyList<string> Check(
        string migrationId,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyDictionary<string, DeploymentBarrier>? barriers = null,
        Func<string, string, bool>? isAncestor = null,
        string baseRef = "base")
    {
        var contracts = operations.Where(RequiresContract).Select(operation => operation.GetType().Name)
            .Distinct().ToArray();
        if (contracts.Length == 0)
            return [];

        if (barriers is null || !barriers.TryGetValue(migrationId, out var barrier))
            return [$"{migrationId}: {string.Join(", ", contracts)} requires a deployment barrier in .github/migration-contracts.json."];

        var failures = new List<string>();
        foreach (var (name, commit) in new[] { ("expandCommit", barrier.ExpandCommit), ("deployedCommit", barrier.DeployedCommit) })
        {
            if (commit is null || !Regex.IsMatch(commit, @"\A[0-9a-f]{40}\z") || isAncestor is null || !isAncestor(commit, baseRef))
                failures.Add($"{migrationId}: deployment barrier {name} must be a full commit already in the PR base, from an earlier release.");
        }

        if (failures.Count == 0 && isAncestor is not null && !isAncestor(barrier.ExpandCommit, barrier.DeployedCommit))
            failures.Add($"{migrationId}: deployment barrier deployedCommit must contain expandCommit.");

        if (!Uri.TryCreate(barrier.EvidenceUrl, UriKind.Absolute, out var evidence) || evidence.Scheme != Uri.UriSchemeHttps)
            failures.Add($"{migrationId}: deployment barrier needs an HTTPS evidenceUrl.");
        if (string.IsNullOrWhiteSpace(barrier.Condition)
            || string.IsNullOrWhiteSpace(barrier.WebDrainEvidence)
            || string.IsNullOrWhiteSpace(barrier.BackgroundDrainEvidence))
            failures.Add($"{migrationId}: deployment barrier must record the compatibility condition and evidence that web and background processes drained.");

        return failures;
    }

    internal static IReadOnlyList<string> CheckRelease(
        IReadOnlyList<(string Id, IReadOnlyList<MigrationOperation> Operations)> migrations,
        IReadOnlyDictionary<string, DeploymentBarrier>? barriers = null,
        Func<string, string, bool>? isAncestor = null,
        string baseRef = "base")
    {
        var ordered = migrations.OrderBy(migration => migration.Id, StringComparer.Ordinal).ToArray();
        var operations = ordered.SelectMany(migration => migration.Operations).ToArray();
        var restores = ordered.SelectMany(migration => migration.Operations.OfType<AlterColumnOperation>()
            .Where(column => IsCompatibleBooleanRestore(column, migration.Operations))).ToHashSet();
        var compatible = new HashSet<MigrationOperation>(restores);
        foreach (var column in operations.OfType<AlterColumnOperation>())
        {
            if (!SimpleBoolean(column) || !SimpleBoolean(column.OldColumn)
                || !column.IsNullable || column.OldColumn.IsNullable
                || column.DefaultValue is not null || column.OldColumn.DefaultValue is not bool)
                continue;

            var later = operations.Skip(Array.IndexOf(operations, column) + 1)
                .OfType<AlterColumnOperation>()
                .FirstOrDefault(other => other.Schema == column.Schema && other.Table == column.Table && other.Name == column.Name);
            if (later is not null && restores.Contains(later))
                compatible.Add(column);
        }

        return ordered.SelectMany(migration => Check(migration.Id,
            migration.Operations.Where(operation => !compatible.Contains(operation)).ToArray(), barriers, isAncestor, baseRef)).ToArray();
    }

    private static bool IsCompatibleBooleanRestore(AlterColumnOperation column, IReadOnlyList<MigrationOperation> operations)
    {
        if (!SimpleBoolean(column) || !SimpleBoolean(column.OldColumn)
            || column.IsNullable || !column.OldColumn.IsNullable || column.DefaultValue is not bool enabled
            || column.Schema is not null
            || !Regex.IsMatch(column.Table, @"\A[A-Za-z_][A-Za-z0-9_]*\z")
            || !Regex.IsMatch(column.Name, @"\A[A-Za-z_][A-Za-z0-9_]*\z"))
            return false;

        var literal = enabled ? "TRUE" : "FALSE";
        var normalization = $"""
            CREATE FUNCTION "Normalize{column.Table}{column.Name}"() RETURNS trigger AS $normalize$
            BEGIN
                NEW."{column.Name}" := COALESCE(NEW."{column.Name}", {literal});
                RETURN NEW;
            END;
            $normalize$ LANGUAGE plpgsql;
            CREATE TRIGGER "Normalize{column.Name}OnWrite"
            BEFORE INSERT OR UPDATE ON "{column.Table}"
            FOR EACH ROW EXECUTE FUNCTION "Normalize{column.Table}{column.Name}"();
            """;
        var backfill = $"UPDATE \"{column.Table}\" SET \"{column.Name}\" = {literal} WHERE \"{column.Name}\" IS NULL;";
        var prefix = operations.TakeWhile(operation => !ReferenceEquals(operation, column)).OfType<SqlOperation>().ToArray();
        var triggerIndex = Array.FindIndex(prefix, sql => !sql.SuppressTransaction && NormalizeSql(sql.Sql) == NormalizeSql(normalization));
        var backfillIndex = Array.FindIndex(prefix, sql => !sql.SuppressTransaction && NormalizeSql(sql.Sql) == NormalizeSql(backfill));
        return triggerIndex >= 0 && backfillIndex > triggerIndex;
    }

    private static string NormalizeSql(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static bool SimpleBoolean(ColumnOperation column) => column.ClrType == typeof(bool) && column.ColumnType == "boolean"
        && column.DefaultValueSql is null && column.ComputedColumnSql is null && !column.IsRowVersion
        && column.MaxLength is null && column.Precision is null && column.Scale is null && column.Collation is null
        && !column.GetAnnotations().Any();

    private static bool RequiresContract(MigrationOperation operation) => operation switch
    {
        DropColumnOperation or RenameColumnOperation or DropTableOperation or RenameTableOperation
            or DropSchemaOperation or AlterColumnOperation => true,
        AddColumnOperation column => !column.IsNullable && column.DefaultValue is null
            && column.DefaultValueSql is null && column.ComputedColumnSql is null,
        SqlOperation sql => RequiresSqlContract(sql.Sql),
        _ => operation.IsDestructiveChange
    };

    internal static IReadOnlyList<string> MigrationIds(IEnumerable<string> paths) => paths
        .Where(path => path.StartsWith("src/Orbit.Infrastructure/Migrations/", StringComparison.Ordinal)
            && path.EndsWith(".cs", StringComparison.Ordinal)
            && !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
        .Select(path => Path.GetFileName(path).Replace(".Designer.cs", ".cs", StringComparison.Ordinal))
        .Select(Path.GetFileNameWithoutExtension)
        .OfType<string>()
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    internal static Dictionary<string, DeploymentBarrier> BarriersForRelease(
        IReadOnlyDictionary<string, DeploymentBarrier> current,
        IReadOnlyDictionary<string, DeploymentBarrier> baseline,
        IReadOnlySet<string> changedPaths)
    {
        var barriers = current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (id, barrier) in baseline)
            if (!changedPaths.Contains($"src/Orbit.Infrastructure/Migrations/{id}.cs"))
                barriers[id] = barrier;
        return barriers;
    }
}

internal sealed record DeploymentBarrier(
    string ExpandCommit,
    string DeployedCommit,
    string EvidenceUrl,
    string Condition,
    string WebDrainEvidence,
    string BackgroundDrainEvidence);
