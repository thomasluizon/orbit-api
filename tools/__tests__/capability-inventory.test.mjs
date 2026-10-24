import assert from "node:assert/strict"
import { cpSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { execFileSync, spawnSync } from "node:child_process"
import { test } from "node:test"
import { buildInventory, checkBaseline, gapReport, semanticSnapshot } from "../capability-inventory.mjs"

const root = resolve(import.meta.dirname, "../..")
const inventory = buildInventory(root)
const baseline = semanticSnapshot
const field = (value, entity, name) => value.entities.find(row => row.entity === entity).fields.find(row => row.name === name)

function mutate(path, transform, verify, accept = false) {
  const copy = realpathSync(mkdtempSync(join(tmpdir(), "orbit-inventory-")))
  try {
    cpSync(join(root, "src"), join(copy, "src"), { recursive: true, filter: source => !/[\\/](bin|obj|Migrations)([\\/]|$)/.test(source) })
    cpSync(join(root, "tools"), join(copy, "tools"), { recursive: true })
    if (accept) execFileSync(process.execPath, [join(copy, "tools/capability-inventory.mjs"), "--accept"])
    const target = join(copy, path)
    const original = readFileSync(target, "utf8")
    const changed = transform(original)
    assert.notEqual(changed, original, "Mutation must exercise real source")
    writeFileSync(target, changed)
    verify(() => buildInventory(copy), copy)
  } finally {
    rmSync(copy, { recursive: true })
  }
}

test("both real registries, all scoped entity views and inherited fields have source evidence", () => {
  assert.ok(inventory.counts.registeredChat > inventory.counts.chat)
  assert.ok(inventory.counts.mcp > inventory.counts.chat)
  for (const entity of ["Habit", "SubHabit", "HabitLog", "Goal", "Tag", "ChecklistTemplate", "Notification", "PushSubscription", "Profile", "Subscription", "Referral", "ApiKey", "CalendarSync", "Gamification"]) {
    const row = inventory.entities.find(item => item.entity === entity)
    assert.ok(row.fields.length, entity)
    for (const property of row.fields) {
      assert.match(property.domain.source, /\.cs#\w+\.\w+$/)
      assert.ok(property.persistence.length)
      for (const surface of ["chat", "mcp"]) assert.equal(Object.keys(property.surfaces[surface].operations).length, 6)
    }
  }
  assert.ok(inventory.capabilities.length > 0)
  assert.ok(inventory.capabilities.every(row => row.surfaces.chat && row.surfaces.mcp))
  assert.equal(field(inventory, "Profile", "xmin").shadow, true)
})

test("a blank line in Habit leaves the CLI drift check green", () => {
  mutate("src/Orbit.Domain/Entities/Habit.cs", text => "\n" + text, (build, copy) => {
    const result = spawnSync(process.execPath, [join(copy, "tools/capability-inventory.mjs"), "--check"], { encoding: "utf8" })
    assert.equal(result.status, 0, result.stderr)
    assert.match(result.stdout, /Generated \d+ chat tools/)
    assert.deepEqual(build(), inventory)
    assert.equal(gapReport(build()), gapReport(inventory))
  }, true)
})

test("a registered chat parameter makes the CLI drift check name the changed tool", () => {
  const tool = inventory.registries.chat.find(row => row.name === "create_tag")
  mutate(tool.declaration.source.split(/[#:]/)[0], text => text.replace('name = new { type =', 'inventory_probe = new { type = JsonSchemaTypes.String },\n            name = new { type ='), (_build, copy) => {
    const result = spawnSync(process.execPath, [join(copy, "tools/capability-inventory.mjs"), "--check"], { encoding: "utf8" })
    assert.equal(result.status, 1, result.stderr)
    assert.match(result.stderr, /registries\.chat\.create_tag/)
    assert.match(result.stderr, /inventory_probe/)
  }, true)
})

test("the committed semantic baseline has no hashes or line numbers", () => {
  const text = readFileSync(join(root, "tools/capability-inventory.baseline.json"), "utf8")
  assert.doesNotMatch(text, /[a-f0-9]{64}/i)
  assert.doesNotMatch(text, /\.cs:\d+/)
  const committed = JSON.parse(text)
  assert.ok(committed.registries.chat.length)
  assert.ok(committed.registries.mcp.length)
  assert.ok(committed.entities.length)
  assert.equal(committed.provenance, undefined)
  assert.equal(committed.artifactDigest, undefined)
  assert.equal(committed.inputs, undefined)
  checkBaseline(inventory, committed)
})

test("generated output is deterministic and the accepted baseline passes", () => {
  assert.deepEqual(buildInventory(root), inventory)
  checkBaseline(inventory, baseline(inventory))
})

test("creation without update and partial readback are gaps", () => {
  const template = field(inventory, "ChecklistTemplate", "Items").surfaces.chat
  assert.equal(template.operations.create.status, "covered")
  assert.equal(template.operations.read.status, "covered")
  assert.equal(template.operations.update.status, "covered")
  mutate("src/Orbit.Api/Extensions/ServiceCollectionExtensions.AiServices.cs", text => text.replace("builder.Services.AddScoped<IAiTool, UpdateChecklistTemplateTool>();", ""), build => {
    assert.ok(field(build(), "ChecklistTemplate", "Items").surfaces.chat.problems.includes("create-without-update"))
  })
  const tags = field(inventory, "Habit", "Tags").surfaces.chat
  assert.equal(tags.operations.read.status, "partial")
  assert.equal(tags.verdict, "gap")
  assert.equal(field(inventory, "Habit", "Description").surfaces.chat.operations.read.status, "covered")
  assert.equal(field(inventory, "Habit", "StartDate").surfaces.chat.operations.update.status, "covered")
  assert.match(gapReport(inventory), /## Field coverage gaps/)
})

test("MCP gateway coverage retains direct differences and known tickets without retired memory entities", () => {
  const reminder = field(inventory, "Habit", "ReminderEnabled").surfaces.mcp.operations.create
  assert.equal(reminder.direct.length, 0)
  assert.equal(reminder.viaOperations[0].via, "execute_agent_operation_v2")
  assert.equal(field(inventory, "Habit", "Emoji").surfaces.mcp.operations.read.status, "covered")
  for (const number of [513, 246, 396, 19, 1574, 1575, 1576]) assert.ok(inventory.knownGaps.some(row => row.ticket.endsWith(`#${number}`)))
  assert.ok(!inventory.entities.some(row => row.entity === "UserFact"))
  assert.ok(!inventory.gaps.some(row => row.entity === "PushSubscription"))
  assert.ok(!inventory.gaps.some(row => row.problems.includes("direct-update-requires-unchanged-title")))
  mutate("src/Orbit.Api/Mcp/Tools/HabitTools.cs", text => text.replace('string? title = null,', 'string title,'), build => {
    assert.ok(build().gaps.some(row => row.problems.includes("direct-update-requires-unchanged-title")))
  })
  assert.ok(inventory.gaps.every(row => row.ticket), "Existing findings must reference the tickets that own them")
  for (const [entity, name] of [["Habit", "Position"], ["SubHabit", "Position"], ["HabitLog", "HabitId"], ["HabitLog", "Date"], ["Goal", "Position"], ["Goal", "Type"], ["Profile", "Email"], ["Profile", "MarketingEmailConsent"]]) {
    assert.ok(!inventory.gaps.some(row => row.entity === entity && row.field === name))
    for (const surface of ["chat", "mcp"]) {
      const value = field(inventory, entity, name).surfaces[surface]
      assert.equal(value.verdict, "deliberate")
      assert.ok(value.operations.update.reason)
    }
  }
  assert.doesNotMatch(gapReport(inventory), /Gaps to file|Requires capability review|\.cs:\d+/)
})

test("nested writable fields and filter predicates have distinct parameter paths", () => {
  const tool = inventory.registries.chat.find(row => row.name === "bulk_update_habits")
  for (const name of ["filter.all", "filter.is_general", "updates.emoji", "updates.checklist_items[].text"]) assert.ok(tool.parameters.some(row => row.name === name), name)
  const general = field(inventory, "Habit", "Schedule").surfaces.chat.operations.filterKeyedBulk
  assert.ok(general.direct.every(row => row.parameter.startsWith("updates.")), "A selector must not be counted as a write")
  mutate("src/Orbit.Application/Chat/Tools/Implementations/BulkUpdateHabitsTool.cs", text => text.replace('is_general = new { type =', 'inventory_probe = new { type ='), build => {
    assert.ok(field(build(), "Habit", "Schedule").surfaces.chat.operations.filterKeyedBulk.direct.every(row => row.parameter !== "updates.is_general"))
  })
  const mcp = inventory.registries.mcp.find(row => row.name === "bulk_update_habits")
  assert.ok(mcp.parameters.some(row => row.name === "updatesJson.emoji"))
  assert.ok(inventory.registries.chat.find(row => row.name === "bulk_create_habits").parameters.some(row => row.name === "habits[].sub_habits[].emoji"))
  assert.equal(inventory.registries.chat.find(row => row.name === "create_habit").parameters.find(row => row.name === "title").required, true)
  assert.equal(field(inventory, "Habit", "Schedule").surfaces.mcp.operations.create.status, "covered")
  assert.deepEqual(inventory.registries.mcp.flatMap(tool => tool.parameters.filter(parameter => parameter.unsupportedByExecutorSchema).map(parameter => [tool.name, parameter.name])), [])
  mutate("src/Orbit.Application/Chat/Tools/Implementations/BulkCreateHabitsTool.cs", text => text.replace('["is_general"] = new { type =', '["inventory_probe"] = new { type ='), build => {
    const changed = build()
    assert.equal(field(changed, "Habit", "Schedule").surfaces.mcp.operations.create.status, "covered")
    assert.deepEqual(changed.registries.mcp.flatMap(tool => tool.parameters.filter(parameter => parameter.unsupportedByExecutorSchema).map(parameter => [tool.name, parameter.name])), [["create_habit", "isGeneral"]])
  })
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
  mutate("src/Orbit.Application/Subscriptions/SubscriptionDtos.cs", text => text.replace("DateTime? TrialEndsAt", "DateTime? InventoryProbe"), build => {
    assert.throws(() => checkBaseline(build(), baseline(inventory)), /entities\.Subscription\.fields\.TrialEndsAt/)
  })
})

test("nullable schemas, constant enums and helper schemas retain their contracts", () => {
  const tool = inventory.registries.chat.find(row => row.name === "update_habit")
  assert.deepEqual(tool.parameters.find(row => row.name === "emoji").type, ["string", "null"])
  assert.deepEqual(tool.parameters.find(row => row.name === "frequency_unit").values, ["Day", "Week", "Month", "Year"])
  assert.ok(tool.parameters.some(row => row.name === "relative_reminders[].minutes_before"))
  assert.deepEqual(tool.parameters.find(row => row.name === "clear_fields").items.values, ["description", "emoji", "frequency_unit", "end_date", "due_time", "due_end_time"])
  assert.equal(inventory.registries.chat.find(row => row.name === "bulk_create_habits").parameters.find(row => row.name === "habits").items.type, "object")
})

test("MCP array parameters remain in the real signature contract", () => {
  const tool = inventory.registries.mcp.find(row => row.name === "update_checklist_template")
  assert.equal(tool.parameters.find(row => row.name === "items")?.type, "string[]?")
})

test("composed bulk log schemas retain filters and added dates", () => {
  const tool = inventory.registries.chat.find(row => row.name === "bulk_log_habits")
  for (const name of ["filter", "filter.all", "filter.habit_ids", "date", "dates"]) assert.ok(tool.parameters.some(row => row.name === name), name)
  mutate("src/Orbit.Application/Chat/Tools/Implementations/BulkLogHabitsTool.cs", text => text.replace('["dates"] = JsonSerializer', '["inventory_probe"] = JsonSerializer'), build => {
    assert.throws(() => checkBaseline(build(), baseline(inventory)), /registries.chat.bulk_log_habits.parameters.dates/)
  })
})

test("current source routes template updates, names, calendar selection and dedicated undo", () => {
  for (const surface of ["chat", "mcp"]) {
    assert.equal(field(inventory, "ChecklistTemplate", "Items").surfaces[surface].operations.update.status, "covered")
    assert.equal(field(inventory, "CalendarSync", "GoogleCalendarSelectedIds").surfaces[surface].operations.update.status, "covered")
    assert.equal(field(inventory, "HabitLog", "Date").surfaces[surface].operations.delete.direct[0].tool, "unlog_habit")
  }
  assert.ok(field(inventory, "Profile", "Name").surfaces.mcp.operations.update.direct.some(row => row.tool === "set_name"))
})

test("habit summary readback follows returned formatting and nested members", () => {
  for (const surface of ["chat", "mcp"]) {
    for (const name of ["Description", "DueEndTime", "ChecklistItems.Text", "ChecklistItems.IsChecked", "RelativeReminders.MinutesBefore", "RelativeReminders.When", "RelativeReminders.Time"]) {
      assert.equal(field(inventory, "Habit", name).surfaces[surface].operations.read.status, "covered", `${surface}.${name}`)
    }
  }
  mutate("src/Orbit.Application/Habits/Queries/HabitFieldSummary.cs", text => text.replace('fields.Add($"Description: {Description}")', 'fields.Add("Description omitted")'), build => {
    assert.throws(build, /Missing readback.*Description/)
  })
  mutate("src/Orbit.Application/Chat/Tools/Implementations/QueryHabitsTool.cs", text => text.replace("{labelStr}{detailSuffix}", "{labelStr}"), build => {
    assert.throws(build, /Missing readback.*detailSuffix/)
  })
})

test("partial domain declarations keep their field evidence", () => {
  assert.match(field(inventory, "Profile", "SignupLanguage")?.domain.source ?? "", /User.Signup.cs#User.SignupLanguage$/)
})

test("expression properties keep their actual member evidence", () => {
  assert.match(field(inventory, "Habit", "Tags").domain.source, /#Habit.Tags$/)
})

test("shared child schema retains its nullable contract", () => {
  const child = inventory.registries.chat.find(row => row.name === "create_sub_habit")
  assert.deepEqual(child.parameters.find(row => row.name === "frequency_unit").type, ["string", "null"])
})

test("direct JSON adapters retain nested contracts", () => {
  assert.equal(field(inventory, "Habit", "RelativeReminders.MinutesBefore").surfaces.mcp.operations.update.direct.find(row => row.tool === "update_habit")?.parameter, "relativeReminders[].minutes_before")
  mutate("src/Orbit.Api/Mcp/Tools/HabitTools.cs", text => text.replace('("relative_reminders", relativeReminders)', '("inventory_probe", relativeReminders)'), build => {
    assert.equal(field(build(), "Habit", "RelativeReminders.MinutesBefore").surfaces.mcp.operations.update.direct.length, 0)
  })
})

test("source mutations expose helper, enum, array, nullable and partial-model drift", () => {
  for (const [path, before, after, diagnostic] of [
    ["src/Orbit.Application/Chat/Tools/Implementations/BulkHabitToolArguments.cs", "minutes_before = new {", "inventory_probe = new {", /registries.chat.update_habit.parameters.relative_reminders/],
    ["src/Orbit.Application/Chat/Tools/JsonSchemaTypes.cs", '"day_before", "same_day"', '"day_before", "inventory_probe"', /registries.chat.update_habit.parameters.scheduled_reminders/],
    ["src/Orbit.Api/Mcp/Tools/ChecklistTemplateTools.cs", "string[]? items = null", "int[]? items = null", /registries.mcp.update_checklist_template.parameters.items.type/],
    ["src/Orbit.Application/Chat/Tools/Implementations/BulkCreateHabitsTool.cs", '["frequency_unit"] = new { type = new[] { JsonSchemaTypes.String, "null" }', '["frequency_unit"] = new { type = JsonSchemaTypes.String', /registries.chat.create_sub_habit.parameters.frequency_unit.type/],
    ["src/Orbit.Domain/Entities/User.Signup.cs", "public string? SignupLanguage", "public string? InventoryProbe", /entities.Profile.fields.SignupLanguage/],
  ]) mutate(path, text => text.replace(before, after), build => {
    assert.throws(() => checkBaseline(build(), baseline(inventory)), diagnostic)
  })
  mutate("src/Orbit.Application/Habits/Queries/HabitFieldSummary.cs", text => text.replace('{item.Text}', '{"omitted"}'), build => {
    assert.throws(build, /Missing readback.*item.Text/)
  })
  mutate("src/Orbit.Application/Habits/Queries/HabitFieldSummary.cs", text => text.replace('Description = habit.Description', 'Description = null'), build => {
    assert.throws(build, /Missing readback.*Description = habit.Description/)
  })
})

test("helpers inside a nested object never flatten their fields onto its parent", () => {
  const tool = inventory.registries.chat.find(row => row.name === "create_habit")
  assert.ok(tool.parameters.some(row => row.name === "sub_habits[].relative_reminders[].minutes_before"))
  assert.ok(!tool.parameters.some(row => row.name === "sub_habits[].minutes_before"))
  for (const tools of Object.values(inventory.registries)) for (const value of tools) assert.equal(new Set(value.parameters.map(row => row.name)).size, value.parameters.length, value.name)
})

test("every habit, goal, checklist and tag form field has a registered Astra path", () => {
  const missing = inventory.formFields.flatMap(form => form.fields.filter(field => field.status !== "covered").map(field => `${form.form}.${field.name}`))
  assert.deepEqual(missing, [])
})

test("adding a form field without an Astra parameter exposes the gap", () => {
  mutate("src/Orbit.Api/Controllers/HabitsControllerRequests.cs", text => text.replace("string Title,", "string Title, string UnreachableFormField,"), build => {
    const form = build().formFields.find(form => form.form === "CreateHabitRequest")
    assert.equal(form.fields.find(field => field.name === "UnreachableFormField").status, "absent")
  })
})
