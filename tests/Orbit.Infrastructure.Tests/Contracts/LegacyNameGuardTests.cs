using System.Text.RegularExpressions;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Contracts;

/// <summary>
/// Keeps the retired product name out of the source tree. A line under src may carry the old name
/// only inside an allowlisted text, and every allowlist entry names the one reason that text stays.
/// The pattern is case-sensitive on the capital A and on a lowercase name that no letter precedes,
/// because a plain case-insensitive match also hits unrelated words such as HasTrackedEntities.
/// </summary>
public class LegacyNameGuardTests
{
    private const string WireName = "wire name: a client, a stored row or an alert reads this exact text";
    private const string TableOrColumn = "table or column name: the database keeps the stored name";
    private const string HarnessName = "harness name: the owner types it and no person who uses the app sees it";
    private const string ConfigKey = "configuration or setting key: an environment keeps this exact text";
    private const string SeedText = "seed data text: a change needs a migration";
    private const string LockKey = "advisory lock key: instances in a rolling release must agree on it";

    private static readonly Regex LegacyName = new(@"Astra|ASTRA|(?<![A-Za-z])astra", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly (string Pattern, string Reason)[] Allowed =
    [
        (@"ASTRA_BLOCK_[A-Z_]+", WireName),
        (@"set_proactive_astra", WireName),
        (@"astra_[a-z_]+", WireName),
        (@"""profile\.proactive-astra\.write""", WireName),
        (@"/api/Profile/proactive-astra", WireName),
        (@"""proactive-astra""", WireName),
        (@"""hasTriedAstra""", WireName),
        (@"""proactiveAstraEnabled""", WireName),
        (@"""storedProactiveAstraEnabled""", WireName),
        (@"""setProactiveAstra""", WireName),
        (@"\$""proactiveAstra\.\{enabled\}""", WireName),
        (@"""astraAllowance""", WireName),
        (@"""chat\.read\.astraAllowance""", WireName),
        (@"""ProactiveAstraEnabled""", TableOrColumn),
        (@"""HasTriedAstra""", TableOrColumn),
        (@"""IssuedAstraBlocks""", TableOrColumn),
        (@"AstraGroundingEnforce", ConfigKey),
        (@"""IssuedAstraBlocksDays""", ConfigKey),
        (@"evals/astra", HarnessName),
        (@"\$""astra-blocks:\{request\.UserId\}""", LockKey),
        (@"\(string\?\)""[^""]*Astra[^""]*""", SeedText),
    ];

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    private static readonly HashSet<string> TextExtensions =
        [".cs", ".md", ".json", ".yml", ".yaml", ".xml", ".props", ".csproj", ".txt", ".html", ".css", ".js", ".sh", ".config", ".svg"];

    private static IEnumerable<string> SourceFiles()
    {
        var src = Path.Combine(Root(), "src");
        var skipped = new[] { "bin", "obj", "Migrations", "node_modules" };
        return Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(path => TextExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !Path.GetRelativePath(src, path).Split(Path.DirectorySeparatorChar).Any(part => skipped.Contains(part)));
    }

    private static (List<string> Violations, HashSet<string> Used) Scan()
    {
        var compiled = Allowed.Select(entry => (Regex: new Regex(entry.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)), entry.Pattern)).ToArray();
        var violations = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in SourceFiles())
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var remaining = lines[index];
                if (!LegacyName.IsMatch(remaining)) continue;
                foreach (var (regex, pattern) in compiled)
                {
                    if (!regex.IsMatch(remaining)) continue;
                    used.Add(pattern);
                    remaining = regex.Replace(remaining, " ");
                }
                if (LegacyName.IsMatch(remaining))
                    violations.Add($"{Path.GetRelativePath(Root(), path)}:{index + 1}: {lines[index].Trim()}");
            }
        }
        return (violations, used);
    }

    [Fact]
    public void Source_CarriesTheLegacyNameOnlyInAllowedTexts()
    {
        Scan().Violations.Should().BeEmpty("a line with the retired name needs an allowlist entry and a reason");
    }

    [Fact]
    public void Allowlist_HasNoEntryThatMatchesNoLine()
    {
        var unused = Allowed.Select(entry => entry.Pattern).Except(Scan().Used).ToList();

        unused.Should().BeEmpty("a stale entry hides a name that moved");
    }

    [Fact]
    public void Allowlist_GivesEveryEntryOneReason()
    {
        Allowed.Should().OnlyContain(entry => entry.Reason.Length > 0);
        Allowed.Select(entry => entry.Pattern).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Files_CarryingTheLegacyNameInTheirPathAreHarnessOrMigration()
    {
        var offenders = Directory.EnumerateFiles(Root(), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root(), path))
            .Where(path => !path.StartsWith("node_modules", StringComparison.Ordinal) && !path.Contains("/bin/") && !path.Contains("/obj/")
                && !path.StartsWith(".git/", StringComparison.Ordinal))
            .Where(path => LegacyName.IsMatch(path))
            .Where(path => !path.Contains("/Migrations/", StringComparison.Ordinal)
                && !path.StartsWith("evals/astra/", StringComparison.Ordinal)
                && !path.StartsWith(".claude/skills/astra-eval/", StringComparison.Ordinal)
                && !path.Contains("/Fixtures/", StringComparison.Ordinal))
            .ToList();

        offenders.Should().BeEmpty(HarnessName);
    }
}
