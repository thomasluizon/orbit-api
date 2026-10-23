import { readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));

const shards = [
  {
    project: "domain",
    shard: "domain-habit-create-log",
    // https://github.com/stryker-mutator/stryker-net/blob/f9109e24c615a7030a3b33e5532c665c974e4ec5/src/Stryker.Configuration/FilePattern.cs
    // Stryker matches fully contained character spans. Prefix exclusions keep boundary-crossing mutants in the next shard.
    mutate: ["**/Entities/Habit.cs{0..15181}"],
  },
  {
    project: "domain",
    shard: "domain-habit-schedule",
    mutate: ["**/Entities/Habit.cs{0..25215}", "!**/Entities/Habit.cs{0..15181}"],
  },
  {
    project: "domain",
    shard: "domain-habit-update-freeze",
    mutate: ["**/Entities/Habit.cs", "!**/Entities/Habit.cs{0..25215}", "**/Entities/StreakFreeze.cs"],
  },
  // https://github.com/thomasluizon/orbit-api/actions/runs/39941432734
  // The combined scope selected 512 mutants after 11 minutes of setup and exhausted the job limit.
  ...[6500, 13000, 19500, 26000, null].map((end, index, ends) => ({
    project: "domain",
    shard: `domain-user-${index + 1}`,
    mutate: [
      `**/Entities/User.cs${end === null ? "" : `{0..${end}}`}`,
      ...(index === 0 ? [] : [`!**/Entities/User.cs{0..${ends[index - 1]}}`]),
    ],
  })),
  { project: "domain", shard: "domain-user-invariants", mutate: ["**/Entities/HabitInvariants.cs"] },
  { project: "application" },
  { project: "infrastructure" },
];

function patternParts(pattern) {
  const [glob] = pattern.replace(/^!/, "").split("{");
  const ranges = [...pattern.matchAll(/\{(\d+)\.\.(\d+)\}/g)].map((match) => [Number(match[1]), Number(match[2])]);
  const regex = new RegExp(`^${glob.split("/").map((part) => part === "**" ? ".*" : part.split("*").map((text) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("[^/]*")).join("/")}$`);
  return { glob, ranges: ranges.length ? ranges : [[0, 2147483647]], matches: (path) => regex.test(path) };
}

const projectDirectories = { domain: "Orbit.Domain", application: "Orbit.Application", infrastructure: "Orbit.Infrastructure" };

function inProject(path, scope) {
  return path.startsWith(`src/${projectDirectories[scope.project]}/`);
}

function selectDiff(target) {
  const git = (...args) => execFileSync("git", args, { encoding: "utf8", maxBuffer: 32 * 1024 * 1024 });
  const changed = git("diff", "--name-only", "--no-renames", "--diff-filter=AM", "-z", target, "HEAD", "--").split("\0").filter(Boolean);
  const candidates = [];
  const scopes = shards.map((shard) => ({ ...shard, mutate: shard.mutate ?? JSON.parse(readFileSync(`${root}stryker-config.${shard.project}.json`, "utf8"))["stryker-config"].mutate }));
  for (const path of changed) {
    if (!path.endsWith(".cs") || !scopes.some((scope) => inProject(path, scope) && scope.mutate.some((pattern) => !pattern.startsWith("!") && patternParts(pattern).matches(path)))) continue;
    const patch = git("--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--unified=0", "--no-renames", target, "HEAD", "--", path);
    const hunks = [...patch.matchAll(/^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/gm)].map((match) => ({
      OldStart: Number(match[1]), OldCount: Number(match[2] ?? 1), NewStart: Number(match[3]), NewCount: Number(match[4] ?? 1),
    }));
    const existed = git("--literal-pathspecs", "ls-tree", "-z", target, "--", path).length > 0;
    candidates.push({ path, Before: existed ? git("show", `${target}:${path}`) : "", After: git("show", `HEAD:${path}`), Hunks: hunks });
  }
  if (!candidates.length) return [];
  const spans = JSON.parse(execFileSync("dotnet", [fileURLToPath(new URL("./mutation-spans/bin/Debug/net10.0/MutationSpans.dll", import.meta.url))], {
    input: JSON.stringify(candidates), encoding: "utf8", maxBuffer: 32 * 1024 * 1024,
  }));
  return scopes.flatMap((scope) => {
    const mutate = [];
    for (const [index, { path }] of candidates.entries()) {
      if (!inProject(path, scope)) continue;
      const exclusions = scope.mutate.filter((pattern) => pattern.startsWith("!") && patternParts(pattern).matches(path));
      for (const pattern of scope.mutate.filter((pattern) => !pattern.startsWith("!") && patternParts(pattern).matches(path))) {
        const { ranges } = patternParts(pattern);
        const glob = `**/${path.split("/").slice(2).join("/")}`;
        const selected = spans[index].flatMap(([start, end]) => ranges.map(([lower, upper]) => [Math.max(start, lower), Math.min(end, upper)]))
          .filter(([start, end]) => start < end && !exclusions.some((pattern) => patternParts(pattern).ranges.some(([lower, upper]) => start >= lower && end <= upper)));
        if (selected.length) mutate.push(`${glob}${selected.map(([start, end]) => `{${start}..${end}}`).join("")}`);
      }
      if (mutate.some((pattern) => patternParts(pattern).matches(path))) mutate.push(...exclusions);
    }
    return mutate.length ? [{ ...scope, mutate }] : [];
  });
}

let selected = shards;
if (process.argv.length > 2 && (process.argv.length !== 4 || process.argv[2] !== "--since")) {
  throw new Error("Usage: mutation-matrix.mjs [--since <commit>]");
}
if (process.argv[2] === "--since") {
  const target = execFileSync("git", ["rev-parse", "--verify", `${process.argv[3]}^{commit}`], { encoding: "utf8" }).trim();
  selected = selectDiff(target);
}

process.stdout.write(`matrix=${JSON.stringify({ include: selected })}\n`);
process.stdout.write(`has-shards=${selected.length > 0}\n`);
