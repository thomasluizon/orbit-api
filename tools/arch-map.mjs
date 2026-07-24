#!/usr/bin/env node
/**
 * Architecture map generator (REBUILD.md Phase 2, api half).
 * Regex/text parses the .cs sources into architecture.json + architecture.html
 * at the repo root. Deterministic: stable sorts, no timestamps, LF output.
 */
import { readdirSync, readFileSync, writeFileSync, statSync } from "node:fs";
import { join, relative, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..");

function walk(dir, out = []) {
  for (const name of readdirSync(dir).sort()) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) walk(full, out);
    else if (name.endsWith(".cs")) out.push(full);
  }
  return out;
}

function rel(path) {
  return relative(repoRoot, path).replaceAll("\\", "/");
}

function stripComments(source) {
  let out = "";
  let i = 0;
  let mode = "code";
  while (i < source.length) {
    const two = source.slice(i, i + 2);
    const ch = source[i];
    if (mode === "code") {
      if (two === "//") mode = "line";
      else if (two === "/*") mode = "block";
      else if (ch === '"') {
        mode = source[i - 1] === "@" || source.slice(i - 2, i) === '@$' || source.slice(i - 2, i) === '$@' ? "verbatim" : "string";
        out += ch;
      } else if (ch === "'") {
        mode = "char";
        out += ch;
      } else out += ch;
    } else if (mode === "line") {
      if (ch === "\n") {
        mode = "code";
        out += ch;
      }
    } else if (mode === "block") {
      if (two === "*/") {
        mode = "code";
        i += 1;
      } else if (ch === "\n") out += ch;
    } else if (mode === "string") {
      out += ch;
      if (ch === "\\") {
        out += source[i + 1] ?? "";
        i += 1;
      } else if (ch === '"') mode = "code";
    } else if (mode === "verbatim") {
      out += ch;
      if (ch === '"') {
        if (source[i + 1] === '"') {
          out += '"';
          i += 1;
        } else mode = "code";
      }
    } else if (mode === "char") {
      out += ch;
      if (ch === "\\") {
        out += source[i + 1] ?? "";
        i += 1;
      } else if (ch === "'") mode = "code";
    }
    i += 1;
  }
  return out;
}

function matchBalanced(text, openIndex, open, close) {
  let depth = 0;
  for (let i = openIndex; i < text.length; i += 1) {
    if (text[i] === open) depth += 1;
    else if (text[i] === close) {
      depth -= 1;
      if (depth === 0) return i;
    }
  }
  return -1;
}

function splitTopLevel(argsText) {
  const parts = [];
  let depth = 0;
  let current = "";
  for (const ch of argsText) {
    if (ch === "<" || ch === "(") depth += 1;
    else if (ch === ">" || ch === ")") depth -= 1;
    if (ch === "," && depth === 0) {
      parts.push(current.trim());
      current = "";
    } else current += ch;
  }
  if (current.trim()) parts.push(current.trim());
  return parts;
}

function simpleName(type) {
  return type.replace(/^[\w.]*\./, "");
}

const HTTP_ATTR = /\[Http(Get|Post|Put|Delete|Patch|Head|Options)(?:\(\s*"([^"]*)"\s*\))?\]/;
const ATTRIBUTE = String.raw`\[(?:[^\]"\n]|"[^"\n]*")*\]`;
const ATTRIBUTE_OR_PRAGMA = String.raw`(?:${ATTRIBUTE}|#[^\n]*)\s*`;

function parseAuthorization(attrText) {
  const auth = attrText.match(/\[(Authorize(?:\([^)]*\))?|AllowAnonymous)\]/);
  return auth ? auth[1] : null;
}

function composeRoute(classRoute, methodTemplate, controllerName, actionName) {
  const shortName = controllerName.replace(/Controller$/, "");
  const substitute = (template) =>
    template
      .replaceAll("[controller]", shortName.toLowerCase())
      .replaceAll("[action]", actionName.toLowerCase());
  const parts = [];
  if (classRoute) parts.push(substitute(classRoute).replace(/^\/|\/$/g, ""));
  if (methodTemplate) parts.push(substitute(methodTemplate).replace(/^\/|\/$/g, ""));
  return "/" + parts.filter(Boolean).join("/");
}

function parseControllers() {
  const controllersDir = join(repoRoot, "src", "Orbit.Api", "Controllers");
  const classes = new Map();
  for (const file of walk(controllersDir)) {
    const text = stripComments(readFileSync(file, "utf8"));
    const classMatch = text.match(
      new RegExp(
        String.raw`((?:${ATTRIBUTE_OR_PRAGMA})*)public\s+(?:sealed\s+)?(?:partial\s+)?class\s+(\w+Controller)\b`
      )
    );
    if (!classMatch) continue;
    const [, classAttrs, className] = classMatch;
    const routeMatch = classAttrs.match(/\[Route\(\s*"([^"]*)"\s*\)\]/);
    const entry = classes.get(className) ?? {
      name: className,
      classRoute: null,
      classAuth: null,
      actions: [],
    };
    if (routeMatch) entry.classRoute = routeMatch[1];
    const classAuth = parseAuthorization(classAttrs);
    if (classAuth) entry.classAuth = classAuth;
    parseActions(text, entry);
    classes.set(className, entry);
  }
  return [...classes.values()].sort((a, b) => a.name.localeCompare(b.name));
}

function parseActions(text, entry) {
  const methodPattern = new RegExp(
    String.raw`((?:${ATTRIBUTE_OR_PRAGMA})+)public\s+(?:async\s+)?([\w.?]+(?:<.*?>)?\??)\s+(\w+)\s*\(`,
    "g"
  );
  let match;
  while ((match = methodPattern.exec(text)) !== null) {
    const [, attrText, returnType, methodName] = match;
    const httpMatch = attrText.match(HTTP_ATTR);
    if (!httpMatch) continue;
    const paramsOpen = text.indexOf("(", match.index + match[0].length - 1);
    const paramsClose = matchBalanced(text, paramsOpen, "(", ")");
    const bodyOpen = text.indexOf("{", paramsClose);
    const arrow = text.indexOf("=>", paramsClose);
    let body = "";
    if (bodyOpen !== -1 && (arrow === -1 || bodyOpen < arrow)) {
      const bodyClose = matchBalanced(text, bodyOpen, "{", "}");
      body = text.slice(bodyOpen, bodyClose + 1);
    } else if (arrow !== -1) {
      body = text.slice(arrow, text.indexOf(";", arrow) + 1);
    }
    entry.actions.push({
      action: methodName,
      httpMethod: httpMatch[1].toUpperCase(),
      methodTemplate: httpMatch[2] ?? null,
      methodAuth: parseAuthorization(attrText),
      attrText,
      returnType,
      body,
    });
  }
}

function deriveMediatrRequest(body) {
  const send = body.match(/[Mm]ediator\.Send(?:<[^>]+>)?\(\s*(?:new\s+([\w.]+)|([\w]+))/) ??
    body.match(/[Ss]ender\.Send(?:<[^>]+>)?\(\s*(?:new\s+([\w.]+)|([\w]+))/);
  if (!send) return null;
  if (send[1]) return simpleName(send[1]);
  const variable = send[2];
  const assignment = body.match(
    new RegExp(`(?:var\\s+)?${variable}\\s*=\\s*new\\s+([\\w.]+)`)
  );
  return assignment ? simpleName(assignment[1]) : null;
}

function unwrapResponse(response) {
  if (!response) return null;
  const trimmed = response.trim();
  if (trimmed === "Result" || trimmed === "Unit") return null;
  const resultMatch = trimmed.match(/^Result<(.*)>$/);
  return resultMatch ? resultMatch[1].trim() : trimmed;
}

function deriveResponseDto(action, handlerResponseByRequest, mediatrRequest) {
  const actionResult = action.returnType.match(/ActionResult<(.*)>/);
  if (actionResult) return actionResult[1].trim();
  const produces =
    action.attrText.match(/\[ProducesResponseType\(\s*typeof\(([^)]+)\)\s*,\s*(?:StatusCodes\.Status20\d\w*|20\d)\s*\)\]/) ??
    action.attrText.match(/\[ProducesResponseType<([^>]+)>\(\s*(?:StatusCodes\.Status20\d\w*|20\d)\s*\)\]/);
  if (produces) return produces[1].trim();
  if (mediatrRequest && handlerResponseByRequest.has(mediatrRequest))
    return unwrapResponse(handlerResponseByRequest.get(mediatrRequest));
  return null;
}

function parseHandlers() {
  const applicationDir = join(repoRoot, "src", "Orbit.Application");
  const handlers = [];
  for (const file of walk(applicationDir)) {
    const text = stripComments(readFileSync(file, "utf8"));
    const pattern = /IRequestHandler</g;
    let match;
    while ((match = pattern.exec(text)) !== null) {
      const openIndex = match.index + "IRequestHandler".length;
      const closeIndex = matchBalanced(text, openIndex, "<", ">");
      if (closeIndex === -1) continue;
      const args = splitTopLevel(text.slice(openIndex + 1, closeIndex));
      if (args.length === 0) continue;
      const before = text.slice(Math.max(0, match.index - 400), match.index);
      if (!/[:,]\s*$/.test(before)) continue;
      const relFile = rel(file);
      const segments = relFile.split("/");
      const featureFolder = segments.length > 3 ? segments[2] : "(root)";
      handlers.push({
        request: simpleName(args[0].replace(/\s+/g, " ")),
        response: args[1] ? args[1].replace(/\s+/g, " ") : "Unit",
        handlerFile: relFile,
        featureFolder,
      });
    }
  }
  const unique = new Map();
  for (const handler of handlers) {
    unique.set(`${handler.request}|${handler.handlerFile}`, handler);
  }
  return [...unique.values()].sort(
    (a, b) => a.request.localeCompare(b.request) || a.handlerFile.localeCompare(b.handlerFile)
  );
}

function parseDependencies() {
  const projects = ["Orbit.Api", "Orbit.Application", "Orbit.Domain", "Orbit.Infrastructure"];
  const graph = {};
  for (const project of projects) {
    const csproj = readFileSync(
      join(repoRoot, "src", project, `${project}.csproj`),
      "utf8"
    );
    const refs = [];
    const pattern = /<ProjectReference\s+Include="([^"]+)"/g;
    let match;
    while ((match = pattern.exec(csproj)) !== null) {
      const name = match[1].replaceAll("\\", "/").split("/").pop().replace(/\.csproj$/, "");
      refs.push(name);
    }
    graph[project] = refs.sort();
  }
  const featureFolders = {};
  const applicationDir = join(repoRoot, "src", "Orbit.Application");
  for (const file of walk(applicationDir)) {
    const segments = rel(file).split("/");
    const folder = segments.length > 3 ? segments[2] : "(root)";
    featureFolders[folder] = (featureFolders[folder] ?? 0) + 1;
  }
  const sortedFolders = Object.fromEntries(
    Object.entries(featureFolders).sort(([a], [b]) => a.localeCompare(b))
  );
  return { projects: graph, featureFolders: sortedFolders };
}

function parseEntities() {
  const contextFile = join(repoRoot, "src", "Orbit.Infrastructure", "Persistence", "OrbitDbContext.cs");
  const text = readFileSync(contextFile, "utf8");
  const names = new Set();
  const pattern = /DbSet<([\w.]+)>/g;
  let match;
  while ((match = pattern.exec(text)) !== null) names.add(simpleName(match[1]));
  const domainFiles = walk(join(repoRoot, "src", "Orbit.Domain"));
  const entities = [];
  for (const name of [...names].sort()) {
    let found = null;
    const declaration = new RegExp(`(?:class|record)\\s+${name}\\b`);
    for (const file of domainFiles) {
      if (declaration.test(readFileSync(file, "utf8"))) {
        found = rel(file);
        break;
      }
    }
    entities.push({ entity: name, file: found });
  }
  return entities;
}

function parseTestCoverage(handlers, entities, featureFolderNames) {
  const typeToFolder = new Map();
  const knownTypes = new Set();
  for (const handler of handlers) {
    knownTypes.add(handler.request);
    typeToFolder.set(handler.request, handler.featureFolder);
    const handlerClass = handler.request + "Handler";
    knownTypes.add(handlerClass);
    typeToFolder.set(handlerClass, handler.featureFolder);
  }
  for (const entity of entities) knownTypes.add(entity.entity);

  const testClasses = [];
  const folderTouches = new Map(featureFolderNames.map((name) => [name, new Set()]));
  for (const file of walk(join(repoRoot, "tests"))) {
    const text = stripComments(readFileSync(file, "utf8"));
    const identifiers = new Set(text.match(/\b[A-Z]\w+\b/g) ?? []);
    const references = [...knownTypes].filter((type) => identifiers.has(type)).sort();
    const usingFolders = new Set();
    for (const usingMatch of text.matchAll(/using\s+Orbit\.Application\.(\w+)/g)) {
      usingFolders.add(usingMatch[1]);
    }
    const classPattern = /(?:public|internal)\s+(?:sealed\s+)?(?:abstract\s+)?(?:partial\s+)?class\s+(\w+)/g;
    let classMatch;
    while ((classMatch = classPattern.exec(text)) !== null) {
      const testClass = classMatch[1];
      if (references.length === 0 && usingFolders.size === 0) continue;
      testClasses.push({ testClass, file: rel(file), references });
      const touched = new Set(usingFolders);
      for (const type of references) {
        const folder = typeToFolder.get(type);
        if (folder) touched.add(folder);
      }
      for (const folder of touched) {
        if (folderTouches.has(folder)) folderTouches.get(folder).add(`${rel(file)}#${testClass}`);
      }
    }
  }
  testClasses.sort((a, b) => a.file.localeCompare(b.file) || a.testClass.localeCompare(b.testClass));
  const perFolder = Object.fromEntries(
    [...folderTouches.entries()]
      .map(([folder, set]) => [folder, set.size])
      .sort(([a], [b]) => a.localeCompare(b))
  );
  const untested = Object.entries(perFolder)
    .filter(([, count]) => count === 0)
    .map(([folder]) => folder);
  return { featureFolders: perFolder, untested, testClasses };
}

function buildEndpoints(controllers, handlerResponseByRequest) {
  const endpoints = [];
  for (const controller of controllers) {
    for (const action of controller.actions) {
      const mediatrRequest = deriveMediatrRequest(action.body);
      const authSource = action.methodAuth
        ? `${action.methodAuth} (method)`
        : controller.classAuth
          ? `${controller.classAuth} (class)`
          : "none";
      endpoints.push({
        controller: controller.name,
        action: action.action,
        httpMethod: action.httpMethod,
        routeTemplate: composeRoute(
          controller.classRoute,
          action.methodTemplate,
          controller.name,
          action.action
        ),
        authorization: authSource,
        mediatrRequest,
        responseDto: deriveResponseDto(action, handlerResponseByRequest, mediatrRequest),
      });
    }
  }
  return endpoints.sort(
    (a, b) =>
      a.routeTemplate.localeCompare(b.routeTemplate) ||
      a.httpMethod.localeCompare(b.httpMethod) ||
      a.action.localeCompare(b.action)
  );
}

const controllers = parseControllers();
const handlers = parseHandlers();
const handlerResponseByRequest = new Map(handlers.map((h) => [h.request, h.response]));
const endpoints = buildEndpoints(controllers, handlerResponseByRequest);
const dependencies = parseDependencies();
const entities = parseEntities();
const testCoverage = parseTestCoverage(handlers, entities, Object.keys(dependencies.featureFolders));

const requestsWithEndpoint = new Set(endpoints.map((e) => e.mediatrRequest).filter(Boolean));
const orphans = {
  endpointsWithoutHandler: endpoints
    .filter((e) => e.mediatrRequest !== null && !handlerResponseByRequest.has(e.mediatrRequest))
    .map((e) => ({ controller: e.controller, action: e.action, mediatrRequest: e.mediatrRequest })),
  handlersWithoutEndpoint: handlers
    .filter((h) => !requestsWithEndpoint.has(h.request))
    .map((h) => ({ request: h.request, handlerFile: h.handlerFile })),
};

const architecture = {
  endpoints,
  handlers,
  orphans,
  dependencies,
  entities,
  testCoverage,
};

const json = JSON.stringify(architecture, null, 2) + "\n";
writeFileSync(join(repoRoot, "architecture.json"), json, "utf8");

const embedded = JSON.stringify(architecture).replaceAll("</", "<\\/");
const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Orbit API architecture map</title>
<style>
:root { --bg: #020618; --card: #0b1026; --fg: #e2e8f0; --muted: #94a3b8; --accent: #7f46f7; --warn: #f59e0b; --bad: #ef4444; --hairline: rgba(255,255,255,0.08); }
* { box-sizing: border-box; }
body { margin: 0; padding: 24px; background: var(--bg); color: var(--fg); font: 14px/1.5 system-ui, sans-serif; }
h1 { font-size: 22px; margin: 0 0 4px; }
h2 { font-size: 16px; margin: 32px 0 8px; color: var(--accent); }
p.sub { color: var(--muted); margin: 0 0 16px; }
.counts { display: flex; flex-wrap: wrap; gap: 12px; margin: 16px 0; }
.counts div { background: var(--card); border: 1px solid var(--hairline); border-radius: 10px; padding: 10px 16px; }
.counts b { display: block; font-size: 20px; }
.counts .warn b { color: var(--warn); }
.counts .bad b { color: var(--bad); }
.tablewrap { overflow-x: auto; border: 1px solid var(--hairline); border-radius: 10px; }
table { border-collapse: collapse; width: 100%; background: var(--card); }
th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--hairline); white-space: nowrap; }
th { color: var(--muted); font-weight: 600; position: sticky; top: 0; background: var(--card); }
tr.orphan td { background: rgba(239,68,68,0.12); }
tr.untested td { background: rgba(245,158,11,0.12); }
td.method { font-weight: 700; color: var(--accent); }
code { font-family: ui-monospace, monospace; font-size: 13px; }
a { color: var(--accent); }
.graph { background: var(--card); border: 1px solid var(--hairline); border-radius: 10px; padding: 16px; }
.graph li { margin: 4px 0; }
.pill { display: inline-block; padding: 1px 8px; border-radius: 999px; font-size: 12px; border: 1px solid var(--hairline); }
.pill.anon { color: var(--warn); border-color: var(--warn); }
.pill.none { color: var(--bad); border-color: var(--bad); }
.muted { color: var(--muted); }
</style>
</head>
<body>
<h1>Orbit API architecture map</h1>
<p class="sub">Generated by tools/arch-map.mjs. Orphans and untested folders are the signal, not noise.</p>
<div class="counts" id="counts"></div>
<h2>Project graph</h2>
<div class="graph"><ul id="graph"></ul></div>
<h2>Endpoints</h2>
<div class="tablewrap"><table id="endpoints"><thead><tr><th>Route</th><th>Method</th><th>Auth</th><th>Request</th><th>Handler</th><th>Response DTO</th></tr></thead><tbody></tbody></table></div>
<h2>Feature folders</h2>
<div class="tablewrap"><table id="features"><thead><tr><th>Folder</th><th>Files</th><th>Handlers</th><th>Test classes touching</th></tr></thead><tbody></tbody></table></div>
<h2>Handlers with no endpoint</h2>
<div class="tablewrap"><table id="orphanHandlers"><thead><tr><th>Request</th><th>Handler file</th></tr></thead><tbody></tbody></table></div>
<h2>Entities</h2>
<div class="tablewrap"><table id="entities"><thead><tr><th>Entity</th><th>Domain file</th></tr></thead><tbody></tbody></table></div>
<script id="arch-data" type="application/json">${embedded}</script>
<script>
const data = JSON.parse(document.getElementById("arch-data").textContent);
const byRequest = new Map(data.handlers.map((h) => [h.request, h]));
const orphanEndpointKeys = new Set(
  data.orphans.endpointsWithoutHandler.map((o) => o.controller + "." + o.action)
);
function el(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined && text !== null) node.textContent = text;
  if (className) node.className = className;
  return node;
}
const counts = document.getElementById("counts");
const untestedCount = data.testCoverage.untested.length;
[
  ["Endpoints", data.endpoints.length, ""],
  ["Handlers", data.handlers.length, ""],
  ["Orphan endpoints", data.orphans.endpointsWithoutHandler.length, data.orphans.endpointsWithoutHandler.length ? "bad" : ""],
  ["Orphan handlers", data.orphans.handlersWithoutEndpoint.length, data.orphans.handlersWithoutEndpoint.length ? "warn" : ""],
  ["Entities", data.entities.length, ""],
  ["Untested feature folders", untestedCount, untestedCount ? "warn" : ""],
].forEach(([label, value, cls]) => {
  const card = el("div", null, cls);
  card.appendChild(el("b", String(value)));
  card.appendChild(el("span", label));
  counts.appendChild(card);
});
const graph = document.getElementById("graph");
Object.entries(data.dependencies.projects).forEach(([project, refs]) => {
  graph.appendChild(el("li", project + " -> " + (refs.length ? refs.join(", ") : "(none)")));
});
const endpointsBody = document.querySelector("#endpoints tbody");
data.endpoints.forEach((endpoint) => {
  const row = document.createElement("tr");
  const isOrphan = orphanEndpointKeys.has(endpoint.controller + "." + endpoint.action);
  if (isOrphan) row.className = "orphan";
  const routeCell = el("td");
  routeCell.appendChild(el("code", endpoint.routeTemplate));
  row.appendChild(routeCell);
  row.appendChild(el("td", endpoint.httpMethod, "method"));
  const authCell = el("td");
  const authClass = endpoint.authorization === "none" ? "pill none"
    : endpoint.authorization.startsWith("AllowAnonymous") ? "pill anon" : "pill";
  authCell.appendChild(el("span", endpoint.authorization, authClass));
  row.appendChild(authCell);
  row.appendChild(el("td", endpoint.mediatrRequest ?? "(none derived)", endpoint.mediatrRequest ? "" : "muted"));
  const handler = endpoint.mediatrRequest ? byRequest.get(endpoint.mediatrRequest) : null;
  const handlerCell = el("td");
  if (handler) handlerCell.appendChild(el("code", handler.handlerFile));
  else handlerCell.appendChild(el("span", isOrphan ? "NO HANDLER" : "", isOrphan ? "pill none" : "muted"));
  row.appendChild(handlerCell);
  row.appendChild(el("td", endpoint.responseDto ?? "", endpoint.responseDto ? "" : "muted"));
  endpointsBody.appendChild(row);
});
const featuresBody = document.querySelector("#features tbody");
const handlerCountByFolder = {};
data.handlers.forEach((h) => {
  handlerCountByFolder[h.featureFolder] = (handlerCountByFolder[h.featureFolder] ?? 0) + 1;
});
Object.entries(data.dependencies.featureFolders).forEach(([folder, files]) => {
  const row = document.createElement("tr");
  const tests = data.testCoverage.featureFolders[folder] ?? 0;
  if (tests === 0) row.className = "untested";
  row.appendChild(el("td", folder));
  row.appendChild(el("td", String(files)));
  row.appendChild(el("td", String(handlerCountByFolder[folder] ?? 0)));
  row.appendChild(el("td", tests === 0 ? "0 (UNTESTED)" : String(tests)));
  featuresBody.appendChild(row);
});
const orphanHandlersBody = document.querySelector("#orphanHandlers tbody");
data.orphans.handlersWithoutEndpoint.forEach((orphan) => {
  const row = document.createElement("tr");
  row.appendChild(el("td", orphan.request));
  const fileCell = el("td");
  fileCell.appendChild(el("code", orphan.handlerFile));
  row.appendChild(fileCell);
  orphanHandlersBody.appendChild(row);
});
const entitiesBody = document.querySelector("#entities tbody");
data.entities.forEach((entity) => {
  const row = document.createElement("tr");
  row.appendChild(el("td", entity.entity));
  const fileCell = el("td");
  if (entity.file) fileCell.appendChild(el("code", entity.file));
  else fileCell.appendChild(el("span", "(not found in Orbit.Domain)", "muted"));
  row.appendChild(fileCell);
  entitiesBody.appendChild(row);
});
</script>
</body>
</html>
`;
writeFileSync(join(repoRoot, "architecture.html"), html, "utf8");

const summary = {
  endpoints: endpoints.length,
  handlers: handlers.length,
  orphanEndpoints: orphans.endpointsWithoutHandler.length,
  orphanHandlers: orphans.handlersWithoutEndpoint.length,
  entities: entities.length,
  untestedFeatureFolders: testCoverage.untested.length,
};
process.stdout.write(JSON.stringify(summary, null, 2) + "\n");
