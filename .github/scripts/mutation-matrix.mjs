import { execFileSync } from "node:child_process";

const shards = [
  {
    project: "domain",
    shard: "domain-habit-freeze",
    mutate: ["**/Entities/Habit.cs", "**/Entities/StreakFreeze.cs"],
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

const baseRef = process.argv[2];
let selected = shards;

if (baseRef) {
  const changed = execFileSync("git", ["diff", "--name-only", "--no-renames", "-z", `${baseRef}...HEAD`], { encoding: "utf8" })
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
  selected = shards.filter(({ project }) => shared || changed.some((path) =>
    path.startsWith(`src/${projects[project]}/`)
    || path.startsWith(`tests/${projects[project]}.Tests/`)
    || path === `stryker-config.${project}.json`
  ));
}

process.stdout.write(`matrix=${JSON.stringify({ include: selected })}\n`);
process.stdout.write(`has-shards=${selected.length > 0}\n`);
