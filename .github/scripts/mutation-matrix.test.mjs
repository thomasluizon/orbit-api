import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const selector = fileURLToPath(new URL("./mutation-matrix.mjs", import.meta.url));
const domainShards = ["domain-habit-create-log", "domain-habit-schedule", "domain-habit-update-freeze", "domain-user-invariants"];
const allShards = [...domainShards, "application", "infrastructure"];

function select(changed) {
  const output = execFileSync(process.execPath, [selector, ...(changed === undefined ? [] : ["--stdin"])], {
    input: changed,
    encoding: "utf8",
  });
  const values = Object.fromEntries(output.trim().split("\n").map((line) => {
    const separator = line.indexOf("=");
    return [line.slice(0, separator), line.slice(separator + 1)];
  }));
  const matrix = JSON.parse(values.matrix);
  assert.equal(values["has-shards"], String(matrix.include.length > 0));
  return matrix.include;
}

function selectPaths(paths) {
  return select(paths.map((path) => `${path}\0`).join(""));
}

function repository(t) {
  const root = mkdtempSync(join(tmpdir(), "orbit-mutation-matrix-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const git = (...args) => execFileSync("git", args, { cwd: root, encoding: "utf8" });
  git("init", "--quiet");
  git("-c", "user.name=Mutation test", "-c", "user.email=mutation@example.com", "commit", "--quiet", "--allow-empty", "-m", "Base");
  git("branch", "base");
  const paths = new Set();
  const write = (path) => {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), "changed\n");
    paths.add(path);
  };
  const commit = (...extraPaths) => {
    git("--literal-pathspecs", "add", "--", ...new Set([...paths, ...extraPaths]));
    git("-c", "user.name=Mutation test", "-c", "user.email=mutation@example.com", "commit", "--quiet", "-m", "Change");
    paths.clear();
  };
  const changed = () => git("diff", "--name-only", "--no-renames", "-z", "--end-of-options", "base...HEAD");
  return { root, git, write, commit, changed };
}

function names(matrix) {
  return matrix.map((entry) => entry.shard ?? entry.project);
}

function mergeWithMovedBase(t) {
  const repo = repository(t);
  repo.git("checkout", "--quiet", "-b", "pull-request");
  repo.write("src/Orbit.Application/Changed.cs");
  repo.commit();
  repo.git("checkout", "--quiet", "base");
  const base = repo.git("rev-parse", "HEAD").trim();
  repo.git("-c", "user.name=Mutation test", "-c", "user.email=mutation@example.com", "merge", "--quiet", "--no-ff", "pull-request", "-m", "Test merge");
  const merge = repo.git("rev-parse", "HEAD").trim();
  assert.equal(repo.git("rev-parse", "HEAD^1").trim(), base);
  repo.git("checkout", "--quiet", "--detach", merge);
  repo.git("branch", "--force", "base", base);
  repo.git("checkout", "--quiet", "base");
  repo.write("src/Orbit.Infrastructure/BaseOnly.cs");
  repo.commit();
  const movedBase = repo.git("rev-parse", "HEAD").trim();
  repo.git("checkout", "--quiet", "--detach", merge);
  repo.git("remote", "add", "origin", repo.root);
  repo.git("fetch", "--quiet", "origin", "base");
  assert.equal(repo.git("rev-parse", "origin/base").trim(), movedBase);
  assert.notEqual(base, movedBase);
  return { ...repo, base, movedBase };
}

function workflowStep(name) {
  const workflow = readFileSync(new URL("../workflows/mutation.yml", import.meta.url), "utf8");
  const step = workflow.split(`      - name: ${name}\n`)[1]?.split(/\n      - /)[0];
  const script = step?.match(/        run: \|\n((?:          [^\n]*\n|\n)*)/)?.[1];
  assert.ok(script, `Workflow step ${name} must have a shell script`);
  return script.replace(/^          /gm, "");
}

test("the PR Stryker step uses the merge first parent after the base branch moves", (t) => {
  const repo = mergeWithMovedBase(t);
  const capture = join(repo.root, "stryker-args");
  const script = workflowStep("Run Stryker (PR changed in-scope files only)")
    .replaceAll("${{ github.base_ref }}", "base")
    .replaceAll("${{ matrix.project }}", "application");
  execFileSync("bash", ["--noprofile", "--norc", "-eo", "pipefail"], {
    cwd: repo.root,
    env: { ...process.env, MUTATE_FILES: "", STRYKER_ARGS: capture },
    input: `nproc() { printf '2\\n'; }\ndotnet() { printf '%s\\0' "$@" > "$STRYKER_ARGS"; }\n${script}`,
    encoding: "utf8",
  });
  const args = readFileSync(capture, "utf8").split("\0").filter(Boolean);
  const since = args.find((arg) => arg.startsWith("--since:"));
  assert.ok(since, "Stryker must receive a since target");
  const target = since.slice("--since:".length);
  assert.equal(repo.git("rev-parse", target).trim(), repo.base);
  assert.equal(target, repo.base, "The target must be the immutable first-parent commit ID");
});

test("project selection excludes moved-base changes after a test merge", (t) => {
  const repo = mergeWithMovedBase(t);
  const output = join(repo.root, "selection-output");
  execFileSync("bash", ["--noprofile", "--norc", "-eo", "pipefail"], {
    cwd: repo.root,
    env: { ...process.env, BASE_REF: "origin/base", GITHUB_OUTPUT: output },
    input: workflowStep("Select changed projects").replaceAll("node .github/scripts/mutation-matrix.mjs", `node "${selector}"`),
    encoding: "utf8",
  });
  const matrixLine = readFileSync(output, "utf8").split("\n").find((line) => line.startsWith("matrix="));
  assert.ok(matrixLine);
  assert.deepEqual(names(JSON.parse(matrixLine.slice("matrix=".length)).include), ["application"]);
});

test("an empty changed-file list selects no shards", () => {
  assert.deepEqual(selectPaths([]), []);
});

test("documentation-only changes select no shards", () => {
  assert.deepEqual(selectPaths([
    "README.md",
    "docs/mutation notes.md",
    "src/Orbit.Domain/CLAUDE.md",
    "tests/Orbit.Application.Tests/README.md",
    "src/Orbit.Infrastructure/docs/design.MD",
  ]), []);
});

test("unrelated projects select no shards", () => {
  assert.deepEqual(selectPaths(["src/Orbit.Api/Controllers/AuthController.cs", "tests/Orbit.Analyzers.Tests/RuleTests.cs"]), []);
});

for (const [project, expected] of [
  ["Domain", domainShards],
  ["Application", ["application"]],
  ["Infrastructure", ["infrastructure"]],
]) {
  for (const path of [`src/Orbit.${project}/Entities/Changed.cs`, `tests/Orbit.${project}.Tests/ChangedTests.cs`]) {
    test(`${path} selects only its project's shards`, () => {
      assert.deepEqual(names(selectPaths([path])), expected);
    });
  }
}

test("changes across projects select their union without duplicate shards", () => {
  assert.deepEqual(names(selectPaths([
    "src/Orbit.Domain/Entities/Habit.cs",
    "tests/Orbit.Domain.Tests/HabitTests.cs",
    "src/Orbit.Infrastructure/Services/UserStreakService.cs",
  ])), [...domainShards, "infrastructure"]);
});

test("NUL separators preserve spaces and newlines within paths", () => {
  assert.deepEqual(names(selectPaths(["src/Orbit.Domain/Entities/Habit notes\nREADME.md"])), []);
  assert.deepEqual(names(selectPaths(["src/Orbit.Domain/Entities/Habit notes\nChanged.cs"])), domainShards);
});

test("renames select both the old and new project, and deletions select the old project", (t) => {
  const repo = repository(t);
  const oldPath = "src/Orbit.Domain/Old.cs";
  const newPath = "src/Orbit.Application/Moved.cs";
  const deletedPath = "tests/Orbit.Infrastructure.Tests/RemovedTests.cs";
  repo.write(oldPath);
  repo.write(deletedPath);
  repo.commit();
  repo.git("branch", "--force", "base");
  mkdirSync(dirname(join(repo.root, newPath)), { recursive: true });
  renameSync(join(repo.root, oldPath), join(repo.root, newPath));
  rmSync(join(repo.root, deletedPath));
  repo.commit(oldPath, newPath, deletedPath);
  assert.deepEqual(names(select(repo.changed())), allShards);
});

test("the diff uses the merge base rather than unrelated changes on the base branch", (t) => {
  const repo = repository(t);
  const head = repo.git("branch", "--show-current").trim();
  repo.git("checkout", "--quiet", "base");
  repo.write("src/Orbit.Infrastructure/BaseOnly.cs");
  repo.commit();
  repo.git("checkout", "--quiet", head);
  repo.write("README.md");
  repo.commit();
  assert.deepEqual(select(repo.changed()), []);
});

for (const path of ["Directory.Build.props", ".config/dotnet-tools.json", ".github/workflows/mutation.yml", ".github/scripts/mutation-matrix.mjs"]) {
  test(`${path} selects every shard`, () => {
    assert.deepEqual(names(selectPaths([path])), allShards);
  });
}

test("a project-specific Stryker config selects only that project", () => {
  assert.deepEqual(names(selectPaths(["stryker-config.application.json"])), ["application"]);
});

test("nightly and manual runs keep all shards and their mutation scopes without input", () => {
  const matrix = select();
  assert.deepEqual(names(matrix), allShards);
  assert.deepEqual(matrix.filter(({ project }) => project === "domain").map(({ mutate }) => mutate), [
    ["**/Entities/Habit.cs{0..15181}"],
    ["**/Entities/Habit.cs{0..25215}", "!**/Entities/Habit.cs{0..15181}"],
    ["**/Entities/Habit.cs", "!**/Entities/Habit.cs{0..25215}", "**/Entities/StreakFreeze.cs"],
    ["**/Entities/User.cs", "**/Entities/HabitInvariants.cs"],
  ]);
});

for (const file of ["Habit.cs", "StreakFreeze.cs", "User.cs", "HabitInvariants.cs"]) {
  test(`a change to ${file} selects every domain shard`, () => {
    assert.deepEqual(names(selectPaths([`src/Orbit.Domain/Entities/${file}`])), domainShards);
  });
}

test("domain Stryker configuration changes select every domain shard", () => {
  assert.deepEqual(names(selectPaths(["stryker-config.domain.json"])), domainShards);
});

test("Habit mutation spans have one owner at boundaries and after file growth", () => {
  const habit = "**/Entities/Habit.cs";
  const scopes = select().filter(({ mutate }) => mutate?.some((pattern) => pattern.startsWith(habit)));
  const boundaries = scopes.flatMap(({ mutate }) => mutate.flatMap((pattern) => {
    const range = pattern.match(/\{(\d+)\.\.(\d+)\}/);
    return range ? [Number(range[1]), Number(range[2])] : [];
  }));
  const positions = [...new Set([0, ...boundaries.flatMap((value) => [value, value + 1]), 1_000_000])];
  const matches = (pattern, start, end) => {
    if (!pattern.replace(/^!/, "").startsWith(habit)) return false;
    const range = pattern.match(/\{(\d+)\.\.(\d+)\}/);
    return !range || (start >= Number(range[1]) && end <= Number(range[2]));
  };
  for (const start of positions) {
    for (const end of positions.filter((value) => value >= start)) {
      const owners = scopes.filter(({ mutate }) =>
        mutate.some((pattern) => !pattern.startsWith("!") && matches(pattern, start, end))
        && !mutate.some((pattern) => pattern.startsWith("!") && matches(pattern, start, end)));
      assert.equal(owners.length, 1, `Mutant span {${start}..${end}} must belong to one shard`);
    }
  }
});
