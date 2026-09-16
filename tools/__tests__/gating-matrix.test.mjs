import { execFileSync, spawnSync } from "node:child_process"
import { cpSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import assert from "node:assert/strict"
import { dirname, join, resolve } from "node:path"
import { after, test } from "node:test"
import { fileURLToPath } from "node:url"

const REPOSITORY_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..")
const TOOL = join(REPOSITORY_ROOT, "tools", "gating-matrix.mjs")
const temporaryRoot = mkdtempSync(join(tmpdir(), "orbit-gating-matrix-"))

after(() => {
  rmSync(temporaryRoot, { recursive: true, force: true })
})

function generate(root = REPOSITORY_ROOT) {
  execFileSync("node", [TOOL, "--root", root], { encoding: "utf8" })
  return readFileSync(join(root, "gating-matrix.json"), "utf8")
}

function copySourceFixture(name) {
  const fixture = join(temporaryRoot, name)
  cpSync(join(REPOSITORY_ROOT, "src"), join(fixture, "src"), {
    recursive: true,
    filter: (source) => statSync(source).isDirectory() || source.endsWith(".cs"),
  })
  return fixture
}

function interfaceMethodNames(source) {
  const text = source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/.*$/gm, "")
  const interfaceStart = text.indexOf("interface IPayGateService")
  const bodyStart = text.indexOf("{", interfaceStart)
  const body = text.slice(bodyStart + 1, text.lastIndexOf("}"))
  const members = body.split(";").map((member) => member.trim()).filter(Boolean)
  return new Set(members.map((member) => {
    const open = member.indexOf("(")
    assert.notEqual(open, -1, `cannot read interface member: ${member}`)
    let prefix = member.slice(0, open).trim()
    if (prefix.endsWith(">")) {
      let depth = 0
      for (let index = prefix.length - 1; index >= 0; index -= 1) {
        if (prefix[index] === ">") depth += 1
        else if (prefix[index] === "<") {
          depth -= 1
          if (depth === 0) {
            prefix = prefix.slice(0, index).trim()
            break
          }
        }
      }
    }
    const name = prefix.match(/([A-Za-z_]\w*)$/)?.[1]
    assert.ok(name, `cannot read interface member: ${member}`)
    return name
  }))
}

function rewriteLineEndings(directory, newline) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) rewriteLineEndings(path, newline)
    else if (entry.name.endsWith(".cs")) {
      const normalized = readFileSync(path, "utf8").replace(/\r\n?/g, "\n")
      writeFileSync(path, normalized.replaceAll("\n", newline), "utf8")
    }
  }
}

test("the repository matrix is complete, deterministic, and explicit about runtime configuration", () => {
  const first = generate()
  const firstMatrix = JSON.parse(first)
  const second = generate()
  const secondMatrix = JSON.parse(second)

  assert.equal(second, first)
  assert.equal(secondMatrix.provenance.generatedFrom, firstMatrix.provenance.generatedFrom)
  assert.match(firstMatrix.provenance.generatedFrom, /^[0-9a-f]{12}$/)
  assert.equal(firstMatrix.provenance.baselineRef, null)
  assert.equal(firstMatrix.provenance.baselineSha, null)
  assert.ok(Number.isInteger(firstMatrix.provenance.inputFiles))
  assert.ok(firstMatrix.provenance.inputFiles > 0)

  const interfaceSource = readFileSync(
    join(REPOSITORY_ROOT, "src", "Orbit.Domain", "Interfaces", "IPayGateService.cs"),
    "utf8",
  )
  const interfaceMethods = interfaceMethodNames(interfaceSource)
  assert.equal(firstMatrix.gates.length, interfaceMethods.size)
  assert.deepEqual(new Set(firstMatrix.gates.map((gate) => gate.capability)), interfaceMethods)

  const amendedFlag = firstMatrix.featureFlags.find((flag) => flag.key === "gamification_free_tier")
  assert.deepEqual(amendedFlag, {
    key: "gamification_free_tier",
    enabled: true,
    planRequirement: null,
    quotaLiftedByPlan: null,
    declaredInCode: true,
  })

  const configurableGate = firstMatrix.gates.find((gate) => gate.appConfigs.length > 0)
  assert.ok(configurableGate)
  assert.equal(typeof configurableGate.appConfigs[0].key, "string")
  assert.notEqual(configurableGate.appConfigs[0].compiledDefaultExpression, null)
  assert.equal(Object.hasOwn(configurableGate.appConfigs[0], "enforcedValue"), false)
  assert.match(firstMatrix.note, /live AppConfigs row overrides/)

  for (const key of ["RetrospectiveProOnly", "SmartRescheduleProOnly"]) {
    const config = firstMatrix.appConfigs.find((candidate) => candidate.key === key)
    assert.ok(config)
    assert.notEqual(config.compiledDefault, null)
    assert.equal(config.seededInMigrations, false)
  }

  assert.deepEqual(
    {
      planRequirement: firstMatrix.gates.find((gate) => gate.capability === "CanCreateHabits")?.planRequirement,
      quotaLiftedByPlan: firstMatrix.gates.find((gate) => gate.capability === "CanCreateHabits")?.quotaLiftedByPlan,
    },
    { planRequirement: null, quotaLiftedByPlan: null },
  )
  assert.deepEqual(
    {
      planRequirement: firstMatrix.gates.find((gate) => gate.capability === "CanAccessCalendar")?.planRequirement,
      quotaLiftedByPlan: firstMatrix.gates.find((gate) => gate.capability === "CanAccessCalendar")?.quotaLiftedByPlan,
    },
    { planRequirement: "Pro", quotaLiftedByPlan: null },
  )
})

test("a gated capability with an unsupported condition fails closed", () => {
  const fixture = copySourceFixture("unrecognised-requirement")

  const implementationPath = join(fixture, "src", "Orbit.Application", "Common", "PayGateService.cs")
  const implementationSource = readFileSync(implementationPath, "utf8")
  const normalizedSource = implementationSource.replaceAll("\r\n", "\n")
  const quotaGuard = "        if (user.AiMessagesLocalDate == userToday && user.AiMessagesUsedToday >= messageLimit)"
  const unsupportedGuard = "        if (user.AiMessagesLocalDate == userToday)"
  assert.ok(normalizedSource.includes(quotaGuard))
  writeFileSync(implementationPath, normalizedSource.replace(quotaGuard, unsupportedGuard), "utf8")

  const result = spawnSync(process.execPath, [TOOL, "--root", fixture], { encoding: "utf8" })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /CanSendAiMessage/)
})

test("a quota failure cannot hide an unrelated unsupported condition", () => {
  const fixture = copySourceFixture("mixed-quota-and-plan")
  const implementationPath = join(fixture, "src", "Orbit.Application", "Common", "PayGateService.cs")
  const source = readFileSync(implementationPath, "utf8").replaceAll("\r\n", "\n")
  const anchor = `        if (IsProductionSmokeAccount(user.Email))
            return Result.Success();`
  const replacement = `${anchor}

        if (user.Email == "fixture@example.com")
            return Result.PayGateFailure("Fixture unsupported gate");`
  assert.ok(source.includes(anchor))
  writeFileSync(implementationPath, source.replace(anchor, replacement), "utf8")

  const result = spawnSync(process.execPath, [TOOL, "--root", fixture], { encoding: "utf8" })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /CanSendAiMessage/)
})

test("plan and quota conditions keep separate access semantics", () => {
  const cases = [
    {
      name: "combined-plan-and-quota",
      condition: "        if (!user.HasProAccess && user.AiMessagesUsedToday >= messageLimit)",
      expected: { planRequirement: null, quotaLiftedByPlan: "Pro" },
    },
    {
      name: "disjoined-plan-and-quota",
      condition: "        if (!user.HasProAccess || user.AiMessagesUsedToday >= messageLimit)",
      expected: { planRequirement: "Pro", quotaLiftedByPlan: null },
    },
    {
      name: "plan-only",
      condition: "        if (!user.HasProAccess)",
      expected: { planRequirement: "Pro", quotaLiftedByPlan: null },
    },
    {
      name: "plan-scoped-quota-only",
      condition: "        if (user.AiMessagesUsedToday >= messageLimit)",
      expected: { planRequirement: null, quotaLiftedByPlan: "Pro" },
    },
    {
      name: "unscoped-quota-only",
      condition: "        if (user.AiMessagesUsedToday >= messageLimit)",
      declaration: "        var messageLimit = freeLimit;",
      expected: { planRequirement: null, quotaLiftedByPlan: null },
    },
  ]

  for (const fixtureCase of cases) {
    const fixture = copySourceFixture(fixtureCase.name)
    const implementationPath = join(fixture, "src", "Orbit.Application", "Common", "PayGateService.cs")
    const source = readFileSync(implementationPath, "utf8").replaceAll("\r\n", "\n")
    const quotaDeclaration = "        var messageLimit = user.HasProAccess ? proLimit : freeLimit;"
    const quotaGuard = "        if (user.AiMessagesLocalDate == userToday && user.AiMessagesUsedToday >= messageLimit)"
    assert.ok(source.includes(quotaDeclaration))
    assert.ok(source.includes(quotaGuard))
    writeFileSync(
      implementationPath,
      source
        .replace(quotaDeclaration, fixtureCase.declaration ?? quotaDeclaration)
        .replace(quotaGuard, fixtureCase.condition),
      "utf8",
    )

    const matrix = JSON.parse(generate(fixture))
    const gate = matrix.gates.find((candidate) => candidate.capability === "CanSendAiMessage")
    assert.deepEqual(
      { planRequirement: gate?.planRequirement, quotaLiftedByPlan: gate?.quotaLiftedByPlan },
      fixtureCase.expected,
      fixtureCase.name,
    )
  }
})

test("a condition with distinct plan guards fails closed", () => {
  const fixture = copySourceFixture("distinct-plan-guards")
  const implementationPath = join(fixture, "src", "Orbit.Application", "Common", "PayGateService.cs")
  const source = readFileSync(implementationPath, "utf8").replaceAll("\r\n", "\n")
  const quotaGuard = "        if (user.AiMessagesLocalDate == userToday && user.AiMessagesUsedToday >= messageLimit)"
  const ambiguousGuard = "        if (!user.HasProAccess && !user.HasTeamAccess)"
  assert.ok(source.includes(quotaGuard))
  writeFileSync(implementationPath, source.replace(quotaGuard, ambiguousGuard), "utf8")

  const result = spawnSync(process.execPath, [TOOL, "--root", fixture], { encoding: "utf8" })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /cannot derive plan requirement for CanSendAiMessage/)
})

test("provenance is stable across LF and CRLF checkouts", () => {
  const lfFixture = copySourceFixture("lf-checkout")
  const crlfFixture = copySourceFixture("crlf-checkout")
  rewriteLineEndings(join(lfFixture, "src"), "\n")
  rewriteLineEndings(join(crlfFixture, "src"), "\r\n")

  assert.equal(generate(crlfFixture), generate(lfFixture))
})

test("generic interface methods cannot disappear from the matrix", () => {
  const fixture = copySourceFixture("generic-interface-method")
  const interfacePath = join(fixture, "src", "Orbit.Domain", "Interfaces", "IPayGateService.cs")
  const interfaceSource = readFileSync(interfacePath, "utf8")
  const interfaceClose = interfaceSource.lastIndexOf("}")
  writeFileSync(
    interfacePath,
    `${interfaceSource.slice(0, interfaceClose)}    Task<Result> CanExport<T>(Guid userId, CancellationToken ct = default);\n${interfaceSource.slice(interfaceClose)}`,
    "utf8",
  )

  const implementationPath = join(fixture, "src", "Orbit.Application", "Common", "PayGateService.cs")
  const implementationSource = readFileSync(implementationPath, "utf8")
  const implementationClose = implementationSource.lastIndexOf("}")
  writeFileSync(
    implementationPath,
    `${implementationSource.slice(0, implementationClose)}    public Task<Result> CanExport<T>(Guid userId, CancellationToken ct = default) =>\n        Task.FromResult(Result.Success());\n${implementationSource.slice(implementationClose)}`,
    "utf8",
  )

  const matrix = JSON.parse(generate(fixture))
  assert.ok(matrix.gates.some((gate) => gate.capability === "CanExport"))
})

test("SQL mutations of gating tables fail closed", () => {
  const fixture = copySourceFixture("gating-table-sql")
  const migrationPath = join(
    fixture,
    "src",
    "Orbit.Infrastructure",
    "Migrations",
    "99999999999999_UnsupportedGatingSql.cs",
  )
  writeFileSync(
    migrationPath,
    `using Microsoft.EntityFrameworkCore.Migrations;

namespace Orbit.Infrastructure.Migrations;

public partial class UnsupportedGatingSql : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE \\\"AppFeatureFlags\\\" SET \\\"Enabled\\\" = false;");
    }
}
`,
    "utf8",
  )

  const result = spawnSync(process.execPath, [TOOL, "--root", fixture], { encoding: "utf8" })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /AppFeatureFlags/)
  assert.match(result.stderr, /99999999999999_UnsupportedGatingSql\.cs/)
})

test("deleting an interface method removes only its generated gate", () => {
  const fixture = copySourceFixture("deletion")

  const before = JSON.parse(generate(fixture))
  const interfacePath = join(fixture, "src", "Orbit.Domain", "Interfaces", "IPayGateService.cs")
  const interfaceSource = readFileSync(interfacePath, "utf8")
  const declaration = interfaceSource.match(/\s+Task(?:<[^;()]+>)?\s+(\w+)\s*\([^;]+;/)
  assert.ok(declaration)
  writeFileSync(interfacePath, interfaceSource.replace(declaration[0], ""), "utf8")

  const afterMatrix = JSON.parse(generate(fixture))
  assert.equal(afterMatrix.gates.length, before.gates.length - 1)
  assert.deepEqual(
    afterMatrix.gates,
    before.gates.filter((gate) => gate.capability !== declaration[1]),
  )
})

test("the generator carries no sample gate, key, or limit literals", () => {
  const source = readFileSync(TOOL, "utf8")
  assert.equal(source.includes("CanCreateHabits"), false)
  assert.equal(source.includes("FreeMaxHabits"), false)
  assert.equal(source.includes("GoalsProOnly"), false)
  assert.equal(/\b(?:5|10|20|50|500|1000)\b/.test(source), false)
})
