import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, mkdirSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const selector = fileURLToPath(new URL("./mutation-matrix.mjs", import.meta.url));
const allShards = ["domain-habit-freeze", "domain-user-invariants", "application", "infrastructure"];

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
  const select = (base = "base") => {
    const output = execFileSync(process.execPath, [selector, base], { cwd: root, encoding: "utf8" });
    const values = Object.fromEntries(output.trim().split("\n").map((line) => {
      const separator = line.indexOf("=");
      return [line.slice(0, separator), line.slice(separator + 1)];
    }));
    const matrix = JSON.parse(values.matrix);
    assert.equal(values["has-shards"], String(matrix.include.length > 0));
    return matrix.include;
  };
  return { root, git, write, commit, select };
}

function names(matrix) {
  return matrix.map((entry) => entry.shard ?? entry.project);
}

test("documentation and unrelated projects select no shards", (t) => {
  const repo = repository(t);
  for (const path of ["README.md", "docs/mutation notes.md", "src/Orbit.Domain/CLAUDE.md", "tests/Orbit.Application.Tests/README.md", "src/Orbit.Infrastructure/docs/design.MD", "src/Orbit.Api/Controllers/AuthController.cs", "tests/Orbit.Analyzers.Tests/RuleTests.cs"]) repo.write(path);
  repo.commit();
  assert.deepEqual(repo.select(), []);
});

for (const [project, expected] of [
  ["Domain", allShards.slice(0, 2)],
  ["Application", ["application"]],
  ["Infrastructure", ["infrastructure"]],
]) {
  for (const path of [`src/Orbit.${project}/Entities/Changed.cs`, `tests/Orbit.${project}.Tests/ChangedTests.cs`]) {
    test(`${path} selects only its project's shards`, (t) => {
      const repo = repository(t);
      repo.write(path);
      repo.commit();
      assert.deepEqual(names(repo.select()), expected);
    });
  }
}

test("changes across projects select their union without duplicate shards", (t) => {
  const repo = repository(t);
  for (const path of ["src/Orbit.Domain/Entities/Habit.cs", "tests/Orbit.Domain.Tests/HabitTests.cs", "src/Orbit.Infrastructure/Services/UserStreakService.cs"]) repo.write(path);
  repo.commit();
  assert.deepEqual(names(repo.select()), [allShards[0], allShards[1], "infrastructure"]);
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
  assert.deepEqual(names(repo.select()), allShards);
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
  assert.deepEqual(repo.select(), []);
});

for (const path of ["Directory.Build.props", ".config/dotnet-tools.json", ".github/workflows/mutation.yml", ".github/scripts/mutation-matrix.mjs"]) {
  test(`${path} selects every shard`, (t) => {
    const repo = repository(t);
    repo.write(path);
    repo.commit();
    assert.deepEqual(names(repo.select()), allShards);
  });
}

test("a project-specific Stryker config selects only that project", (t) => {
  const repo = repository(t);
  repo.write("stryker-config.application.json");
  repo.commit();
  assert.deepEqual(names(repo.select()), ["application"]);
});

test("nightly and manual runs keep all shards and their mutation scopes without a diff", (t) => {
  const repo = repository(t);
  const matrix = repo.select("");
  assert.deepEqual(names(matrix), allShards);
  assert.deepEqual(matrix[0].mutate, ["**/Entities/Habit.cs", "**/Entities/StreakFreeze.cs"]);
  assert.deepEqual(matrix[1].mutate, ["**/Entities/User.cs", "**/Entities/HabitInvariants.cs"]);
});

test("an unavailable base fails instead of silently skipping mutation", (t) => {
  const repo = repository(t);
  const result = spawnSync(process.execPath, [selector, "missing-base"], { cwd: repo.root, encoding: "utf8" });
  assert.notEqual(result.status, 0);
  assert.equal(result.stdout, "");
});

for (const base of ["--output=changed", "origin/main branch", "origin/main\n", "origin/main..HEAD", "origin/main~1", "origin//main", "origin/.hidden", "origin/main.lock"]) {
  test(`invalid base ${JSON.stringify(base)} is refused before invoking git`, (t) => {
    const repo = repository(t);
    const marker = join(repo.root, "child-invoked");
    const probe = join(repo.root, "probe.mjs");
    writeFileSync(probe, `
      import childProcess from "node:child_process";
      import { writeFileSync } from "node:fs";
      import { syncBuiltinESMExports } from "node:module";
      const original = childProcess.execFileSync;
      childProcess.execFileSync = (...args) => {
        writeFileSync(${JSON.stringify(marker)}, "invoked");
        return original(...args);
      };
      syncBuiltinESMExports();
    `);
    const result = spawnSync(process.execPath, ["--import", probe, selector, base], { cwd: repo.root, encoding: "utf8" });
    assert.equal(result.status, 1);
    assert.equal(result.stdout, "");
    assert.equal(result.stderr, `Invalid base ref: ${JSON.stringify(base)}. Expected a git ref or object name.\n`);
    assert.equal(existsSync(marker), false);
  });
}

for (const kind of ["remote ref", "40-character SHA"]) {
  test(`${kind} is accepted with an untrusted inherited PATH`, (t) => {
    const repo = repository(t);
    repo.git("branch", "origin/main", "base");
    const base = kind === "remote ref" ? "origin/main" : repo.git("rev-parse", "base").trim();
    if (kind === "40-character SHA") assert.match(base, /^[a-f0-9]{40}$/);
    repo.write("src/Orbit.Application/Changed.cs");
    repo.commit();
    const bin = join(repo.root, "bin");
    const marker = join(repo.root, "untrusted-git-invoked");
    mkdirSync(bin);
    writeFileSync(join(bin, "git"), `#!/bin/sh\ntouch '${marker}'\nexit 99\n`, { mode: 0o755 });
    const result = spawnSync(process.execPath, [selector, base], {
      cwd: repo.root,
      encoding: "utf8",
      env: { ...process.env, PATH: `${bin}:/usr/bin:/bin` },
    });
    assert.equal(result.status, 0);
    assert.equal(result.stdout, 'matrix={"include":[{"project":"application"}]}\nhas-shards=true\n');
    assert.equal(result.stderr, 'Changed: "src/Orbit.Application/Changed.cs"\n');
    assert.equal(existsSync(marker), false);
  });
}
