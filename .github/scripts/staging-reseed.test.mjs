import assert from "node:assert/strict"
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { spawnSync } from "node:child_process"
import { test } from "node:test"

const root = resolve(import.meta.dirname, "../..")
const workflow = readFileSync(join(root, ".github/workflows/staging-reseed.yml"), "utf8")
const plans = JSON.parse(readFileSync(join(import.meta.dirname, "staging-reseed-plans.json"), "utf8"))
const steps = [...workflow.matchAll(/^      - name: (.+)\n([\s\S]*?)(?=^      - |$(?![\s\S]))/gm)]
  .map(([, name, body]) => ({ name, body }))

function runStep(t, name, options = {}) {
  const step = steps.find(candidate => candidate.name === name)
  assert.ok(step, `Workflow step exists: ${name}`)
  const run = step.body.match(/^        run: \|\n([\s\S]*)/m)
  const script = run ? run[1].replace(/^          /gm, "") : step.body.match(/^        run: (.+)$/m)?.[1]
  assert.ok(script, `Workflow step has a shell command: ${name}`)
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
  process.stdout.write("dpg-dasotg8jo6nc73cok6g0-a")
}
`, { mode: 0o755 })
  writeFileSync(join(directory, "dotnet"), "#!/usr/bin/env bash\nprintf 'dotnet\\n' >> \"$MOCK_CALLS\"\n", { mode: 0o755 })
  const scriptPath = join(directory, "step.sh")
  writeFileSync(scriptPath, (options.script ?? script) + "\n")
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
  }
  delete env.BASH_ENV
  delete env.STAGING_RESEED_STEP
  if (bashEnv) env.BASH_ENV = bashEnv.replace("${{ github.workspace }}", root)
  if (stepName) env.STAGING_RESEED_STEP = stepName
  const result = spawnSync("bash", ["-e", scriptPath], { cwd: root, env, encoding: "utf8" })
  return { ...result, calls: readFileSync(callsPath, "utf8"), output: readFileSync(outputPath, "utf8") }
}

const replaceStep = "Replace staging database and refresh its API connection"

test("replacement targets only the database and its environment group, leaving the link untouched", t => {
  const result = runStep(t, replaceStep)
  assert.equal(result.status, 0, result.stderr || "The plan guard rejected an untargeted replacement")
  assert.match(result.calls, /"apply"/)
  assert.equal(result.output, "database_id=dpg-dasotg8jo6nc73cok6g0-a\n")
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
