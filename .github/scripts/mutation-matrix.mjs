import { readFileSync } from "node:fs";

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
  {
    project: "domain",
    shard: "domain-user-invariants",
    mutate: ["**/Entities/User.cs", "**/Entities/HabitInvariants.cs"],
  },
  { project: "application" },
  { project: "infrastructure" },
];

const projects = {
  domain: "Orbit.Domain",
  application: "Orbit.Application",
  infrastructure: "Orbit.Infrastructure",
};

let selected = shards;

if (process.argv[2] === "--stdin") {
  const changed = readFileSync(0, "utf8")
    .split("\0")
    .filter(Boolean);
  for (const path of changed) process.stderr.write(`Changed: ${JSON.stringify(path)}\n`);
  const shared = changed.some((path) =>
    /^Directory\..*\.(props|targets)$/.test(path)
    || /^[^/]+\.slnx?$/.test(path)
    || /^(global\.json|[Nn]u[Gg]et\.[Cc]onfig|\.config\/dotnet-tools\.json)$/.test(path)
    || path === ".github/workflows/mutation.yml"
    || path.startsWith(".github/scripts/mutation-matrix.")
  );
  selected = shards.filter(({ project }) => shared || changed.some((path) => !/\.(md|mdx|rst|adoc)$/i.test(path) && (
    path.startsWith(`src/${projects[project]}/`)
    || path.startsWith(`tests/${projects[project]}.Tests/`)
    || path === `stryker-config.${project}.json`
  )));
}

process.stdout.write(`matrix=${JSON.stringify({ include: selected })}\n`);
process.stdout.write(`has-shards=${selected.length > 0}\n`);
