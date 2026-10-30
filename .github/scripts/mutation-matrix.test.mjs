import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const selector = fileURLToPath(new URL("./mutation-matrix.mjs", import.meta.url));
const dotnetPath = realpathSync(join(process.env.DOTNET_ROOT ?? "/usr/local/share/dotnet", "dotnet"));
execFileSync(dotnetPath, ["build", fileURLToPath(new URL("./mutation-spans/MutationSpans.csproj", import.meta.url)), "--nologo"], { stdio: "pipe" });

function select(repo, target, env = process.env) {
  const revision = target && target !== "HEAD^1" ? repo.git("rev-parse", "--verify", "--end-of-options", `${target}^{commit}`).trim() : target;
  const output = execFileSync(process.execPath, [selector, ...(target ? ["--since", revision] : [])], {
    cwd: repo?.root, encoding: "utf8", env,
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
  const git = (...args) => execFileSync("/usr/bin/git", args, { cwd: root, encoding: "utf8" });
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

function assertRejectedBeforeProcess(t, args, message, env = process.env, firstOutput) {
  const directory = mkdtempSync(join(tmpdir(), "orbit-mutation-process-probe-"));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const marker = join(directory, "process-started");
  const preload = join(directory, "probe.mjs");
  writeFileSync(preload, [
    'import childProcess from "node:child_process";',
    'import { writeFileSync } from "node:fs";',
    'import { syncBuiltinESMExports } from "node:module";',
    "let calls = 0;",
    "childProcess.execFileSync = () => {",
    ...(firstOutput === undefined ? [] : [`  if (++calls === 1) return ${JSON.stringify(firstOutput)};`]),
    `  writeFileSync(${JSON.stringify(marker)}, "started");`,
    '  throw new Error("Process started before validation");',
    "};",
    "syncBuiltinESMExports();",
  ].join("\n"));
  assert.throws(() => execFileSync(process.execPath, ["--import", preload, selector, ...args], { env, stdio: "pipe" }), message);
  assert.equal(existsSync(marker), false, "Unvalidated values must never reach execFileSync");
}

for (const target of ["--help", "HEAD^1\n", `${"a".repeat(40)}\n`, "main", "HEAD", "a".repeat(39)]) {
  test(`process validation: unsafe since target ${JSON.stringify(target)} is rejected before execution`, (t) => {
    assertRejectedBeforeProcess(t, ["--since", target], /Invalid since target/);
  });
}

for (const installation of ["--help", `${process.env.DOTNET_ROOT ?? "/usr/local/share/dotnet"}\n`]) {
  test(`process validation: unsafe DOTNET_ROOT ${JSON.stringify(installation)} is rejected before execution`, (t) => {
    assertRejectedBeforeProcess(t, ["--since", "HEAD^1"], /DOTNET_ROOT must/, { ...process.env, DOTNET_ROOT: installation });
  });
}

for (const resolved of ["--help\n", `${"a".repeat(20)}\n${"a".repeat(20)}\n`]) {
  test(`process validation: invalid resolved commit ${JSON.stringify(resolved)} is rejected before another execution`, (t) => {
    assertRejectedBeforeProcess(t, ["--since", "HEAD^1"], /Invalid resolved commit/, process.env, resolved);
  });
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

for (const [file, shard] of [
  ["SendCodeCommand.cs", "application-auth-code"],
  ["VerifyCodeCommand.cs", "application-auth-code"],
  ["CreateDesktopHandoffCommand.cs", "application-auth-session"],
  ["ExchangeDesktopHandoffCommand.cs", "application-auth-session"],
  ["GoogleSignInFlow.cs", "application-auth-session"],
]) {
  test(`diff spans: auth file ${file} selects exactly its owning shard`, (t) => {
    const path = `src/Orbit.Application/Auth/Commands/${file}`;
    const scopes = select(change(t, path), "base");
    assert.deepEqual(scopes.map((scope) => scope.shard), [shard]);
    assertOneOwner(scopes, path, 13, 33);
  });
}

const nightly = () => select().filter(({ project }) => project === "domain");

function owns(scope, file, start, end) {
  const relative = file.startsWith("src/") ? file.split("/").slice(2).join("/") : `Entities/${file}`;
  const matches = (pattern) => {
    const glob = pattern.replace(/^!/, "").split("{")[0].slice(3);
    const matcher = new RegExp(`^${glob.split("*").map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("[^/]*")}$`);
    if (!matcher.test(relative)) return false;
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
  const matrix = select();
  assert.equal(matrix.filter(({ project }) => project === "domain").length, 13);
  assert.equal(matrix.filter(({ project }) => project === "application").length, 13);
  for (const project of ["domain", "application"]) {
    const scopes = matrix.filter((scope) => scope.project === project);
    const config = JSON.parse(readFileSync(new URL(`../../stryker-config.${project}.json`, import.meta.url), "utf8"))["stryker-config"];
    const directory = new URL(`../../src/Orbit.${project === "domain" ? "Domain" : "Application"}/`, import.meta.url);
    const files = readdirSync(directory, { recursive: true }).filter((path) => path.endsWith(".cs"));
    const configured = { mutate: config.mutate };
    const path = (relative) => `src/Orbit.${project === "domain" ? "Domain" : "Application"}/${relative}`;
    assert.deepEqual(files.filter((file) => scopes.some((scope) => owns(scope, path(file), 0, 0))),
      files.filter((file) => owns(configured, path(file), 0, 0)));
    assert.deepEqual(config.thresholds, { high: 80, low: 60, break: 0 });
  }
  const infrastructure = JSON.parse(readFileSync(new URL("../../stryker-config.infrastructure.json", import.meta.url), "utf8"))["stryker-config"];
  assert.deepEqual(infrastructure.thresholds, { high: 80, low: 60, break: 0 });
  assert.deepEqual(matrix.at(-1), { project: "infrastructure" });
  assert.match(readFileSync(new URL("../workflows/mutation.yml", import.meta.url), "utf8"), /timeout-minutes: 45\b/);
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

test("diff spans: the full User source with the PR 876 doc-only edit selects nothing", (t) => {
  const source = readFileSync(new URL(`../../${userPath}`, import.meta.url), "utf8");
  const oldSummary = "Social storage retained until https://github.com/thomasluizon/orbit-tickets/issues/1555 contracts the schema.";
  const newSummary = "Compatibility-only storage with no runtime caller or mapped writes, retained until https://github.com/thomasluizon/orbit-tickets/issues/1654 contracts the schema.";
  const declaration = "public partial class User : Entity";
  assert.ok(source.includes(declaration));
  const before = source.replace(declaration, `/// <summary>${oldSummary}</summary>\n${declaration}`);
  const after = before.replace(oldSummary, newSummary);
  assert.notEqual(before, after);
  assert.deepEqual(select(change(t, userPath, before, after), "base"), []);
});

for (const [name, expression, replacement] of [
  ["binary expression", "left &&\n        right", "other"],
  ["conditional expression", "left\n        ? right\n        : false", "other"],
  ["invocation statement", "Check(\n        right\n    )", "other"],
]) {
  test(`diff spans: a changed inner line preserves the complete multiline ${name}`, (t) => {
    const source = `class User {\n    bool Value => ${expression};\n}\n`;
    const revised = source.replace("right", replacement);
    const matrix = select(change(t, userPath, source, revised), "base");
    const start = revised.indexOf(expression.split("\n")[0]);
    const end = revised.lastIndexOf(";");
    assert.equal(matrix.filter((scope) => owns(scope, "User.cs", start, end)).length, 1,
      `The complete multiline expression at ${start}..${end} must remain mutable`);
  });
}

test("diff spans: widening across a nightly boundary preserves the complete mutant in one shard", (t) => {
  const prefix = `/*${" ".repeat(6450)}*/\n`;
  const source = prefix + "class User {\n    bool Value => left &&\n        right &&\n        tail;\n}\n";
  const revised = source.replace("right", "other");
  const matrix = select(change(t, userPath, source, revised), "base");
  assert.equal(matrix.filter((scope) => owns(scope, "User.cs", revised.indexOf("left"), revised.indexOf(";"))).length, 1);
});

test("diff spans: executable resolution ignores writable PATH entries", (t) => {
  const repo = change(t);
  const bin = join(repo.root, "bin");
  mkdirSync(bin);
  for (const name of ["git", "dotnet"]) {
    writeFileSync(join(bin, name), "#!/bin/sh\nexit 97\n", { mode: 0o755 });
  }
  assert.deepEqual(select(repo, "base", { ...process.env, PATH: bin }), select(repo, "base"));
});

for (const path of [
  "src/Orbit.Application/Auth/Commands/--help.cs",
  "src/Orbit.Application/Auth/Commands/unsafe;argument.cs",
  "src/Orbit.Application/Auth/Commands/unsafe$(argument).cs",
  "src/Orbit.Application/Auth/Commands/unsafe\nargument.cs",
]) {
  test(`process validation: file arguments must be canonical in-scope paths: ${JSON.stringify(path)}`, (t) => {
    assert.throws(() => select(change(t, path), "base"), /Invalid in-scope repository path/);
  });
}

test("nightly chat habit spans split the measured slow prefix and retain the completed suffix", () => {
  const scopes = select().filter(({ shard }) => shard?.startsWith("application-chat-habits-"));
  assert.deepEqual(scopes.map(({ mutate }) => mutate), [
    ["**/Chat/Tools/Implementations/CreateHabitTool.cs{0..10500}", "**/Chat/Tools/Implementations/ManageAccountTool.cs"],
    ["**/Chat/Tools/Implementations/CreateHabitTool.cs{0..15000}", "!**/Chat/Tools/Implementations/CreateHabitTool.cs{0..10500}"],
    ["**/Chat/Tools/Implementations/CreateHabitTool.cs", "!**/Chat/Tools/Implementations/CreateHabitTool.cs{0..15000}", "**/Chat/Tools/Implementations/ProfileTools.cs"],
  ]);
  const path = "src/Orbit.Application/Chat/Tools/Implementations/CreateHabitTool.cs";
  for (const boundary of [10500, 15000]) {
    assertOneOwner(scopes, path, boundary - 1, boundary);
    assertOneOwner(scopes, path, boundary, boundary + 1);
    assertOneOwner(scopes, path, boundary - 1, boundary + 1);
    const owner = scopes.find((scope) => owns(scope, path, boundary, boundary + 1));
    assert.throws(() => assertOneOwner(scopes.filter((scope) => scope !== owner), path, boundary, boundary + 1), /exactly one owner/);
    assert.throws(() => assertOneOwner([...scopes, owner], path, boundary, boundary + 1), /exactly one owner/);
  }
});

test("nightly chat processing spans split the slow middle shard and retain companion coverage", () => {
  const scopes = select().filter(({ shard }) => shard?.startsWith("application-chat-processing-"));
  assert.deepEqual(scopes, [
    {
      project: "application", shard: "application-chat-processing-1",
      mutate: ["**/Chat/Commands/ProcessUserChatCommand.Tools.cs{0..18000}"],
    },
    {
      project: "application", shard: "application-chat-processing-2",
      mutate: ["**/Chat/Commands/ProcessUserChatCommand.Tools.cs{0..25000}", "!**/Chat/Commands/ProcessUserChatCommand.Tools.cs{0..18000}", "**/Chat/Commands/ProcessUserChatCommand.Correction.cs"],
    },
    {
      project: "application", shard: "application-chat-processing-3",
      mutate: ["**/Chat/Commands/ProcessUserChatCommand.Tools.cs{0..32000}", "!**/Chat/Commands/ProcessUserChatCommand.Tools.cs{0..25000}"],
    },
    {
      project: "application", shard: "application-chat-processing-4",
      mutate: ["**/Chat/Commands/ProcessUserChatCommand.Tools.cs", "!**/Chat/Commands/ProcessUserChatCommand.Tools.cs{0..32000}"],
    },
  ]);
});

test("nightly scopes split each timed-out or near-limit shard", () => {
  const scopes = select();
  assert.ok(scopes.filter(({ shard }) => shard?.startsWith("domain-habit-update-freeze-")).length >= 3);
  assert.ok(scopes.filter(({ shard }) => shard?.startsWith("domain-user-invariants-")).length >= 3);
  assert.ok(scopes.filter(({ project }) => project === "application").length > 1);
});

test("diff spans: enclosing statements and blocks preserve multiline mutants within their member", (t) => {
  const source = "class User {\n    void Update() {\n        Check(\n            right\n        );\n    }\n    bool Other => true;\n}\n";
  const revised = source.replace("right", "other");
  const matrix = select(change(t, userPath, source, revised), "base");
  assert.equal(matrix.filter((scope) => owns(scope, "User.cs", revised.indexOf("{", revised.indexOf("Update")), revised.indexOf("    }") + 5)).length, 1);
  assert.equal(matrix.filter((scope) => owns(scope, "User.cs", revised.indexOf("true"), revised.indexOf("true") + 4)).length, 0);
});

test("application: every in-scope character and boundary-crossing span has exactly one nightly owner", () => {
  const scopes = select().filter(({ project }) => project === "application");
  const directory = new URL("../../src/Orbit.Application/", import.meta.url);
  const config = JSON.parse(readFileSync(new URL("../../stryker-config.application.json", import.meta.url), "utf8"))["stryker-config"];
  const configured = { mutate: config.mutate };
  const sizes = new Map(scopes.map(({ shard }) => [shard, 0]));
  for (const relative of readdirSync(directory, { recursive: true }).filter((path) => path.endsWith(".cs"))) {
    const path = `src/Orbit.Application/${relative}`;
    const matching = scopes.filter((scope) => owns(scope, path, 0, 0)
      || scope.mutate.some((pattern) => pattern.replace(/^!/, "").split("{")[0] === `**/${relative}`));
    if (!owns(configured, path, 0, 0)) {
      assert.equal(matching.length, 0, `${path} must remain outside the nightly scope`);
      continue;
    }
    const source = readFileSync(new URL(relative, directory), "utf8");
    for (let start = 0; start < source.length; start++) {
      assertOneOwner(matching, path, start, start + 1);
      const owner = matching.find((scope) => owns(scope, path, start, start + 1));
      sizes.set(owner.shard, sizes.get(owner.shard) + 1);
    }
    const boundaries = matching.flatMap(({ mutate }) => mutate.flatMap((pattern) => [...pattern.matchAll(/\{\d+\.\.(\d+)\}/g)].map((match) => Number(match[1]))));
    const positions = [...new Set([0, source.length, 1000000, ...boundaries.flatMap((value) => [value - 1, value, value + 1])])];
    for (const start of positions) {
      for (const end of positions.filter((value) => value >= start)) assertOneOwner(matching, path, start, end);
    }
  }
  for (const [shard, size] of sizes) assert.ok(size > 0 && size < 30000, `${shard} owns ${size} measured UTF-16 characters`);
  const first = scopes.find(({ shard }) => shard === "application-chat-processing-1");
  const path = "src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.Tools.cs";
  assert.throws(() => assertOneOwner(scopes.filter((scope) => scope !== first), path, 0, 1), /exactly one owner/);
  assert.throws(() => assertOneOwner([...scopes, first], path, 0, 1), /exactly one owner/);
});

for (const [path, boundary] of [
  ["src/Orbit.Domain/Entities/Habit.cs", 31500],
  ["src/Orbit.Domain/Entities/HabitInvariants.cs", 2000],
  ["src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.Tools.cs", 18000],
  ["src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.Tools.cs", 25000],
  ["src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.Tools.cs", 32000],
  ["src/Orbit.Application/Chat/Tools/Implementations/CreateHabitTool.cs", 10500],
  ["src/Orbit.Application/Chat/Tools/Implementations/CreateHabitTool.cs", 15000],
]) {
  test(`diff spans: ${path} routes multiline changes across ${boundary} without dropping the mutant`, (t) => {
    const prefix = `/*${" ".repeat(boundary - 50)}*/\n`;
    const source = prefix + "class User {\n    bool Value => left &&\n        right &&\n        tail;\n}\n";
    const revised = source.replace("right", "other");
    const matrix = select(change(t, path, source, revised), "base");
    assert.equal(matrix.filter((scope) => owns(scope, path, revised.indexOf("left"), revised.indexOf(";"))).length, 1);
  });
}
