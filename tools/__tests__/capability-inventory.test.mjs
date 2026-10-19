import assert from "node:assert/strict"
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { createHash } from "node:crypto"
import { test } from "node:test"
import { buildInventory, checkBaseline, gapReport } from "../capability-inventory.mjs"

const root = resolve(import.meta.dirname, "../..")
const inventory = buildInventory(root)
const baseline = value => ({ inputs: value.provenance.inputs, artifactDigest: createHash("sha256").update(JSON.stringify(value)).digest("hex") })
const field = (value, entity, name) => value.entities.find(row => row.entity === entity).fields.find(row => row.name === name)

function mutate(path, transform, verify) {
  const copy = mkdtempSync(join(tmpdir(), "orbit-inventory-"))
  try {
    cpSync(join(root, "src"), join(copy, "src"), { recursive: true, filter: source => !/[\\/](bin|obj|Migrations)([\\/]|$)/.test(source) })
    cpSync(join(root, "tools"), join(copy, "tools"), { recursive: true })
    const target = join(copy, path)
    const original = readFileSync(target, "utf8")
    const changed = transform(original)
    assert.notEqual(changed, original, "Mutation must exercise real source")
    writeFileSync(target, changed)
    verify(() => buildInventory(copy))
  } finally {
    rmSync(copy, { recursive: true })
  }
}

test("both real registries, all scoped entity views and inherited fields have source evidence", () => {
  assert.ok(inventory.counts.registeredChat > inventory.counts.chat)
  assert.ok(inventory.counts.mcp > inventory.counts.chat)
  for (const entity of ["Habit", "SubHabit", "HabitLog", "Goal", "Tag", "ChecklistTemplate", "Notification", "PushSubscription", "Profile", "Subscription", "Referral", "ApiKey", "CalendarSync", "UserFact", "Gamification"]) {
    const row = inventory.entities.find(item => item.entity === entity)
    assert.ok(row.fields.length, entity)
    for (const property of row.fields) {
      assert.match(property.domain.source, /\.cs:\d+$/)
      assert.ok(property.persistence.length)
      for (const surface of ["chat", "mcp"]) assert.equal(Object.keys(property.surfaces[surface].operations).length, 6)
    }
  }
  assert.ok(inventory.capabilities.length > 0)
  assert.ok(inventory.capabilities.every(row => row.surfaces.chat && row.surfaces.mcp))
  assert.equal(field(inventory, "Profile", "xmin").shadow, true)
})

test("generated output is deterministic and the accepted baseline passes", () => {
  assert.deepEqual(buildInventory(root), inventory)
  checkBaseline(inventory, baseline(inventory))
})

test("creation without update and partial readback are gaps", () => {
  const template = field(inventory, "ChecklistTemplate", "Items").surfaces.chat
  assert.equal(template.operations.create.status, "covered")
  assert.equal(template.operations.read.status, "covered")
  assert.ok(template.problems.includes("create-without-update"))
  const checklist = field(inventory, "Habit", "ChecklistItems").surfaces.chat
  assert.equal(checklist.operations.read.status, "partial")
  assert.equal(checklist.verdict, "gap")
  assert.equal(field(inventory, "Habit", "Description").surfaces.chat.operations.read.status, "absent")
  assert.ok(field(inventory, "Profile", "Name").surfaces.chat.problems.includes("domain-edit-without-tool-update"))
  assert.ok(field(inventory, "Habit", "RelativeReminders").surfaces.chat.problems.includes("domain-edit-without-tool-update"))
  assert.match(gapReport(inventory), /## Gaps to file/)
  assert.match(gapReport(inventory), /### Profile.Name/)
})

test("MCP gateway coverage retains direct differences, known tickets and deliberate memory removal", () => {
  const reminder = field(inventory, "Habit", "ReminderEnabled").surfaces.mcp.operations.create
  assert.equal(reminder.direct.length, 0)
  assert.equal(reminder.viaOperations[0].via, "execute_agent_operation_v2")
  assert.equal(field(inventory, "Habit", "Emoji").surfaces.mcp.operations.read.status, "covered")
  for (const number of [513, 246, 396, 19, 235]) assert.ok(inventory.knownGaps.some(row => row.ticket.endsWith(`#${number}`)))
  assert.ok(!inventory.gaps.some(row => row.entity === "UserFact"))
  assert.ok(!inventory.gaps.some(row => row.entity === "PushSubscription"))
  assert.ok(inventory.gaps.some(row => row.problems.includes("direct-update-requires-unchanged-title")))
})

test("nested writable fields and filter predicates have distinct parameter paths", () => {
  const tool = inventory.registries.chat.find(row => row.name === "bulk_update_habits")
  for (const name of ["filter.all", "filter.is_general", "updates.emoji", "updates.checklist_items[].text"]) assert.ok(tool.parameters.some(row => row.name === name), name)
  const general = field(inventory, "Habit", "IsGeneral").surfaces.chat.operations.filterKeyedBulk
  assert.equal(general.direct.length, 0, "A selector must not be counted as a write")
  const mcp = inventory.registries.mcp.find(row => row.name === "bulk_update_habits")
  assert.ok(mcp.parameters.some(row => row.name === "updatesJson.emoji"))
  assert.ok(inventory.registries.chat.find(row => row.name === "bulk_create_habits").parameters.some(row => row.name === "habits[].sub_habits[].emoji"))
})

test("removing a real registration removes coverage and fails the drift gate", () => {
  mutate("src/Orbit.Api/Extensions/ServiceCollectionExtensions.AiServices.cs", text => text.replace("builder.Services.AddScoped<IAiTool, CreateTagTool>();", ""), build => {
    const changed = build()
    assert.equal(field(changed, "Tag", "Color").surfaces.chat.operations.create.status, "absent")
    assert.throws(() => checkBaseline(changed, baseline(inventory)), /review required/)
  })
})

test("a new domain field is inventoried and fails review rather than inheriting coverage", () => {
  mutate("src/Orbit.Domain/Entities/Tag.cs", text => text.replace("public string Color", "public string InventoryProbe { get; private set; }\n    public string Color"), build => {
    const changed = build()
    assert.equal(field(changed, "Tag", "InventoryProbe").surfaces.chat.operations.read.status, "absent")
    assert.throws(() => checkBaseline(changed, baseline(inventory)), /review required/)
  })
})

test("removing a returned field invalidates its semantic binding", () => {
  mutate("src/Orbit.Api/Mcp/Tools/HabitTools.cs", text => text.replace('h.Emoji ?? "None"', '"None"'), build => {
    assert.throws(build, /Missing rendered field mcp:get_habit h.Emoji/)
  })
})

test("returned DTO drift is covered even outside Queries directories", () => {
  mutate("src/Orbit.Application/Subscriptions/SubscriptionDtos.cs", text => text.replace("string Plan", "string InventoryProbe"), build => {
    assert.throws(() => checkBaseline(build(), baseline(inventory)), /SubscriptionDtos.cs/)
  })
})
