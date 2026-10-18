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
  if (!/^[A-Za-z0-9_][A-Za-z0-9._/-]*$/.test(baseRef)
    || /\s|\.\./.test(baseRef)
    || baseRef.split("/").some((part) => !/^[A-Za-z0-9_]/.test(part) || part.endsWith(".") || part.endsWith(".lock"))) {
    process.stderr.write(`Invalid base ref: ${JSON.stringify(baseRef)}. Expected a git ref or object name.\n`);
    process.exit(1);
  }
  const changed = execFileSync("git", ["diff", "--name-only", "--no-renames", "-z", "--end-of-options", `${baseRef}...HEAD`], {
    encoding: "utf8",
    env: { ...process.env, PATH: "/usr/bin:/bin" },
  })
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
