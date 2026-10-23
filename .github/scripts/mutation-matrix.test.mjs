import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const selector = fileURLToPath(new URL("./mutation-matrix.mjs", import.meta.url));
execFileSync("dotnet", ["build", fileURLToPath(new URL("./mutation-spans/MutationSpans.csproj", import.meta.url)), "--nologo"], { stdio: "pipe" });

function select(repo, target) {
  const output = execFileSync(process.execPath, [selector, ...(target ? ["--since", target] : [])], {
    cwd: repo?.root, encoding: "utf8",
  });
  const values = Object.fromEntries(output.trim().split("\n").map((line) => {
    const separator = line.indexOf("=");
    return [line.slice(0, separator), line.slice(separator + 1)];
  }));
  const matrix = JSON.parse(values.matrix);
  assert.equal(values["has-shards"], String(matrix.include.length > 0));
  return matrix.include;
}

function repository(t) {
  const root = mkdtempSync(join(tmpdir(), "orbit-mutation-matrix-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const git = (...args) => execFileSync("git", args, { cwd: root, encoding: "utf8" });
  git("init", "--quiet");
  git("config", "core.autocrlf", "false");
  git("config", "user.name", "Mutation test");
  git("config", "user.email", "mutation@example.com");
  git("commit", "--quiet", "--allow-empty", "-m", "Base");
  git("branch", "base");
  const paths = new Set();
  const write = (path, contents = "changed\n") => {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), contents);
    paths.add(path);
  };
  const commit = () => {
    git("--literal-pathspecs", "add", "--", ...paths);
    git("commit", "--quiet", "-m", "Change");
    paths.clear();
  };
  return { root, git, write, commit };
}

const userPath = "src/Orbit.Domain/Entities/User.cs";
const before = "class User {\n    int Value => 1;\n}\n";
const after = before.replace("=> 1", "=> 2");

function change(t, path = userPath, oldSource = before, newSource = after) {
  const repo = repository(t);
  repo.write(path, oldSource);
  repo.commit();
  repo.git("branch", "--force", "base");
  repo.write(path, newSource);
  repo.write("README.md");
  repo.commit();
  return repo;
}

function workflowStep(name) {
  const workflow = readFileSync(new URL("../workflows/mutation.yml", import.meta.url), "utf8");
  const step = workflow.split(`      - name: ${name}\n`)[1]?.split(/\n      - /)[0];
  const script = step?.match(/        run: \|\n((?:          [^\n]*\n|\n)*)/)?.[1];
  assert.ok(script, `Workflow step ${name} must have a shell script`);
  return script.replace(/^          /gm, "");
}

function mergeWithMovedBase(t) {
  const repo = change(t);
  repo.git("branch", "pull-request");
  repo.git("checkout", "--quiet", "base");
  const base = repo.git("rev-parse", "HEAD").trim();
  repo.git("merge", "--quiet", "--no-ff", "pull-request", "-m", "Test merge");
  const merge = repo.git("rev-parse", "HEAD").trim();
  assert.equal(repo.git("rev-parse", "HEAD^1").trim(), base);
  repo.git("checkout", "--quiet", "--detach", merge);
  repo.git("branch", "--force", "base", base);
  repo.git("checkout", "--quiet", "base");
  repo.write("src/Orbit.Infrastructure/Services/JwtTokenService.cs", before);
  repo.commit();
  repo.git("checkout", "--quiet", "--detach", merge);
  return { ...repo, base };
}

test("the PR Stryker step uses the merge first parent after the base branch moves", (t) => {
  const repo = mergeWithMovedBase(t);
  const capture = join(repo.root, "stryker-args");
  const script = workflowStep("Run Stryker (PR changed in-scope files only)")
    .replaceAll("${{ matrix.project }}", "domain");
  const [scope] = select(repo, "HEAD^1");
  execFileSync("bash", ["--noprofile", "--norc", "-eo", "pipefail"], {
    cwd: repo.root,
    env: { ...process.env, MUTATE_FILES: scope.mutate.join(" "), STRYKER_ARGS: capture },
    input: `nproc() { printf '2\\n'; }\ndotnet() { printf '%s\\0' "$@" > "$STRYKER_ARGS"; }\n${script}`,
  });
  const args = readFileSync(capture, "utf8").split("\0").filter(Boolean);
  assert.ok(args.includes(`--since:${repo.base}`));
  assert.deepEqual(args.slice(-2), ["--mutate", "**/Entities/User.cs{13..33}"]);
});

test("workflow span selection excludes moved-base changes and uses the Stryker target", (t) => {
  const repo = mergeWithMovedBase(t);
  const output = join(repo.root, "selection-output");
  execFileSync("bash", ["--noprofile", "--norc", "-eo", "pipefail"], {
    cwd: repo.root,
    env: { ...process.env, EVENT_NAME: "pull_request", GITHUB_OUTPUT: output },
    input: workflowStep("Select changed projects").replaceAll("node .github/scripts/mutation-matrix.mjs", `node "${selector}"`),
  });
  const matrixLine = readFileSync(output, "utf8").split("\n").find((line) => line.startsWith("matrix="));
  assert.deepEqual(JSON.parse(matrixLine.slice(7)).include, select(repo, "HEAD^1"));
  assert.deepEqual(select(repo, "HEAD^1").map(({ shard }) => shard), ["domain-user-1"]);
});

for (const [name, oldSource, newSource] of [
  ["unchanged file", before, before],
  ["comment-only change", `${before}// original\n`, `${before}// revised\n`],
  ["inline comment", before.replace("1;", "1; // original"), before.replace("1;", "1; // revised")],
  ["XML doc comment", `/// <summary>Original</summary>\n${before}`, `/// <summary>Revised</summary>\n${before}`],
  ["block comment", `/* original\n words */\n${before}`, `/* revised\n more words */\n${before}`],
  ["blank line insertion", before, `\n${before}`],
  ["whitespace-only change", before, before.replace("    int", "        int")],
  ["deletion only", before, "class User {\n}\n"],
]) {
  test(`diff spans: ${name} selects no mutable span`, (t) => {
    assert.deepEqual(select(change(t, userPath, oldSource, newSource), "base"), []);
  });
}

test("diff spans: a changed User span selects only its owning shard", (t) => {
  assert.deepEqual(select(change(t), "base"), [{ project: "domain", shard: "domain-user-1", mutate: ["**/Entities/User.cs{13..33}"] }]);
});

test("diff spans: a later User span retains the owning shard's prefix exclusion", (t) => {
  const padding = `/*${" ".repeat(7000)}*/\n`;
  const repo = change(t, userPath, padding + before, padding + after);
  assert.deepEqual(select(repo, "base"), [{ project: "domain", shard: "domain-user-2", mutate: ["**/Entities/User.cs{7018..7038}", "!**/Entities/User.cs{0..6500}"] }]);
});

test("diff spans: separate comment and code hunks select only the code hunk", (t) => {
  const prefix = "// original\n\n\n\n";
  const repo = change(t, userPath, prefix + before, prefix.replace("original", "revised") + after);
  assert.deepEqual(select(repo, "base")[0].mutate, ["**/Entities/User.cs{27..47}"]);
});

for (const literal of ['"https://host/old"', '@"/* old */"', '"""/* old */"""', '$"old {1 + 2}"']) {
  test(`diff spans: string contents remain mutable: ${literal}`, (t) => {
    const source = `class User { string Value => ${literal}; }\n`;
    assert.equal(select(change(t, userPath, source, source.replace("old", "new")), "base").length, 1);
  });
}

test("diff spans: offsets use UTF-16 and preserve CRLF", (t) => {
  const prefix = "// emoji 😀\r\n";
  const oldSource = prefix + before.replaceAll("\n", "\r\n");
  const newSource = prefix + after.replaceAll("\n", "\r\n");
  const start = newSource.indexOf("    int");
  const end = newSource.indexOf("}\r\n");
  assert.deepEqual(select(change(t, userPath, oldSource, newSource), "base")[0].mutate, [`**/Entities/User.cs{${start}..${end}}`]);
});

test("diff spans: added in-scope files select their new code", (t) => {
  const repo = repository(t);
  repo.write(userPath, after);
  repo.commit();
  assert.deepEqual(select(repo, "base")[0].mutate, [`**/Entities/User.cs{0..${after.length}}`]);
});

test("diff spans: deleted files select nothing", (t) => {
  const repo = change(t);
  rmSync(join(repo.root, userPath));
  repo.git("--literal-pathspecs", "add", "--", userPath);
  repo.git("commit", "--quiet", "-m", "Delete");
  assert.deepEqual(select(repo, "base"), []);
});

for (const path of ["README.md", ".github/workflows/mutation.yml", "stryker-config.domain.json", "tests/Orbit.Domain.Tests/UserTests.cs", "src/Orbit.Api/Controllers/AuthController.cs", "src/Orbit.Domain/Entities/Other.cs"]) {
  test(`diff spans: ${path} has no changed in-scope source`, (t) => {
    const repo = repository(t);
    repo.write(path, after);
    repo.commit();
    assert.deepEqual(select(repo, "base"), []);
  });
}

for (const [path, pattern, project] of [
  ["src/Orbit.Application/Auth/Commands/SignInCommand.cs", "**/Auth/Commands/SignInCommand.cs", "application"],
  ["src/Orbit.Infrastructure/Services/JwtTokenService.cs", "**/Services/JwtTokenService.cs", "infrastructure"],
  ["src/Orbit.Domain/Entities/HabitInvariants.cs", "**/Entities/HabitInvariants.cs", "domain"],
  ["src/Orbit.Domain/Entities/StreakFreeze.cs", "**/Entities/StreakFreeze.cs", "domain"],
]) {
  test(`diff spans: ${path} keeps its configured scope`, (t) => {
    const matrix = select(change(t, path), "base");
    assert.equal(matrix.length, 1);
    assert.equal(matrix[0].project, project);
    assert.deepEqual(matrix[0].mutate, [`${pattern}{13..33}`]);
  });
}

test("diff spans: wildcard scopes keep each file's ranges separate", (t) => {
  const repo = change(t, "src/Orbit.Application/Auth/Commands/First.cs");
  repo.write("src/Orbit.Application/Auth/Commands/Second.cs", after);
  repo.commit();
  assert.deepEqual(select(repo, "base")[0].mutate, ["**/Auth/Commands/First.cs{13..33}", "**/Auth/Commands/Second.cs{0..35}"]);
});

const nightly = () => select().filter(({ project }) => project === "domain");

function owns(scope, file, start, end) {
  const matches = (pattern) => {
    if (pattern.replace(/^!/, "").split("{")[0] !== `**/Entities/${file}`) return false;
    const ranges = [...pattern.matchAll(/\{(\d+)\.\.(\d+)\}/g)];
    return !ranges.length || ranges.some((match) => start >= Number(match[1]) && end <= Number(match[2]));
  };
  return scope.mutate.some((pattern) => !pattern.startsWith("!") && matches(pattern))
    && !scope.mutate.some((pattern) => pattern.startsWith("!") && matches(pattern));
}

function assertOneOwner(scopes, file, start, end) {
  assert.equal(scopes.filter((scope) => owns(scope, file, start, end)).length, 1, `${file}{${start}..${end}} needs exactly one owner`);
}

test("nightly and manual runs keep the configured file set and thresholds", () => {
  const scopes = nightly();
  assert.equal(scopes.length, 9);
  const config = JSON.parse(readFileSync(new URL("../../stryker-config.domain.json", import.meta.url), "utf8"))["stryker-config"];
  assert.deepEqual([...new Set(scopes.flatMap(({ mutate }) => mutate.filter((pattern) => !pattern.startsWith("!")).map((pattern) => pattern.split("{")[0])))].sort(), [...config.mutate].sort());
  assert.deepEqual(config.thresholds, { high: 80, low: 60, break: 0 });
  assert.deepEqual(select().slice(-2), [{ project: "application" }, { project: "infrastructure" }]);
});

for (const file of ["Habit.cs", "StreakFreeze.cs", "User.cs", "HabitInvariants.cs"]) {
  test(`${file}: every character has exactly one nightly owner`, () => {
    const scopes = nightly();
    const source = readFileSync(new URL(`../../src/Orbit.Domain/Entities/${file}`, import.meta.url), "utf8");
    for (let start = 0; start < source.length; start++) assertOneOwner(scopes, file, start, start + 1);
    const boundaries = scopes.flatMap(({ mutate }) => mutate.flatMap((pattern) => [...pattern.matchAll(/\{\d+\.\.(\d+)\}/g)].map((match) => Number(match[1]))));
    const positions = [...new Set([0, source.length, 1000000, ...boundaries.flatMap((value) => [value - 1, value, value + 1])])];
    for (const start of positions) {
      for (const end of positions.filter((value) => value >= start)) assertOneOwner(scopes, file, start, end);
    }
  });
}

test("ownership guard rejects an unowned or double-owned character", () => {
  const scopes = nightly();
  const firstUser = scopes.find((scope) => owns(scope, "User.cs", 0, 1));
  assert.throws(() => assertOneOwner(scopes.filter((scope) => scope !== firstUser), "User.cs", 0, 1), /exactly one owner/);
  assert.throws(() => assertOneOwner([...scopes, firstUser], "User.cs", 0, 1), /exactly one owner/);
});

for (const path of ["tests/Orbit.Domain.Tests/Entities/User.cs", "src/Orbit.Application/Entities/User.cs"]) {
  test(`diff spans: a matching filename outside the owning project is excluded: ${path}`, (t) => {
    assert.deepEqual(select(change(t, path), "base"), []);
  });
}
