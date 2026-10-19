using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Orbit.Infrastructure.Tests.Persistence;

internal static class RollingMigrationGate
{
    private static readonly Regex ContractSql = new(
        @"\b(?:DROP|RENAME|TRUNCATE)\b|\bEXECUTE(?!\s+(?:FUNCTION|PROCEDURE)\b)\b|\bALTER(?!\s+TABLE\b)\b|\bSET\s+NOT\s+NULL\b|\bCREATE\s+OR\s+REPLACE\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        SqlOperation sql => ContractSql.IsMatch(Regex.Replace(sql.Sql, @"/\*.*?\*/|--[^\r\n]*", " ", RegexOptions.Singleline)),
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
