import { test } from 'node:test'
import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../../', import.meta.url))
const read = (path) => readFileSync(root + path, 'utf8')

const resultSource = read('src/Orbit.Domain/Common/Result.cs')
const webhookHandler = read('src/Orbit.Application/Subscriptions/Commands/HandleWebhookCommand.cs')
const webhookAction = read('src/Orbit.Api/Controllers/SubscriptionController.cs')

const resultClaims = [
  { file: 'src/Orbit.Application/CLAUDE.md', forbidden: /Result<T>\.Failure\(/, required: /Result\.Failure<T>\(ErrorMessages\.<Name>\)/ },
]

for (const { file, forbidden, required } of resultClaims) {
  test(`${file} names a Result failure call that compiles with the message first`, () => {
    const text = read(file)
    assert.doesNotMatch(text, forbidden)
    assert.match(text, required)
    assert.match(resultSource, /public static Result<T> Failure<T>\(string error\)/)
    assert.ok(existsSync(root + 'src/Orbit.Application/Common/ErrorMessages.cs'))
  })
}

const webhookClaims = [
  { file: 'src/Orbit.Infrastructure/CLAUDE.md', owner: 'HandleWebhookCommandHandler', path: 'Orbit.Application/Subscriptions/Commands/HandleWebhookCommand.cs' },
]

for (const { file, owner, path } of webhookClaims) {
  test(`${file} puts the webhook signature check in ${owner}`, () => {
    const line = read(file).split('\n').find((l) => l.includes('WebhookSecret'))
    assert.ok(line)
    assert.ok(line.includes(owner))
    assert.ok(line.includes(path))
    assert.doesNotMatch(line, /SubscriptionController/)
    assert.ok(existsSync(root + 'src/' + path))
    assert.match(webhookHandler, /class HandleWebhookCommandHandler/)
    assert.match(webhookHandler, /StripeWebhookSignature\.Verify/)
    assert.match(webhookHandler, /WebhookSecret/)
    assert.doesNotMatch(webhookAction, /StripeWebhookSignature\.Verify|WebhookSecret/)
  })
}

const projects = [
  { name: 'Orbit.Api', hasFile: true },
  { name: 'Orbit.Application', hasFile: true },
  { name: 'Orbit.Domain', hasFile: false },
  { name: 'Orbit.Infrastructure', hasFile: true },
]

for (const { name, hasFile } of projects) {
  test(`CLAUDE.md claim about ${name} matches the tree`, () => {
    assert.equal(existsSync(`${root}src/${name}/CLAUDE.md`), hasFile)
    const claim = read('CLAUDE.md').split('\n').find((l) => l.includes('`Orbit.Api`, `Orbit.Application` and `Orbit.Infrastructure` each have a `CLAUDE.md`'))
    assert.ok(claim)
    const sentence = claim.split('. ').find((s) => s.includes('each have a'))
    assert.equal(sentence.includes('`' + name + '`'), hasFile)
    assert.doesNotMatch(read('CLAUDE.md'), /Each project has a `CLAUDE\.md`/)
  })
}

const replayReaders = [
  { test: 'tests/Orbit.Infrastructure.Tests/AI/AiIntentServiceStreamingTests.ServerConversations.cs', fixture: 'pt-BR.json' },
  { test: 'tests/Orbit.Infrastructure.Tests/Services/AgentChatWriteHoldTests.DateContext.cs', fixture: 'language + ".json"' },
]

for (const { test: testFile, fixture } of replayReaders) {
  test(`evals/astra/README.md names ${testFile.split('/').pop()} and only the files that it reads`, () => {
    const readme = read('evals/astra/README.md')
    const source = read(testFile)
    assert.ok(source.includes(fixture))
    assert.doesNotMatch(source, /large-account/)
    assert.ok(readme.includes('`' + testFile.split('/').pop() + '`'))
    assert.match(readme, /replay `en\.json` and `pt-BR\.json`\./)
    assert.doesNotMatch(readme, /replay these files/)
    assert.match(readme, /No test reads `large-account\.json`\./)
  })
}

const habitMonthDoc = read('src/Orbit.Api/OpenApi/HabitMonth.md').replace(/\s+/g, ' ')
const calendarWindow = read('src/Orbit.Domain/Models/HabitCalendarWindow.cs')
const periodRange = read('src/Orbit.Application/Habits/Queries/RetrospectivePeriodRange.cs')

const habitMonthWindowClaims = [
  {
    name: 'relative_period this_month ends today',
    forbidden: /complete local month, including future days/,
    required: /`this_month` ends today; `last_month` is the previous month/,
    source: periodRange,
    proof: /"this_month" => new DateOnly\(today\.Year, today\.Month, 1\),[\s\S]*return \(dateFrom, today\);/,
  },
  {
    name: 'a single date runs to the end or from the start of its month',
    forbidden: /one date alone selects the month that holds it/,
    required: /A start date alone runs to the last day of its month; an end date alone starts on the first day\./,
    source: calendarWindow,
    proof: /var anchor = from \?\? to \?\? today;[\s\S]*var start = from \?\? new DateOnly\(anchor\.Year, anchor\.Month, 1\);[\s\S]*var end = to \?\? new DateOnly\(start\.Year, start\.Month, DateTime\.DaysInMonth\(start\.Year, start\.Month\)\)/,
  },
  {
    name: 'a month window with no date is the current month, future days included',
    forbidden: /A `month` window is the complete local month/,
    required: /A `month` window with no date is the current month, future days included\./,
    source: calendarWindow,
    proof: /from \?\? to \?\? today/,
  },
]

for (const { name, forbidden, required, source, proof } of habitMonthWindowClaims) {
  test(`HabitMonth.md states that ${name}`, () => {
    assert.doesNotMatch(habitMonthDoc, forbidden)
    assert.match(habitMonthDoc, required)
    assert.match(source, proof)
  })
}

const fixturesReadme = read('tests/Orbit.Application.Tests/Fixtures/README.md').replace(/\s+/g, ' ')

const fixtureReplayClaims = [
  {
    name: 'Gamification/achievements.en.json is a copy of the consumer text, not model output',
    required: /`Gamification\/achievements\.en\.json` is a copy of the achievement text in the consumer `en\.json`\. It is not model output\./,
    source: 'tests/Orbit.Application.Tests/Gamification/AchievementDefinitionsTests.cs',
    proof: /achievements\.en\.json/,
  },
  {
    name: 'PendingRevisions bodies replay at the HTTP transport',
    required: /`PendingRevisions\/` and `TerminalReads\/` hold Responses API bodies\. The tests replay them at the HTTP transport, through the real SDK and `AiIntentService`\./,
    source: 'tests/Orbit.Application.Tests/Commands/Chat/ChatWriteHoldHandlerTests.RevisionOpinion.cs',
    proof: /HttpMessageHandler/,
  },
  {
    name: 'TerminalReads bodies replay at the HTTP transport',
    required: /`PendingRevisions\/` and `TerminalReads\/` hold Responses API bodies\. The tests replay them at the HTTP transport, through the real SDK and `AiIntentService`\./,
    source: 'tests/Orbit.Application.Tests/Chat/TerminalReadProviderTests.cs',
    proof: /HttpMessageHandler/,
  },
]

for (const { name, required, source, proof } of fixtureReplayClaims) {
  test(`Fixtures/README.md states that ${name}`, () => {
    assert.match(fixturesReadme, required)
    assert.doesNotMatch(fixturesReadme, /Each fixture file holds the output of the AI model for one test case\. The tests replay the file at `IAiIntentService`/)
    assert.match(read(source), proof)
  })
}
