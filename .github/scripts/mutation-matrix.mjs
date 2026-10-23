import { readFileSync, realpathSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { isAbsolute, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));
const gitPath = "/usr/bin/git";
const dotnetRoot = process.env.DOTNET_ROOT ?? "/usr/local/share/dotnet";
if (!isAbsolute(dotnetRoot)) throw new Error("DOTNET_ROOT must be an absolute SDK installation path");
const dotnetPath = realpathSync(join(dotnetRoot, "dotnet"));
const git = (...args) => execFileSync(gitPath, args, { encoding: "utf8", maxBuffer: 32 * 1024 * 1024 });

function spanShards(project, name, file, boundaries, start = 0) {
  return [...boundaries, null].map((end, index) => ({
    project,
    shard: `${name}-${index + 1}`,
    // https://github.com/stryker-mutator/stryker-net/blob/f9109e24c615a7030a3b33e5532c665c974e4ec5/src/Stryker.Configuration/FilePattern.cs
    // Prefix exclusions assign boundary-crossing mutants to the shard containing their end.
    mutate: [
      `${file}${end === null ? "" : `{0..${end}}`}`,
      ...(index === 0 && start === 0 ? [] : [`!${file}{0..${index === 0 ? start : boundaries[index - 1]}}`]),
    ],
  }));
}

const shards = [
  {
    project: "domain",
    shard: "domain-habit-create-log",
    mutate: ["**/Entities/Habit.cs{0..15181}"],
  },
  {
    project: "domain",
    shard: "domain-habit-schedule",
    mutate: ["**/Entities/Habit.cs{0..25215}", "!**/Entities/Habit.cs{0..15181}"],
  },
  // https://github.com/thomasluizon/orbit-api/actions/runs/39941432734
  // The report partitions 317 selected mutants into groups of 108, 109 and 100, including StreakFreeze.
  ...spanShards("domain", "domain-habit-update-freeze", "**/Entities/Habit.cs", [31500, 37500], 25215)
    .map((scope, index) => index === 2 ? { ...scope, mutate: [...scope.mutate, "**/Entities/StreakFreeze.cs"] } : scope),
  // https://github.com/thomasluizon/orbit-api/actions/runs/39941432734
  // The combined scope selected 512 mutants after 11 minutes of setup and exhausted the job limit.
  ...spanShards("domain", "domain-user", "**/Entities/User.cs", [6500, 13000, 19500, 26000]),
  // https://github.com/thomasluizon/orbit-api/actions/runs/39958472456
  // The report partitions 113 selected invariant mutants into groups of 38, 37 and 38.
  ...spanShards("domain", "domain-user-invariants", "**/Entities/HabitInvariants.cs", [2000, 4000]),
  { project: "application", shard: "application-auth", mutate: ["**/Auth/Commands/*.cs"] },
  {
    project: "application", shard: "application-subscriptions", mutate: [
      "**/Subscriptions/Commands/VerifyPlayPurchaseCommand.cs",
      "**/Subscriptions/Commands/HandlePlayNotificationCommand.cs",
      "**/Subscriptions/Services/PlayReferralCouponConsumer.cs",
      "**/Common/PlaySubscriptionStateExtensions.cs",
      "**/Common/SubscriptionSourceExtensions.cs",
    ],
  },
  { project: "application", shard: "application-schedule", mutate: ["**/Habits/Services/HabitScheduleService.cs"] },
  ...spanShards("application", "application-chat-processing", "**/Chat/Commands/ProcessUserChatCommand.Tools.cs", [18000])
    .map((scope, index) => index === 1 ? { ...scope, mutate: [...scope.mutate, "**/Chat/Commands/ProcessUserChatCommand.Correction.cs"] } : scope),
  {
    project: "application", shard: "application-chat-context", mutate: [
      "**/Chat/Commands/ProcessUserChatCommand.Accumulator.cs",
      "**/Chat/Tools/ChatToolArgumentCheck.cs",
      "**/Chat/Tools/PreparedToolIntent.cs",
      "**/Chat/Tools/Implementations/ManageApiKeysTool.cs",
      "**/Chat/Tools/Implementations/ManageSubscriptionTool.cs",
    ],
  },
  ...spanShards("application", "application-chat-habits", "**/Chat/Tools/Implementations/CreateHabitTool.cs", [15000])
    .map((scope, index) => ({ ...scope, mutate: [...scope.mutate, index === 0
      ? "**/Chat/Tools/Implementations/ManageAccountTool.cs" : "**/Chat/Tools/Implementations/ProfileTools.cs"] })),
  {
    project: "application", shard: "application-chat-calendar-notifications", mutate: [
      "**/Chat/Tools/Implementations/CalendarTools.cs",
      "**/Chat/Tools/Implementations/NotificationTools.cs",
    ],
  },
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

function isInScopeFile(path, scopes) {
  return /^src\/Orbit\.(?:Domain|Application|Infrastructure)\/(?:[A-Za-z0-9_][A-Za-z0-9_.-]*\/)*[A-Za-z0-9_][A-Za-z0-9_.-]*\.cs$/.test(path)
    && scopes.some((scope) => inProject(path, scope)
      && scope.mutate.some((pattern) => !pattern.startsWith("!") && patternParts(pattern).matches(path)));
}

function selectDiff(target) {
  const changed = git("diff", "--name-only", "--no-renames", "--diff-filter=AM", "-z", target, "HEAD", "--").split("\0").filter(Boolean);
  const candidates = [];
  const scopes = shards.map((shard) => ({ ...shard, mutate: shard.mutate ?? JSON.parse(readFileSync(`${root}stryker-config.${shard.project}.json`, "utf8"))["stryker-config"].mutate }));
  for (const path of changed) {
    if (!isInScopeFile(path, scopes)) continue;
    const patch = git("--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--unified=0", "--no-renames", target, "HEAD", "--", path);
    const hunks = [...patch.matchAll(/^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/gm)].map((match) => ({
      OldStart: Number(match[1]), OldCount: Number(match[2] ?? 1), NewStart: Number(match[3]), NewCount: Number(match[4] ?? 1),
    }));
    const existed = git("--literal-pathspecs", "ls-tree", "-z", target, "--", path).length > 0;
    candidates.push({ path, Before: existed ? git("show", `${target}:${path}`) : "", After: git("show", `HEAD:${path}`), Hunks: hunks });
  }
  if (!candidates.length) return [];
  const spans = JSON.parse(execFileSync(dotnetPath, [fileURLToPath(new URL("./mutation-spans/bin/Debug/net10.0/MutationSpans.dll", import.meta.url))], {
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
  const target = git("rev-parse", "--verify", "--end-of-options", `${process.argv[3]}^{commit}`).trim();
  selected = selectDiff(target);
}

process.stdout.write(`matrix=${JSON.stringify({ include: selected })}\n`);
process.stdout.write(`has-shards=${selected.length > 0}\n`);
