data "archive_file" "production_health" {
  type        = "zip"
  source_file = "${path.module}/workers/production-health.mjs"
  output_path = "${path.module}/.terraform/production-health.zip"
}

resource "aws_cloudwatch_log_group" "production_health" {
  name              = "/aws/lambda/orbit-production-health"
  retention_in_days = 30
}

resource "aws_iam_role" "production_health" {
  name = "orbit-production-health"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "production_health" {
  name = "write-health-metrics"
  role = aws_iam_role.production_health.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
      Resource = "${aws_cloudwatch_log_group.production_health.arn}:*"
    }]
  })
}

resource "aws_lambda_function" "production_health" {
  function_name    = "orbit-production-health"
  role             = aws_iam_role.production_health.arn
  handler          = "production-health.handler"
  runtime          = "nodejs22.x"
  filename         = data.archive_file.production_health.output_path
  source_code_hash = data.archive_file.production_health.output_base64sha256
  timeout          = 30
  memory_size      = 128

  depends_on = [aws_iam_role_policy.production_health]
}

resource "aws_cloudwatch_event_rule" "production_health" {
  name                = "orbit-production-health"
  schedule_expression = "rate(1 minute)"
}

resource "aws_cloudwatch_event_target" "production_health" {
  rule = aws_cloudwatch_event_rule.production_health.name
  arn  = aws_lambda_function.production_health.arn
}

resource "aws_lambda_permission" "production_health" {
  statement_id  = "HealthSchedule"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.production_health.function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.production_health.arn
}

resource "aws_cloudwatch_metric_alarm" "production_health" {
  for_each = toset(["/health", "/health/jobs"])

  alarm_name          = "orbit-production-health-${each.key == "/health" ? "process" : "jobs"}"
  alarm_description   = "Production ${each.key} failed two consecutive polls or stopped reporting."
  namespace           = "Orbit/ProductionHealth"
  metric_name         = "Healthy"
  dimensions          = { Endpoint = each.key }
  statistic           = "Minimum"
  period              = 60
  evaluation_periods  = 2
  datapoints_to_alarm = 2
  comparison_operator = "LessThanThreshold"
  threshold           = 1
  treat_missing_data  = "breaching"
  alarm_actions       = [aws_sns_topic.ses_dead_letter_alerts.arn]
  ok_actions          = [aws_sns_topic.ses_dead_letter_alerts.arn]
}
