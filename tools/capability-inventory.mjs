#!/usr/bin/env node
import { readdirSync, readFileSync, writeFileSync } from "node:fs"
import { dirname, join, relative, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { entities, aliases, actions, parameterRestrictions, payloads, outputAliases, rendered, deliberateFields } from "./capability-inventory.bindings.mjs"

const defaultRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const operations = ["create", "read", "update", "delete", "bulk", "filterKeyedBulk"]
const normalize = value => value.replace(/[^a-z0-9]/gi, "").toLowerCase()
const words = text => (text ?? "").split(/\s+/).filter(Boolean)
const memberLocations = new WeakMap()

function walk(directory) {
  return readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name < b.name ? -1 : 1).flatMap(entry => {
    if (["bin", "obj", "Migrations"].includes(entry.name)) return []
    const path = join(directory, entry.name)
    return entry.isDirectory() ? walk(path) : entry.name.endsWith(".cs") ? [path] : []
  })
}

function balanced(text, start, open = "{", close = "}") {
  let depth = 0
  for (let i = start; i < text.length; i++) {
    if (text[i] === '"' || text[i] === "'") i = endQuote(text, i)
    else if (text[i] === open) depth++
    else if (text[i] === close && --depth === 0) return i
  }
  throw new Error(`Unbalanced ${open} near ${text.slice(start, start + 60)}`)
}

function endQuote(text, start) {
  const quote = text[start]
  const raw = text.slice(start).match(/^"{3,}/)?.[0]
  if (raw) {
    const end = text.indexOf(raw, start + raw.length)
    if (end < 0) throw new Error("Unclosed raw C# string")
    return end + raw.length - 1
  }
  const prefix = text.slice(Math.max(0, start - 2), start)
  const verbatim = quote === '"' && prefix.includes("@")
  const interpolated = quote === '"' && prefix.includes("$")
  for (let index = start + 1; index < text.length; index++) {
    if (!verbatim && text[index] === "\\") index++
    else if (text[index] === quote) {
      if (verbatim && text[index + 1] === quote) index++
      else return index
    } else if (interpolated && text[index] === "{") {
      if (text[index + 1] === "{") index++
      else index = balanced(text, index)
    }
  }
  throw new Error("Unclosed C# string")
}

function uncomment(text) {
  let result = ""
  for (let index = 0; index < text.length; index++) {
    if (text[index] === '"' || text[index] === "'") {
      const end = endQuote(text, index)
      result += text.slice(index, end + 1)
      index = end
    } else if (text.slice(index, index + 2) === "//" || text.slice(index, index + 2) === "/*") {
      const line = text[index + 1] === "/"
      let end = text.indexOf(line ? "\n" : "*/", index + 2)
      if (end < 0) end = text.length
      else if (!line) end += 2
      result += text.slice(index, end).replace(/[^\n]/g, " ")
      index = end - 1
    } else result += text[index]
  }
  return result
}

function sourceMember(file, offset) {
  if (!memberLocations.has(file)) {
    const locations = []
    for (const type of classes(file)) {
      let index = type.start + 1
      while (index < type.end) {
        while (/\s/.test(file.text[index])) index++
        const start = index
        while (file.text[index] === "[") {
          index = balanced(file.text, index, "[", "]") + 1
          while (/\s/.test(file.text[index])) index++
        }
        const headerStart = index
        while (index < type.end && !["{", ";"].includes(file.text[index])) {
          if (file.text[index] === '"' || file.text[index] === "'") index = endQuote(file.text, index)
          else if (file.text[index] === "(") index = balanced(file.text, index, "(", ")")
          index++
        }
        const header = file.text.slice(headerStart, index)
        const name = header.match(/\b(\w+)(?:<[^>]*>)?\s*\(/)?.[1]
          ?? header.split(/=>|=/)[0].trim().match(/(\w+)$/)?.[1]
        const end = file.text[index] === "{" ? balanced(file.text, index) : index
        if (name) locations.push({ start, end, member: `${type.name}.${name}` })
        index = end + 1
      }
    }
    memberLocations.set(file, locations)
  }
  const location = memberLocations.get(file).find(item => offset >= item.start && offset <= item.end)
  if (!location) throw new Error(`Missing member evidence in ${file.path} for ${file.text.slice(offset, offset + 60)}`)
  return location.member
}

function evidence(file, offset, expression, member) {
  return { source: `${file.path}#${member ?? sourceMember(file, offset)}`, expression }
}

function classes(file) {
  return [...file.text.matchAll(/(?:public|internal|private)\s+(?:(?:abstract|sealed|partial|static)\s+)*class\s+(\w+)/g)].map(match => {
    let start = match.index + match[0].length
    if (file.text.slice(start).trimStart().startsWith("(")) {
      start = file.text.indexOf("(", start)
      start = balanced(file.text, start, "(", ")") + 1
    }
    const open = file.text.indexOf("{", start)
    const end = balanced(file.text, open)
    return { name: match[1], header: file.text.slice(match.index, open), file, start: open, end, text: file.text.slice(open, end + 1) }
  })
}

function members(text) {
  const result = []
  let start = 0
  for (let index = 0; index <= text.length; index++) {
    if (text[index] === '"' || text[index] === "'") index = endQuote(text, index)
    else if (text[index] === "{" || text[index] === "(" || text[index] === "[") {
      const close = { "{": "}", "(": ")", "[": "]" }[text[index]]
      index = balanced(text, index, text[index], close)
    } else if (text[index] === "," || index === text.length) {
      result.push({ text: text.slice(start, index), offset: start })
      start = index + 1
    }
  }
  return result
}

function parameters(text, file, offset, classMap, prefix = "", depth = 0) {
  if (depth > 8) throw new Error("Recursive schema exceeds supported depth")
  const found = []
  const properties = /\bproperties\s*=\s*new(?: Dictionary<string, object>)?\s*\{/.exec(text)
  if (!properties) return found
  const open = text.indexOf("{", properties.index)
  const close = balanced(text, open)
  const requiredText = (text.slice(close + 1).match(/required\s*=\s*new\[\]\s*\{([^}]+)\}/)?.[1] ?? "").replace(/\b(\w+)\b/g, token => {
    const value = file.text.match(new RegExp(`const string ${token} = "([^"]+)"`))?.[1]
    return value ? `"${value}"` : token
  })
  for (const part of members(text.slice(open + 1, close))) {
    const key = part.text.match(/^\s*(?:(\w+)|\[(?:"([^"]+)"|(\w+))\])\s*=/)
    if (!key) continue
    const name = key[1] ?? key[2] ?? file.text.match(new RegExp(`const string ${key[3]} = "([^"]+)"`))?.[1]
    if (!name) throw new Error(`Unresolved schema key ${part.text}`)
    const at = offset + open + 1 + part.offset + part.text.indexOf(key[0])
    const path = prefix + name
    const nested = part.text.slice(key[0].length)
    const schemaOpen = nested.indexOf("{")
    const schemaParts = schemaOpen < 0 ? [] : members(nested.slice(schemaOpen + 1, balanced(nested, schemaOpen)))
    const trait = name => schemaParts.find(item => new RegExp(`^\\s*@?${name}\\s*=`).test(item.text))?.text.split(/=(.*)/s)[1].trim()
    const type = trait("type")?.match(/^(?:JsonSchemaTypes\.(\w+)|"(\w+)")$/)?.slice(1).find(Boolean)?.toLowerCase()
    const values = trait("enum")?.match(/new\[\]\s*\{([^}]+)\}/)?.[1]
    found.push({ name: path, leaf: name, required: requiredText.includes(`"${name}"`), ...(type ? { type } : {}), ...(values ? { values: members(values).map(item => JSON.parse(item.text.trim())) } : {}), ...evidence(file, at, name) })
    const nestedOffset = offset + open + 1 + part.offset + key[0].length
    const suffix = type === "array" ? "[]." : "."
    found.push(...parameters(nested, file, nestedOffset, classMap, path + suffix, depth + 1))
    const helper = nested.match(/(?:(\w+)\.)?(FilterSchema|HabitItemSchema)\(/)
    if (helper) {
      const owner = helper[1] ? classMap.get(helper[1]) : [...classMap.values()].find(type => type.file === file && type.text.includes(` ${helper[2]}(`))
      if (!owner) throw new Error(`Missing schema helper ${helper[0]}`)
      const method = owner.text.search(new RegExp(`(?:public|private|internal) static object ${helper[2]}\\(`))
      if (method < 0) throw new Error(`Missing schema helper declaration ${helper[0]}`)
      const helperOpen = owner.text.indexOf("{", method)
      found.push(...parameters(owner.text.slice(helperOpen, balanced(owner.text, helperOpen) + 1), owner.file, owner.start + helperOpen, classMap, path + suffix, depth + 1))
    }
  }
  if (text.includes('properties["sub_habits"]')) {
    const at = text.indexOf('properties["sub_habits"]')
    const children = found.map(item => ({ ...item, name: prefix + "sub_habits[]." + item.name.slice(prefix.length) }))
    found.push({ name: prefix + "sub_habits", leaf: "sub_habits", required: false, ...evidence(file, offset + at, 'properties["sub_habits"]') }, ...children)
  }
  return found
}

function schemaParameters(type, classMap) {
  let owner = type
  if (!owner.text.includes("GetParameterSchema()")) {
    const parent = owner.header.match(/:\s*(\w+)/)?.[1]
    owner = classMap.get(parent)
    if (!owner) throw new Error(`Missing schema for ${type.name}`)
  }
  const method = owner.text.indexOf("GetParameterSchema()")
  const after = method + "GetParameterSchema()".length
  if (owner.text.slice(after).trimStart().startsWith("{")) {
    const open = owner.text.indexOf("{", after)
    return parameters(owner.text.slice(open, balanced(owner.text, open) + 1), owner.file, owner.start + open, classMap)
  }
  const arrow = owner.text.indexOf("=>", method)
  const expression = owner.text.slice(arrow + 2).trimStart()
  if (!expression.startsWith("new")) {
    const call = expression.match(/^(\w+)\.(\w+)\(/)
    if (!call) throw new Error(`Unsupported schema expression ${type.name}`)
    const helper = classMap.get(call[1])
    if (!helper) throw new Error(`Missing helper ${call[1]}`)
    const methodStart = helper.text.indexOf(`${call[2]}(`)
    const bodyStart = helper.text.indexOf("{", methodStart)
    return parameters(helper.text.slice(bodyStart, balanced(helper.text, bodyStart) + 1), helper.file, helper.start + bodyStart, classMap)
  }
  const open = owner.text.indexOf("{", arrow)
  const end = balanced(owner.text, open)
  return parameters(owner.text.slice(open, end + 1), owner.file, owner.start + open, classMap)
}

function registries(files, classMap) {
  const chat = []
  const mcp = []
  const resolveName = expression => {
    if (expression.startsWith('"')) return JSON.parse(expression)
    const [type, member] = expression.split(".")
    const source = [...files.values()].find(file => new RegExp(`class ${type}\\b`).test(file.text))
    const value = source?.text.match(new RegExp(`const string ${member} = "([^"]+)"`))?.[1]
    if (!value) throw new Error(`Unresolved tool name ${expression}`)
    return value
  }
  for (const file of files.values()) {
    if (!file.path.startsWith("src/Orbit.Api/Extensions/")) continue
    const registrations = /AddScoped<IAiTool,\s*(\w+)>|AddScoped<IAiTool>\(sp => sp\.GetRequiredService<(\w+)>/g
    for (const match of file.text.matchAll(registrations)) {
      const type = classMap.get(match[1] ?? match[2])
      if (!type) throw new Error(`Missing registered chat type ${match[0]}`)
      const nameMatch = type.text.match(/string Name\s*=>\s*("[^"]+"|\w+\.\w+)/)
      if (!nameMatch) throw new Error(`Missing name for ${type.name}`)
      chat.push({ name: resolveName(nameMatch[1]), surface: "chat", registration: evidence(file, match.index, match[0]), declaration: evidence(type.file, type.start + nameMatch.index, nameMatch[0]), parameters: schemaParameters(type, classMap), type })
    }
    for (const match of file.text.matchAll(/\.WithTools<(\w+)>\(\)/g)) {
      const type = classMap.get(match[1])
      if (!type) throw new Error(`Missing registered MCP type ${match[1]}`)
      const attributes = [...type.text.matchAll(/\[McpServerTool\(Name\s*=\s*("[^"]+"|\w+\.\w+)\)/g)]
      for (let index = 0; index < attributes.length; index++) {
        const attribute = attributes[index]
        const boundary = attributes[index + 1]?.index ?? type.text.length
        const text = type.text.slice(attribute.index, boundary)
        const method = text.match(/public\s+(?:async\s+)?[\w<>?,]+\s+\w+\s*\(/)
        if (!method) throw new Error(`Missing MCP method ${attribute[0]}`)
        const open = text.indexOf("(", method.index)
        const close = balanced(text, open, "(", ")")
        const signature = text.slice(open + 1, close)
        const params = [...signature.matchAll(/\[Description\((?:"(?:\\.|[^"\\])*"|\w+)\)\]\s*([\w?<>]+)\s+(\w+)(\s*=\s*[^,\n]+)?/g)].map(param => ({ name: param[2], type: param[1], required: !param[3], ...(param[3] ? { default: param[3].replace(/^\s*=\s*/, "").trim() } : {}), ...evidence(type.file, type.start + attribute.index + open + 1 + param.index, param[0]) }))
        mcp.push({ name: resolveName(attribute[1]), surface: "mcp", registration: evidence(file, match.index, match[0]), declaration: evidence(type.file, type.start + attribute.index, attribute[0]), parameters: params, type: { ...type, start: type.start + attribute.index, text } })
      }
    }
  }
  for (const surface of [chat, mcp]) {
    surface.sort((a, b) => a.name < b.name ? -1 : 1)
    if (new Set(surface.map(tool => tool.name)).size !== surface.length) throw new Error("Duplicate registered tool")
  }
  if (!chat.length || !mcp.length) throw new Error("Both registries must be present")
  for (const tool of mcp) {
    const backing = chat.find(candidate => candidate.name === tool.name)
    if (!backing) continue
    for (const parameter of tool.parameters) {
      const forwarded = tool.type.text.match(new RegExp(`(?:^|\\n)\\s*(\\w+)\\s*=\\s*${parameter.name}\\s*(?:,|\\n|})`))?.[1]
      if (forwarded && tool.type.text.includes(`"${tool.name}"`) && !backing.parameters.some(item => normalize(item.name) === normalize(forwarded))) {
        parameter.unsupportedByExecutorSchema = true
        parameter.forwardedAs = forwarded
        parameter.executorSchema = backing.declaration
        const validation = files.get("src/Orbit.Application/Chat/Tools/ChatToolArgumentCheck.cs")
        const check = validation.text.indexOf("!AgentArgumentSchema.Accepts")
        if (check < 0) throw new Error("Review executor schema enforcement")
        parameter.executorValidation = evidence(validation, check, "!AgentArgumentSchema.Accepts")
      }
    }
    for (const parameter of [...tool.parameters]) {
      if (!["filterJson", "updatesJson"].includes(parameter.name)) continue
      const object = parameter.name.replace("Json", "")
      if (!tool.type.file.text.includes(`JsonSerializer.Deserialize<JsonElement>(${parameter.name})`)) throw new Error(`Review JSON adapter ${tool.name}.${parameter.name}`)
      tool.parameters.push(...backing.parameters.filter(item => item.name.startsWith(`${object}.`)).map(item => ({ ...item, name: item.name.replace(object, parameter.name), adapter: parameter })))
    }
    if (tool.name === "bulk_create_habits") {
      const helper = files.get("src/Orbit.Api/Mcp/Tools/McpToolHelpers.cs")
      const start = helper.text.indexOf("ToBulkHabitArgs(")
      const open = helper.text.indexOf("{", start)
      const end = balanced(helper.text, open)
      for (const member of helper.text.slice(open, end).matchAll(/(\w+)\s*=\s*dto\.(\w+)/g)) tool.parameters.push({ name: `habitsJson[].${member[2][0].toLowerCase()}${member[2].slice(1)}`, leaf: member[1], ...evidence(helper, open + member.index, member[0]), adapter: tool.declaration })
    }
  }
  return { chat, mcp }
}

function recordFields(files, name) {
  for (const file of files.values()) {
    const match = new RegExp(`\\brecord ${name}\\s*\\(`).exec(file.text)
    if (!match) continue
    const open = file.text.indexOf("(", match.index)
    const close = balanced(file.text, open, "(", ")")
    return [...file.text.slice(open + 1, close).matchAll(/[\w<>?,.\[\]]+\s+(\w+)\s*(?:=[^,]*|)(?=,|$)/g)].map(field => ({ name: field[1], ...evidence(file, open + 1 + field.index, field[0].trim(), `${name}.${field[1]}`) }))
  }
  throw new Error(`Missing returned record ${name}`)
}

function readField(tool, field, files) {
  const key = `${tool.surface}:${tool.name}`
  const projections = rendered[key]
  if (projections && [...words(projections.fields), ...words(projections.partial)].includes(field)) {
    const source = projections.file ? files.get(projections.file) : tool.type.file
    const text = projections.file ? source.text : tool.type.text
    const start = projections.start ? text.indexOf(projections.start) : 0
    if (start === -1) throw new Error(`Missing projection start for ${key}`)
    const member = projections.names?.[field] ?? field
    const expression = `${projections.receiver}.${member}`
    const index = text.indexOf(expression, start)
    if (index === -1) {
      if (words(projections.partial).includes(field)) return null
      throw new Error(`Missing rendered field ${key} ${expression}; review the binding`)
    }
    return { tool: tool.name, status: words(projections.partial).includes(field) ? "partial" : "covered", ...evidence(source, (projections.file ? 0 : tool.type.start) + index, expression) }
  }
  const records = payloads[key] ?? []
  for (const record of records) {
    const names = [field, ...(outputAliases[field] ?? [])]
    const match = recordFields(files, record).find(item => names.includes(item.name))
    if (match) return { tool: tool.name, status: "covered", ...match, payload: record, returnedBy: tool.declaration }
  }
  return null
}

function modelFields(model, classMap, context) {
  const type = classMap.get(model)
  if (!type) throw new Error(`Missing domain entity ${model}`)
  const entityMatch = new RegExp(`modelBuilder\\.Entity<${model}>\\(entity =>`).exec(context.text)
  if (!entityMatch) throw new Error(`Missing EF configuration for ${model}`)
  const configOpen = context.text.indexOf("{", entityMatch.index)
  const configEnd = balanced(context.text, configOpen)
  const config = context.text.slice(configOpen, configEnd + 1)
  const source = type.text
  const pattern = /public\s+([\w<>?,.\[\]]+)\s+(\w+)\s*(?:\{\s*get\s*;|=>\s*_\w+\.AsReadOnly\(\))/g
  const fields = [...source.matchAll(pattern)].map(match => {
    const configured = [...config.matchAll(new RegExp(`\\b\\w+\\.${match[2]}\\b`, "g"))].map(item => evidence(context, configOpen + item.index, item[0]))
    return { name: match[2], type: match[1], domain: evidence(type.file, type.start + match.index, match[0]), persistence: configured.length ? configured : [evidence(context, entityMatch.index, `Entity<${model}>: EF convention mapping`)] }
  })
  const base = classMap.get("Entity")
  const id = base.text.match(/public Guid Id\b/)
  fields.unshift({ name: "Id", type: "Guid", domain: evidence(base.file, base.start + id.index, id[0]), persistence: [evidence(context, entityMatch.index, `Entity<${model}>: inherited key`)] })
  const concurrency = context.text.indexOf(`MapXminToken(modelBuilder.Entity<${model}>())`)
  if (concurrency >= 0) {
    const mapping = context.text.indexOf('entity.Property<uint>("xmin")')
    fields.push({ name: "xmin", type: "uint", shadow: true, domain: evidence(context, concurrency, "Shadow concurrency token, no domain property"), persistence: [evidence(context, mapping, 'entity.Property<uint>("xmin").IsRowVersion()')] })
  }
  return fields
}

function capabilityRows(files, registry, gatewayTools, gateway) {
  const file = files.get("src/Orbit.Infrastructure/Services/AgentCatalogService.Capabilities.cs")
  const resolveNames = (text, argument) => {
    const list = text.match(new RegExp(`${argument}:\\s*\\[([^\\]]*)\\]`))?.[1] ?? ""
    return members(list).filter(item => item.text.trim()).map(item => {
      const value = item.text.trim()
      if (value.startsWith('"')) return JSON.parse(value)
      const [typeName, member] = value.split(".")
      const source = [...files.values()].find(candidate => new RegExp(`class ${typeName}\\b`).test(candidate.text))
      const resolved = source?.text.match(new RegExp(`const string ${member} = "([^"]+)"`))?.[1]
      if (!resolved) throw new Error(`Unresolved capability tool ${value}`)
      return resolved
    })
  }
  return [...file.text.matchAll(/new AgentCapability\(/g)].map(match => {
    const open = file.text.indexOf("(", match.index)
    const text = file.text.slice(open + 1, balanced(file.text, open, "(", ")"))
    const id = members(text)[0].text.trim()
    const chat = resolveNames(text, "ChatToolNames")
    const mcp = resolveNames(text, "McpToolNames")
    return { capability: id, evidence: evidence(file, match.index, id), surfaces: Object.fromEntries(["chat", "mcp"].map(surface => {
      const names = surface === "chat" ? chat : mcp
      const direct = registry[surface].filter(tool => names.includes(tool.name)).map(tool => ({ name: tool.name, declaration: tool.declaration, registration: tool.registration }))
      const via = surface === "mcp" ? gatewayTools.filter(tool => chat.includes(tool.name)).map(tool => ({ name: tool.name, via: gateway.name, declaration: tool.declaration, gateway: gateway.declaration })) : []
      return [surface, { verdict: direct.length || via.length ? "covered" : "absent", direct, viaOperations: via }]
    })) }
  })
}

function routeEvidence(tool, gateway) {
  return { ...tool, via: "execute_agent_operation_v2", operationId: tool.name, gateway: gateway.declaration }
}

export function buildInventory(root = defaultRoot) {
  const files = new Map(walk(join(root, "src")).map(path => {
    const name = relative(root, path).split("\\").join("/")
    return [name, { path: name, text: uncomment(readFileSync(path, "utf8").replace(/\r\n?/g, "\n")) }]
  }))
  const classMap = new Map()
  for (const file of files.values()) for (const type of classes(file)) {
    if (!classMap.has(type.name) || type.file.path.endsWith(`/${type.name}.cs`) || type.file.path.includes("Chat/Tools/Implementations")) classMap.set(type.name, type)
  }
  const registry = registries(files, classMap)
  const context = files.get("src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs")
  const gateway = registry.mcp.find(tool => tool.name === "execute_agent_operation_v2")
  const catalog = files.get("src/Orbit.Infrastructure/Services/AgentCatalogService.Operations.cs")
  if (!gateway || !catalog.text.includes("tool is not IChatOnlyAiTool")) throw new Error("Review the MCP operation gateway binding")
  const gatewayTools = registry.chat.filter(tool => !tool.type.header.includes("IChatOnlyAiTool"))
  const registeredChat = registry.chat.length
  registry.chat = registry.chat.filter(tool => !tool.type.header.includes("IMcpOnlyAiTool"))
  const capabilities = capabilityRows(files, registry, gatewayTools, gateway)
  const absenceEvidence = Object.fromEntries(["chat", "mcp"].map(surface => [surface, registry[surface].map(tool => tool.registration).filter((item, index, all) => all.findIndex(other => other.source === item.source) === index)]))
  const rows = []
  const gaps = []
  for (const [entity, binding] of Object.entries(entities)) {
    const fields = modelFields(binding.model, classMap, context).filter(field => !binding.select || binding.select.test(field.name))
    for (const field of [...fields]) {
      const nested = field.type.match(/^IReadOnlyList<(ChecklistItem|ScheduledReminderTime|RelativeReminderTime)>$/)?.[1]
      if (nested) for (const member of recordFields(files, nested)) fields.push({ name: `${field.name}.${member.name}`, type: nested, domain: member, persistence: field.persistence })
    }
    const editable = new Map(binding.editableRecord ? recordFields(files, binding.editableRecord).map(member => [member.name, member]) : [])
    for (const [field, method] of Object.entries(binding.editableMethods ?? {})) {
      const type = classMap.get(binding.model)
      const match = new RegExp(`public [\\w<>]+ ${method}\\(`).exec(type.text)
      if (!match) throw new Error(`Missing editable domain method ${binding.model}.${method}`)
      const open = type.text.indexOf("{", match.index)
      const body = type.text.slice(open, balanced(type.text, open))
      if (!new RegExp(`\\b${field}\\s*=`).test(body)) throw new Error(`Domain method ${method} no longer writes ${field}`)
      editable.set(field, evidence(type.file, type.start + match.index, `${method} writes ${field}`))
    }
    const surfaceOperations = {}
    const fieldRows = fields.map(field => ({ ...field, surfaces: {} }))
    for (const surface of ["chat", "mcp"]) {
      const selects = (tool, operation) => (binding.tools[operation] ?? []).includes(tool.name) && (operation !== "filterKeyedBulk" || tool.parameters.some(parameter => ["filter", "filterJson"].includes(parameter.name)))
      const relevant = operation => registry[surface].filter(tool => selects(tool, operation))
      const via = operation => surface === "mcp" ? gatewayTools.filter(tool => selects(tool, operation)) : []
      surfaceOperations[surface] = Object.fromEntries(operations.map(operation => [operation, {
        status: relevant(operation).length || via(operation).length ? "covered" : binding.deliberate ? "deliberate" : "absent",
        tools: relevant(operation).map(tool => ({ name: tool.name, declaration: tool.declaration, registration: tool.registration, parameters: tool.parameters, status: tool.parameters.some(parameter => parameter.unsupportedByExecutorSchema) ? "advertised-only" : "covered", ...(actions[`${entity}:${operation}`] ? { action: actions[`${entity}:${operation}`] } : {}) })),
        viaOperations: via(operation).map(tool => ({ name: tool.name, via: gateway.name, declaration: tool.declaration, gateway: gateway.declaration, parameters: tool.parameters })),
        absenceEvidence: relevant(operation).length ? [] : absenceEvidence[surface],
      }]))
      for (const field of fieldRows) {
        const cells = {}
        for (const operation of operations) {
          const find = tool => {
            if (operation === "read") {
              const result = readField(tool, field.name.split(".")[0], files)
              return result && field.name.includes(".") ? { ...result, status: "partial" } : result
            }
            if (operation === "delete") return { tool: tool.name, status: "entity-operation", ...tool.declaration }
            const allowedTools = parameterRestrictions[field.name]
            if (allowedTools && !allowedTools.includes(tool.name)) return null
            const [parent, member] = field.name.split(".")
            const names = member ? [member] : binding.aliases?.[field.name] ?? aliases[field.name] ?? [field.name]
            const parameter = tool.parameters.find(param => !/^(filter|filterJson)\./.test(param.name) && !param.name.includes("sub_habits") && (!member || normalize(param.name).includes(normalize(parent))) && names.some(name => normalize(name) === normalize(param.leaf ?? param.name)))
            return parameter ? { tool: tool.name, status: tool.parameters.some(item => item.unsupportedByExecutorSchema) ? "advertised-only" : "covered", parameter: parameter.name, ...parameter } : null
          }
          const direct = relevant(operation).map(find).filter(Boolean)
          const indirect = via(operation).map(tool => {
            const result = find(tool)
            return result ? routeEvidence({ ...result, name: tool.name }, gateway) : null
          }).filter(Boolean)
          const hasFull = [...direct, ...indirect].some(item => ["covered", "entity-operation"].includes(item.status))
          const reason = deliberateFields[`${entity}.${field.name}`] ?? binding.deliberate
          const advertisedOnly = [...direct, ...indirect].some(item => item.status === "advertised-only")
          cells[operation] = { status: hasFull ? "covered" : advertisedOnly ? "advertised-only" : direct.length || indirect.length ? "partial" : reason ? "deliberate" : "absent", direct, viaOperations: indirect, ...(reason ? { reason } : {}), evidence: field.domain }
        }
        const canWrite = ["create", "update", "bulk", "filterKeyedBulk"].some(operation => cells[operation].status === "covered")
        const problems = []
        if (canWrite && cells.read.status !== "covered" && !deliberateFields[`${entity}.${field.name}`]) problems.push("write-without-full-readback")
        const hasUpdate = cells.update.status === "covered" || [...cells.bulk.direct, ...cells.bulk.viaOperations, ...cells.filterKeyedBulk.direct, ...cells.filterKeyedBulk.viaOperations].some(item => /update|reschedule/.test(item.tool))
        if (cells.create.status === "covered" && !hasUpdate && !deliberateFields[`${entity}.${field.name}`] && !binding.deliberate) problems.push("create-without-update")
        const editEvidence = editable.get(field.name.split(".")[0])
        if (editEvidence && !hasUpdate && !problems.includes("create-without-update") && !deliberateFields[`${entity}.${field.name}`]) problems.push("domain-edit-without-tool-update")
        if (editEvidence && !canWrite && cells.read.status !== "covered" && !deliberateFields[`${entity}.${field.name}`]) problems.push("editable-field-without-readback")
        if (binding.deliberate) problems.length = 0
        field.surfaces[surface] = { verdict: problems.length ? "gap" : deliberateFields[`${entity}.${field.name}`] || binding.deliberate ? "deliberate" : "inventoried", problems, operations: cells }
        if (problems.length) gaps.push({ entity, field: field.name, surface, problems, domain: field.domain, ...(editEvidence ? { editEvidence } : {}), operations: cells })
      }
    }
    rows.push({ entity, model: binding.model, ...(binding.deliberate ? { deliberate: binding.deliberate } : {}), operations: surfaceOperations, fields: fieldRows })
  }
  const publicTool = ({ type, ...tool }) => tool
  const toolEvidence = names => [...registry.chat, ...registry.mcp].filter(tool => names.includes(tool.name)).map(tool => tool.declaration)
  const knownGaps = [
    { ticket: "thomasluizon/orbit-tickets#513", observation: "Emoji is accepted on MCP create/update and returned by get_habit/list_habits; inspect the Habit.Emoji cells and filter-keyed bulk cells.", evidence: toolEvidence(["create_habit", "update_habit", "get_habit", "list_habits"]) },
    { ticket: "thomasluizon/orbit-tickets#246", observation: "No dedicated unlog_habit registration. log_habit toggles a completion off; HabitLog.delete records that route rather than declaring undo absent.", evidence: [...toolEvidence(["log_habit"]), ...absenceEvidence.chat, ...absenceEvidence.mcp] },
    { ticket: "thomasluizon/orbit-tickets#396", observation: "set_proactive_astra is registered on both surfaces; readback is checked separately in Profile.ProactiveAstraEnabled.", evidence: toolEvidence(["set_proactive_astra", "get_profile"]) },
    { ticket: "thomasluizon/orbit-tickets#19", observation: "Filter-keyed bulk update/reschedule/delete/log/skip routes exist. MCP log reads include partial and returned counts. Inspect parameter paths to distinguish predicates from explicit IDs.", evidence: toolEvidence(["bulk_update_habits", "bulk_reschedule_habits", "bulk_delete_habits", "bulk_log_habits", "bulk_skip_habits", "get_habit_logs", "get_all_habit_logs"]) },
    { ticket: "thomasluizon/orbit-tickets#235", observation: entities.UserFact.deliberate, evidence: [...absenceEvidence.chat, ...absenceEvidence.mcp] },
    { ticket: "thomasluizon/orbit-tickets#1574", observation: "update_habit constructs HabitUpdateParams without DueEndTime, while Habit.Update assigns it unconditionally; an unrelated update clears the stored deadline.", evidence: [evidence(classMap.get("UpdateHabitTool").file, classMap.get("UpdateHabitTool").file.text.indexOf("private static HabitUpdateParams ResolveUpdateParams"), "ResolveUpdateParams omits DueEndTime"), evidence(classMap.get("Habit").file, classMap.get("Habit").file.text.indexOf("DueEndTime = p.DueEndTime;"), "DueEndTime = p.DueEndTime")] },
    { ticket: "thomasluizon/orbit-tickets#1575", observation: "Habit and sub-habit field readback and update gaps, plus the required MCP update title and unsupported create_habit isGeneral adapter parameter.", evidence: toolEvidence(["query_habits", "create_habit", "create_sub_habit", "update_habit", "bulk_update_habits", "get_habit", "list_habits"]) },
    { ticket: "thomasluizon/orbit-tickets#1576", observation: "Checklist template updates, profile name updates and calendar selection updates/readback have no complete tool route.", evidence: toolEvidence(["get_checklist_templates", "create_checklist_template", "get_profile", "update_profile_preferences", "manage_calendar_sync", "get_calendar_overview"]) },
  ]
  const requiredTitle = registry.mcp.find(tool => tool.name === "update_habit")?.parameters.find(param => param.name === "title" && param.required)
  if (requiredTitle) gaps.push({ entity: "Habit", surface: "mcp", field: "Title", problems: ["direct-update-requires-unchanged-title"], evidence: requiredTitle })
  for (const tool of registry.mcp) for (const parameter of tool.parameters.filter(item => item.unsupportedByExecutorSchema)) {
    gaps.push({ entity: Object.keys(entities).find(entity => Object.values(entities[entity].tools).flat().includes(tool.name)) ?? "Adapter", surface: "mcp", field: parameter.name, tool: tool.name, problems: ["adapter-forwards-unsupported-parameter"], evidence: parameter })
  }
  for (const gap of gaps) {
    if (["Habit", "SubHabit"].includes(gap.entity) || gap.entity === "Goal" && gap.field === "Habits") gap.ticket = "thomasluizon/orbit-tickets#1575"
    else if (gap.entity === "ChecklistTemplate" || gap.entity === "Profile" && gap.field === "Name" || gap.entity === "CalendarSync" && gap.field === "GoogleCalendarSelectedIds") gap.ticket = "thomasluizon/orbit-tickets#1576"
  }
  return {
    schemaVersion: 2,
    scope: "Extension of #318: registered chat and MCP tools, persisted domain fields, direct output projections and MCP operation-gateway routes. No user data is read.",
    semantics: { covered: "A registered tool accepts this parameter or returns the field. Policy, plan, ownership and confirmation checks still apply.", partial: "A summary or count is returned, not the full writable value. This does not satisfy readback.", "advertised-only": "A direct adapter forwards a parameter absent from its backing executor schema. Its signature alone does not establish working coverage.", absent: "No matching registered tool parameter or returned projection in the reviewed bindings. Domain and complete registry evidence accompany the cell.", deliberate: "An explicit reason explains why this operation is not exposed.", delete: "Deletion applies to the entity, not an independent field mutation.", bulk: "Includes repeated explicit-ID operations and filter-keyed operations; filterKeyedBulk is reported separately.", gateway: "MCP can execute non-chat-only IAiTool operations through execute_agent_operation_v2. Direct MCP limitations remain visible in direct arrays.", bindings: "Semantic bindings identify returned DTOs and formatted output members. CI compares capability semantics; evidence identifies source paths and members without pinning source contents or line numbers." },
    registries: { chat: registry.chat.map(publicTool), mcp: registry.mcp.map(publicTool) },
    counts: { registeredChat, chat: registry.chat.length, mcp: registry.mcp.length, capabilities: capabilities.length, entities: rows.length, fields: rows.reduce((sum, row) => sum + row.fields.length, 0) },
    capabilities, entities: rows, knownGaps, gaps,
  }
}

const sorted = (rows, key = row => row.name) => [...rows].sort((a, b) => key(a).localeCompare(key(b), "en"))
const gapKey = gap => `${gap.entity}.${gap.field}.${gap.surface}.${gap.problems.join("+")}`

export function semanticSnapshot(inventory) {
  const parameters = rows => sorted(rows).map(({ name, type, required, values, default: defaultValue, unsupportedByExecutorSchema, forwardedAs }) => ({ name, ...(type ? { type } : {}), ...(required !== undefined ? { required } : {}), ...(values ? { values: [...values].sort() } : {}), ...(defaultValue !== undefined ? { default: defaultValue } : {}), ...(unsupportedByExecutorSchema ? { unsupportedByExecutorSchema, forwardedAs } : {}) }))
  const route = row => ({ tool: row.tool ?? row.name, ...(row.parameter ? { parameter: row.parameter } : {}), ...(row.status ? { status: row.status } : {}), ...(row.via ? { via: row.via } : {}), ...(row.action ? { action: row.action } : {}) })
  const routes = rows => sorted(rows.map(route), row => JSON.stringify(row))
  const operation = cell => ({ status: cell.status, ...(cell.reason ? { reason: cell.reason } : {}), ...(cell.direct?.length || cell.tools?.length ? { direct: routes(cell.direct ?? cell.tools) } : {}), ...(cell.viaOperations.length ? { viaOperations: routes(cell.viaOperations) } : {}) })
  const surfaces = cells => Object.fromEntries(Object.entries(cells).map(([surface, cell]) => [surface, {
    ...(cell.verdict ? { verdict: cell.verdict, problems: [...cell.problems].sort() } : {}),
    operations: Object.fromEntries(operations.map(name => [name, operation(cell.operations?.[name] ?? cell[name])])),
  }]))
  return {
    schemaVersion: inventory.schemaVersion,
    registries: Object.fromEntries(Object.entries(inventory.registries).map(([surface, tools]) => [surface, sorted(tools).map(tool => ({ name: tool.name, parameters: parameters(tool.parameters) }))])),
    capabilities: sorted(inventory.capabilities, row => row.capability).map(row => ({ capability: row.capability, surfaces: Object.fromEntries(Object.entries(row.surfaces).map(([surface, cell]) => [surface, { verdict: cell.verdict, direct: routes(cell.direct), viaOperations: routes(cell.viaOperations) }])) })),
    entities: sorted(inventory.entities, row => row.entity).map(row => ({ entity: row.entity, model: row.model, ...(row.deliberate ? { deliberate: row.deliberate } : {}), surfaces: surfaces(row.operations), fields: sorted(row.fields).map(field => ({ name: field.name, type: field.type, ...(field.shadow ? { shadow: true } : {}), surfaces: surfaces(field.surfaces) })) })),
    knownGaps: sorted(inventory.knownGaps, row => row.ticket).map(({ ticket, observation }) => ({ ticket, observation })),
    gaps: sorted(inventory.gaps, gapKey).map(({ entity, field, surface, problems, ticket, tool }) => ({ entity, field, surface, problems: [...problems].sort(), ...(tool ? { tool } : {}), ...(ticket ? { ticket } : {}) })),
  }
}

function differences(actual, expected, path = "") {
  if (Object.is(actual, expected)) return []
  if (Array.isArray(actual) && Array.isArray(expected)) {
    const key = row => row && typeof row === "object" ? row.entity && row.field ? gapKey(row) : row.name ?? row.entity ?? row.capability ?? row.ticket ?? JSON.stringify(row) : String(row)
    const keyed = rows => Object.fromEntries(rows.map(row => [key(row), row]))
    return differences(keyed(actual), keyed(expected), path)
  }
  if (actual && expected && typeof actual === "object" && typeof expected === "object" && !Array.isArray(actual) && !Array.isArray(expected)) {
    return [...new Set([...Object.keys(actual), ...Object.keys(expected)])].sort().flatMap(key => differences(actual[key], expected[key], path ? `${path}.${key}` : key))
  }
  return [`${path}: ${expected === undefined ? "added" : actual === undefined ? "removed" : "changed"}`]
}

export function checkBaseline(inventory, baseline) {
  const changed = differences(semanticSnapshot(inventory), baseline)
  if (changed.length) throw new Error(`Capability inventory review required for:\n${changed.join("\n")}\nReview generated gaps and bindings, then run node tools/capability-inventory.mjs --accept.`)
}

export function gapReport(inventory) {
  const grouped = new Map()
  for (const gap of inventory.gaps) {
    const key = `${gap.entity}.${gap.field}:${gap.problems.includes("direct-update-requires-unchanged-title") ? "required-title" : "roundtrip"}`
    if (!grouped.has(key)) grouped.set(key, [])
    grouped.get(key).push(gap)
  }
  const lines = ["# Capability inventory findings", "", "Generated by `node tools/capability-inventory.mjs --accept` from the reviewed API tree. Verified gaps link to their owning tickets. No account data is read.", "", "## Known gaps", ""]
  for (const gap of inventory.knownGaps) lines.push(`- ${gap.ticket}: ${gap.observation}`, `  Evidence: ${gap.evidence.map(item => `\`${item.source}\``).join(", ")}.`)
  lines.push("", "## Field coverage gaps", "")
  for (const group of grouped.values()) {
    const first = group[0]
    const requiredTitle = first.problems.includes("direct-update-requires-unchanged-title")
    const unsupported = first.problems.includes("adapter-forwards-unsupported-parameter")
    const problems = new Set(group.flatMap(gap => gap.problems))
    lines.push(`### ${first.entity}.${first.field}: ${requiredTitle ? "direct MCP update requires the current title" : [...problems].join(", ")}`, "")
    lines.push(`Surfaces: ${group.map(gap => gap.surface).join(", ")}.`)
    const tickets = [...new Set(group.map(gap => gap.ticket).filter(Boolean))]
    lines.push(`Tracking: ${tickets.length ? tickets.join(", ") : "Requires capability review."}`)
    if (unsupported) {
      lines.push(`Evidence on the API source tree: \`${first.evidence.source}\`, \`${first.evidence.executorSchema.source}\`, \`${first.evidence.executorValidation.source}\`.`)
      lines.push(`User-visible effect: ${first.tool} forwards ${first.evidence.forwardedAs}, which its backing tool schema does not accept. The direct signature advertises a value that cannot be used successfully.`)
      lines.push("Fix: Align the direct adapter and executor schema while preserving shipped contracts and validating the intended field.", "")
      continue
    }
    const sources = new Set(group.flatMap(gap => gap.evidence ? [gap.evidence.source] : [gap.domain.source, ...(gap.editEvidence ? [gap.editEvidence.source] : []), ...["create", "update", "bulk", "filterKeyedBulk"].flatMap(operation => [...gap.operations[operation].direct, ...gap.operations[operation].viaOperations].map(item => item.source))]))
    const readers = inventory.entities.find(entity => entity.entity === first.entity).operations
    for (const gap of group) for (const tool of readers[gap.surface].read.tools) sources.add(tool.declaration.source)
    lines.push(`Evidence on the API source tree: ${[...sources].map(source => `\`${source}\``).join(", ")}.`)
    lines.push(`User-visible effect: ${requiredTitle ? "Changing an unrelated habit property through the direct MCP tool requires retrieving and resending its decrypted title. The generic operation route accepts the chat schema." : `${problems.has("domain-edit-without-tool-update") ? "The domain supports editing" : "Astra can write"} ${first.entity}.${first.field}${problems.has("domain-edit-without-tool-update") ? " but Astra has no update parameter" : ""}${problems.has("write-without-full-readback") || problems.has("editable-field-without-readback") ? "; Astra cannot read its full value back" : ""}${problems.has("create-without-update") ? "; there is no parameter to change it after creation" : ""}.`}`)
    lines.push(`Fix: ${requiredTitle ? "Make title optional on the direct MCP adapter and preserve the existing value when omitted." : `${problems.has("write-without-full-readback") || problems.has("editable-field-without-readback") ? "Return the full field through the relevant read projections. " : ""}${problems.has("create-without-update") || problems.has("domain-edit-without-tool-update") ? "Add an update parameter with application validation and domain guards, or record an explicit immutable-field rationale." : ""}`}`, "")
  }
  lines.push("## Deliberate omissions", "")
  for (const [field, reason] of Object.entries(deliberateFields)) lines.push(`- ${field}: ${reason}`)
  return lines.map(line => line.trimEnd()).join("\n")
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2)
    if (args.some(arg => !["--check", "--accept"].includes(arg)) || args.length > 1) throw new Error("usage: node tools/capability-inventory.mjs [--check | --accept]")
    const inventory = buildInventory()
    writeFileSync(join(defaultRoot, "capability-inventory.json"), JSON.stringify(inventory, null, 2) + "\n")
    const baselinePath = join(defaultRoot, "tools/capability-inventory.baseline.json")
    const reportPath = join(defaultRoot, "tools/capability-inventory.gaps.md")
    if (args.includes("--accept")) {
      writeFileSync(baselinePath, JSON.stringify(semanticSnapshot(inventory), null, 2) + "\n")
      writeFileSync(reportPath, gapReport(inventory))
    }
    if (args.includes("--check")) {
      checkBaseline(inventory, JSON.parse(readFileSync(baselinePath, "utf8")))
    }
    process.stdout.write(`Generated ${inventory.counts.chat} chat tools, ${inventory.counts.mcp} MCP tools, ${inventory.counts.entities} entity views, ${inventory.counts.fields} fields and ${inventory.gaps.length} gap observations.\n`)
  } catch (error) {
    process.stderr.write(`capability-inventory: ${error.message}\n`)
    process.exitCode = 1
  }
}
