import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"
import { test } from "node:test"

const root = dirname(fileURLToPath(import.meta.url))
const ses = readFileSync(join(root, "ses.tf"), "utf8")
const configuration = readFileSync(join(root, "configuration.tf"), "utf8")
const literal = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")

function block(source, header) {
  const start = source.indexOf(header)
  assert.notEqual(start, -1, `Missing ${header}`)
  const opening = source.indexOf("{", start + header.length - (header.endsWith("{") ? 1 : 0))
  let depth = 0
  for (let index = opening; index < source.length; index++) {
    if (source[index] === "{") depth++
    if (source[index] === "}" && --depth === 0) return source.slice(opening + 1, index)
  }
  assert.fail(`Unclosed ${header}`)
}

test("each API environment uses its own SES configuration sets and event topic", () => {
  const production = block(configuration, "production_api_values = {")
  const staging = block(configuration, "staging_api_values = merge(")

  for (const [environment, resource, topic, values] of [
    ["production", "orbit", "ses_events", production],
    ["staging", "staging", "ses_events_staging", staging],
  ]) {
    for (const stream of ["transactional", "marketing"]) {
      const key = stream === "transactional" ? "Ses__TransactionalConfigurationSet" : "Ses__MarketingConfigurationSet"
      assert.match(values, new RegExp(`${literal(key)}\\s*=\\s*aws_sesv2_configuration_set\\.${literal(resource)}\\["${literal(stream)}"\\]\\.configuration_set_name`), environment)
    }
    assert.match(values, new RegExp(`Ses__TopicArn\\s*=\\s*aws_sns_topic\\.${literal(topic)}\\.arn`), environment)
  }
})

test("SES event destinations and subscriptions stay within the sending environment", () => {
  for (const [resource, destination, topic, subscription, endpoint] of [
    ["orbit", "sns", "ses_events", "ses_events_api", "https://api.useorbit.org/api/email/ses-events"],
    ["staging", "sns_staging", "ses_events_staging", "ses_events_staging_api", "https://api-staging.useorbit.org/api/email/ses-events"],
  ]) {
    const event = block(ses, `resource "aws_sesv2_configuration_set_event_destination" "${destination}"`)
    const hook = block(ses, `resource "aws_sns_topic_subscription" "${subscription}"`)
    assert.match(event, new RegExp(`for_each\\s*=\\s*aws_sesv2_configuration_set\\.${literal(resource)}`))
    assert.match(event, new RegExp(`topic_arn\\s*=\\s*aws_sns_topic\\.${literal(topic)}\\.arn`))
    assert.match(hook, new RegExp(`topic_arn\\s*=\\s*aws_sns_topic\\.${literal(topic)}\\.arn`))
    assert.ok(hook.includes(`endpoint  = "${endpoint}"`))
  }
})

test("each SES webhook retains undeliverable events in its own queue", () => {
  for (const [subscription, queue, topic] of [
    ["ses_events_api", "ses_events", "ses_events"],
    ["ses_events_staging_api", "ses_events_staging", "ses_events_staging"],
  ]) {
    const hook = block(ses, `resource "aws_sns_topic_subscription" "${subscription}"`)
    const deadLetterQueue = block(ses, `resource "aws_sqs_queue" "${queue}"`)
    const queuePolicy = block(ses, `resource "aws_sqs_queue_policy" "${queue}"`)
    assert.match(hook, new RegExp(`redrive_policy\\s*=\\s*jsonencode\\(\\{\\s*deadLetterTargetArn\\s*=\\s*aws_sqs_queue\\.${queue}\\.arn`))
    assert.match(deadLetterQueue, /message_retention_seconds\s*=\s*1209600/)
    assert.match(deadLetterQueue, /kms_master_key_id\s*=\s*aws_kms_key\.ses_events\.arn/)
    assert.match(queuePolicy, new RegExp(`aws_sns_topic\\.${topic}\\.arn`))
    assert.match(queuePolicy, /sns\.amazonaws\.com/)
  }
})

test("each SES dead-letter queue alerts its own operations topic", () => {
  const variables = readFileSync(join(root, "variables.tf"), "utf8")
  for (const [queue, alarm, topic, topicName, emailVariable] of [
    ["ses_events", "ses_events_dead_letter", "ses_dead_letter_alerts", "orbit-ses-dead-letter-alerts", "production_ses_dlq_alert_email"],
    ["ses_events_staging", "ses_events_staging_dead_letter", "ses_dead_letter_alerts_staging", "orbit-ses-dead-letter-alerts-staging", "staging_ses_dlq_alert_email"],
  ]) {
    const deadLetterQueue = block(ses, `resource "aws_sqs_queue" "${queue}"`)
    const alert = block(ses, `resource "aws_cloudwatch_metric_alarm" "${alarm}"`)
    const operationsTopic = block(ses, `resource "aws_sns_topic" "${topic}"`)
    const subscription = block(ses, `resource "aws_sns_topic_subscription" "${topic}"`)
    const topicPolicy = block(ses, `resource "aws_sns_topic_policy" "${topic}"`)
    const email = block(variables, `variable "${emailVariable}"`)

    assert.match(deadLetterQueue, /message_retention_seconds\s*=\s*1209600/)
    assert.match(alert, /namespace\s*=\s*"AWS\/SQS"/)
    assert.match(alert, /metric_name\s*=\s*"ApproximateNumberOfMessagesVisible"/)
    assert.match(alert, new RegExp(`QueueName\\s*=\\s*aws_sqs_queue\\.${queue}\\.name`))
    assert.match(alert, /comparison_operator\s*=\s*"GreaterThanOrEqualToThreshold"/)
    assert.match(alert, /threshold\s*=\s*1/)
    assert.match(alert, /evaluation_periods\s*=\s*1/)
    assert.match(alert, /treat_missing_data\s*=\s*"notBreaching"/)
    assert.match(alert, new RegExp(`alarm_actions\\s*=\\s*\\[aws_sns_topic\\.${topic}\\.arn\\]`))
    assert.ok(operationsTopic.includes(`name = "${topicName}"`))
    assert.match(subscription, new RegExp(`topic_arn\\s*=\\s*aws_sns_topic\\.${topic}\\.arn`))
    assert.match(subscription, /protocol\s*=\s*"email"/)
    assert.match(subscription, new RegExp(`endpoint\\s*=\\s*var\\.${emailVariable}`))
    assert.match(subscription, new RegExp(`count\\s*=\\s*var\\.${emailVariable}\\s*==\\s*""\\s*\\?\\s*0\\s*:\\s*1`))
    assert.match(topicPolicy, /cloudwatch\.amazonaws\.com/)
    assert.match(topicPolicy, new RegExp(`aws_cloudwatch_metric_alarm\\.${alarm}\\.arn`))
    assert.match(topicPolicy, new RegExp(`Resource\\s*=\\s*aws_sns_topic\\.${topic}\\.arn`))
    assert.match(email, /default\s*=\s*""/)
  }
})

test("each API environment has credentials scoped to its own configuration sets", () => {
  const productionPolicy = block(ses, 'resource "aws_iam_user_policy" "api_ses"')
  const stagingPolicy = block(ses, 'resource "aws_iam_user_policy" "api_ses_staging"')
  const keyParameter = block(ses, 'resource "aws_ssm_parameter" "api_ses_access_key_id"')
  const secretParameter = block(ses, 'resource "aws_ssm_parameter" "api_ses_secret_access_key"')

  assert.match(productionPolicy, /aws_sesv2_configuration_set\.orbit/)
  assert.doesNotMatch(productionPolicy, /aws_sesv2_configuration_set\.staging/)
  assert.match(stagingPolicy, /aws_sesv2_configuration_set\.staging/)
  assert.doesNotMatch(stagingPolicy, /aws_sesv2_configuration_set\.orbit/)
  assert.match(keyParameter, /each\.key == "production" \? aws_iam_access_key\.api_ses\.id : aws_iam_access_key\.api_ses_staging\.id/)
  assert.match(secretParameter, /each\.key == "production" \? aws_iam_access_key\.api_ses\.secret : aws_iam_access_key\.api_ses_staging\.secret/)
})
