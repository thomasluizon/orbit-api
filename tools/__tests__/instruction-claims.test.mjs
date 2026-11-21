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
