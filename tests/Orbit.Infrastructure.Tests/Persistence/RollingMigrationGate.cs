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
}

internal sealed record DeploymentBarrier(
    string ExpandCommit,
    string DeployedCommit,
    string EvidenceUrl,
    string Condition,
    string WebDrainEvidence,
    string BackgroundDrainEvidence);
