import assert from "node:assert/strict"
import { existsSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { spawnSync } from "node:child_process"
import { test } from "node:test"

const root = resolve(import.meta.dirname, "../..")
const workflow = readFileSync(join(root, ".github/workflows/staging-reseed.yml"), "utf8")
const plans = JSON.parse(readFileSync(join(import.meta.dirname, "staging-reseed-plans.json"), "utf8"))
const steps = [...workflow.matchAll(/^      - name: (.+)\n([\s\S]*?)(?=^      - |$(?![\s\S]))/gm)]
  .map(([, name, body]) => ({ name, body }))

function stepScript(name) {
  const step = steps.find(candidate => candidate.name === name)
  assert.ok(step, `Workflow step exists: ${name}`)
  const run = step.body.match(/^        run: \|\n([\s\S]*)/m)
  const script = run ? run[1].replace(/^          /gm, "") : step.body.match(/^        run: (.+)$/m)?.[1]
  assert.ok(script, `Workflow step has a shell command: ${name}`)
  return script
}

function runStep(t, name, options = {}) {
  const step = steps.find(candidate => candidate.name === name)
  const script = stepScript(name)
  const directory = mkdtempSync(join(tmpdir(), "orbit-reseed-test-"))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const planPath = join(directory, "plan.json")
  const callsPath = join(directory, "calls")
  const outputPath = join(directory, "output")
  writeFileSync(join(directory, "plans.json"), JSON.stringify(plans))
  writeFileSync(callsPath, "")
  writeFileSync(outputPath, "")
  writeFileSync(join(directory, "terraform"), `#!/usr/bin/env node
import { appendFileSync, readFileSync, writeFileSync } from "node:fs"
const args = process.argv.slice(2)
const command = args[1]
appendFileSync(process.env.MOCK_CALLS, JSON.stringify(args) + "\\n")
if (command === process.env.MOCK_FAILURE) process.exit(42)
if (command === "plan") {
  const plans = JSON.parse(readFileSync(process.env.MOCK_PLANS, "utf8"))
  const targeted = args.includes("-target=render_postgres.staging") && args.includes("-target=render_env_group.staging_database")
  const plan = process.env.MOCK_UNSAFE === "true" ? plans.untargeted : targeted ? plans.targeted : plans.untargeted
  writeFileSync(process.env.MOCK_PLAN, JSON.stringify(plan))
} else if (command === "show") {
  process.stdout.write(readFileSync(process.env.MOCK_PLAN, "utf8"))
} else if (command === "output") {
  process.stdout.write(args.at(-1) === "staging_external_connection_string"
    ? "postgresql://orbit_staging:carry-test-secret@staging.example/orbit_staging"
    : "dpg-dasotg8jo6nc73cok6g0-a")
}
`, { mode: 0o755 })
  writeFileSync(join(directory, "dotnet"), "#!/usr/bin/env bash\nprintf 'dotnet\\n' >> \"$MOCK_CALLS\"\n", { mode: 0o755 })
  writeFileSync(join(directory, "psql"), `#!/usr/bin/env node
import { appendFileSync } from "node:fs"
let input = "", exported = false
process.stdin.setEncoding("utf8")
process.stdin.on("data", chunk => {
  input += chunk
  if (!exported && input.includes("SELECT pg_export_snapshot();")) {
    exported = true
    if (process.env.MOCK_FAILURE === "snapshot") process.exit(43)
    process.stdout.write("00000003-0000001B-1\\n")
  }
})
process.stdin.on("end", () => {
  appendFileSync(process.env.MOCK_CALLS, JSON.stringify({command: "psql", input}) + "\\n")
  if (process.env.MOCK_FAILURE === (exported ? "source counts" : "destination counts")) process.exit(43)
  process.stdout.write(process.env.MOCK_COUNTS)
})
`, { mode: 0o755 })
  for (const command of ["pg_dump", "pg_restore"]) {
    writeFileSync(join(directory, command), `#!/usr/bin/env node
import { appendFileSync, existsSync, writeFileSync } from "node:fs"
const args = process.argv.slice(2)
const operation = ${JSON.stringify(command)} === "pg_dump" ? "dump" : args.includes("--list") ? "archive" : "restore"
appendFileSync(process.env.MOCK_CALLS, JSON.stringify({command: ${JSON.stringify(command)}, args}) + "\\n")
if (process.env.MOCK_FAILURE === operation) process.exit(43)
if (operation === "dump") {
  writeFileSync(args.find(arg => arg.startsWith("--file=")).slice(7), "custom archive double")
} else if (!existsSync(args.at(-1))) process.exit(44)
`, { mode: 0o755 })
  }
  const scriptPath = join(directory, "step.sh")
  writeFileSync(scriptPath, [options.script ?? script, ...(options.then ?? []).map(stepScript)].join("\n"))
  const bashEnv = workflow.match(/^      BASH_ENV: (.+)$/m)?.[1]
  const stepName = step.body.match(/^          STAGING_RESEED_STEP: (.+)$/m)?.[1]
  const env = {
    ...process.env,
    PATH: `${directory}:${process.env.PATH}`,
    GITHUB_OUTPUT: outputPath,
    MOCK_CALLS: callsPath,
    MOCK_PLAN: planPath,
    MOCK_PLANS: join(directory, "plans.json"),
    MOCK_FAILURE: options.failure ?? "",
    MOCK_UNSAFE: String(options.unsafe ?? false),
    MOCK_COUNTS: options.counts ?? '["public", "Users", 4]\n["public", "Habits", 12]\n["public", "EmptyTable", 0]\n["public", "__EFMigrationsHistory", 7]\n',
    STAGING_CARRY_DIRECTORY: options.carryDirectory ?? join(directory, "carry"),
  }
  delete env.BASH_ENV
  delete env.STAGING_RESEED_STEP
  if (bashEnv) env.BASH_ENV = bashEnv.replace("${{ github.workspace }}", root)
  if (stepName) env.STAGING_RESEED_STEP = stepName
  const result = spawnSync("bash", ["-e", scriptPath], { cwd: root, env, encoding: "utf8" })
  return { ...result, calls: readFileSync(callsPath, "utf8"), output: readFileSync(outputPath, "utf8"),
    carryDirectory: env.STAGING_CARRY_DIRECTORY }
}

const replaceStep = "Replace staging database and refresh its API connection"

test("replacement targets only the database and its environment group, leaving the link untouched", t => {
  const result = runStep(t, replaceStep)
  assert.equal(result.status, 0, result.stderr || "The plan guard rejected an untargeted replacement")
  assert.match(result.calls, /"apply"/)
  assert.equal(result.output, "database_id=dpg-dasotg8jo6nc73cok6g0-a\n")
})

test("data carry brackets replacement and verifies before migrations", () => {
  const names = steps.map(step => step.name)
  const backup = names.indexOf("Back up staging data")
  const replace = names.indexOf(replaceStep)
  const restore = names.indexOf("Restore and verify staging data")
  const migrate = names.indexOf("Apply migrations to the replacement database")
  assert.ok(backup >= 0 && backup < replace, "Capture must succeed before replacement")
  assert.ok(restore > replace && restore < migrate, "Verify before migrations can change counts")
  for (const name of ["Back up staging data", replaceStep, "Restore and verify staging data"]) {
    assert.match(steps.find(step => step.name === name).body, /^        if: steps.age.outputs.replace == 'true'$/m)
  }
  assert.match(steps.find(step => step.name === "Delete local staging data").body, /^        if: always\(\)$/m)
  assert.doesNotMatch(workflow, /upload-artifact/)
})

test("data carry restores the archive and verifies every table in the shared snapshot", t => {
  const backup = runStep(t, "Back up staging data")
  assert.equal(backup.status, 0, backup.stderr)
  assert.ok(existsSync(join(backup.carryDirectory, "ready")))
  assert.equal(statSync(join(backup.carryDirectory, "database.dump")).mode & 0o777, 0o600)
  assert.match(backup.calls, /--format=custom/)
  assert.match(backup.calls, /--snapshot=00000003-0000001B-1/)
  assert.match(backup.calls, /REPEATABLE READ READ ONLY/)
  assert.match(backup.calls, /pg_catalog.pg_tables/)
  assert.match(backup.calls, /json_build_array/)
  const restore = runStep(t, "Restore and verify staging data", { carryDirectory: backup.carryDirectory,
    counts: '["public", "Habits", 12]\n["public", "EmptyTable", 0]\n["public", "__EFMigrationsHistory", 7]\n["public", "Users", 4]\n' })
  assert.equal(restore.status, 0, restore.stderr)
  assert.match(restore.stdout, /Verified staging data row counts for all 4 tables/)
  assert.match(restore.calls, /--exit-on-error/)
  assert.match(restore.calls, /--single-transaction/)
  for (const result of [backup, restore]) assert.doesNotMatch(result.stdout + result.stderr, /carry-test-secret|postgresql:\/\//)
})

test("successful data carry reaches migrations only after replacement and count verification", t => {
  const result = runStep(t, "Back up staging data", { then: [replaceStep,
    "Restore and verify staging data", "Apply migrations to the replacement database"] })
  assert.equal(result.status, 0, result.stderr)
  const calls = result.calls.split("\n").filter(Boolean)
  const dump = calls.findIndex(line => line.includes('"command":"pg_dump"'))
  const apply = calls.findIndex(line => line.includes('"apply"'))
  const restore = calls.findIndex(line => line.includes('"--single-transaction"'))
  const verify = calls.findLastIndex(line => line.includes('"command":"psql"'))
  const migrate = calls.indexOf("dotnet")
  assert.ok(dump >= 0 && apply > dump && restore > apply && verify > restore && migrate > verify)
})

test("data restore refuses an incomplete backup", t => {
  const result = runStep(t, "Restore and verify staging data", { then: ["Apply migrations to the replacement database"] })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /backup is incomplete/)
  assert.doesNotMatch(result.calls, /pg_restore|dotnet/)
})

for (const counts of [
  '["public", "Users", 3]\n["public", "Habits", 13]\n["public", "EmptyTable", 0]\n["public", "__EFMigrationsHistory", 7]\n',
  '["public", "Users", 4]\n["public", "Habits", 12]\n["public", "__EFMigrationsHistory", 7]\n',
  '["public", "Users", 4]\n["public", "Habits", 12]\n["public", "EmptyTable", 0]\n["public", "__EFMigrationsHistory", 7]\n["other", "Extra", 0]\n',
]) {
  test(`data carry rejects changed counts or tables: ${counts.trim()}`, t => {
    const backup = runStep(t, "Back up staging data")
    assert.equal(backup.status, 0, backup.stderr)
    const restore = runStep(t, "Restore and verify staging data", { carryDirectory: backup.carryDirectory, counts,
      then: ["Apply migrations to the replacement database"] })
    assert.notEqual(restore.status, 0, restore.stdout)
    assert.match(restore.stderr, /Staging data row counts do not match/)
    assert.match(restore.stderr, /Restore and verify staging data/)
    assert.doesNotMatch(restore.calls, /dotnet/)
  })
}

for (const failure of ["output", "snapshot", "dump", "source counts", "archive"]) {
  test(`data backup blocks replacement when ${failure} fails`, t => {
    const result = runStep(t, "Back up staging data", { failure, then: [replaceStep] })
    assert.notEqual(result.status, 0)
    assert.ok(!existsSync(join(result.carryDirectory, "ready")))
    assert.doesNotMatch(result.calls, /"apply"/)
    assert.match(result.stderr, /Back up staging data/)
    assert.doesNotMatch(result.stdout + result.stderr, /carry-test-secret/)
  })
}

for (const counts of ["", '["public", "Users", "4"]\n', '["public", "Users", 4]\n["public", "Users", 4]\n']) {
  test(`data backup rejects incomplete count output: ${JSON.stringify(counts)}`, t => {
    const result = runStep(t, "Back up staging data", { counts })
    assert.notEqual(result.status, 0)
    assert.ok(!existsSync(join(result.carryDirectory, "ready")))
  })
}

for (const failure of ["restore", "destination counts"]) {
  test(`data carry stops before migrations when ${failure} fails`, t => {
    const backup = runStep(t, "Back up staging data")
    assert.equal(backup.status, 0, backup.stderr)
    const result = runStep(t, "Restore and verify staging data", { carryDirectory: backup.carryDirectory, failure,
      then: ["Apply migrations to the replacement database"] })
    assert.notEqual(result.status, 0)
    assert.doesNotMatch(result.calls, /dotnet/)
    assert.match(result.stderr, /Restore and verify staging data/)
    assert.doesNotMatch(result.stdout + result.stderr, /carry-test-secret/)
  })
}

test("local staging data is deleted after success and after failure", t => {
  for (const failure of ["", "source counts"]) {
    const backup = runStep(t, "Back up staging data", { failure })
    const cleanup = runStep(t, "Delete local staging data", { carryDirectory: backup.carryDirectory })
    assert.equal(cleanup.status, 0, cleanup.stderr)
    assert.ok(!existsSync(backup.carryDirectory))
  }
})

test("replacement still rejects changes to the environment group link", t => {
  const result = runStep(t, replaceStep, { unsafe: true })
  assert.notEqual(result.status, 0)
  assert.doesNotMatch(result.calls, /"apply"/)
  assert.match(result.stderr, /render_env_group_link\.staging_database/)
})

for (const command of ["plan", "show", "apply", "output"]) {
  test(`replacement reports a failed Terraform ${command} command`, t => {
    const result = runStep(t, replaceStep, { failure: command })
    assert.equal(result.status, 42, result.stderr)
    assert.match(result.stderr, /Replace staging database and refresh its API connection/)
    assert.match(result.stderr, new RegExp(`terraform .*${command}`))
    assert.match(result.stderr, /42/)
    assert.equal(result.output, "")
  })
}

for (const name of ["Apply migrations to the replacement database", "Seed the owner account"]) {
  test(`${name} stops when a connection output cannot be read`, t => {
    const result = runStep(t, name, { failure: "output" })
    assert.equal(result.status, 42, result.stderr)
    assert.doesNotMatch(result.calls, /dotnet/)
    assert.match(result.stderr, new RegExp(name))
  })
}

for (const { name } of steps) {
  for (const script of ["false", "exit 7", 'value="$(false)"', "false | cat"]) {
    test(`${name} diagnoses ${script}`, t => {
      const result = runStep(t, name, { script })
      assert.notEqual(result.status, 0)
      assert.match(result.stderr, new RegExp(name))
      assert.ok(result.stderr.includes(script), result.stderr)
    })
  }
}
