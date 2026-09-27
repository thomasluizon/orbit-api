locals {
  ses_streams = {
    transactional = {
      domain            = "send.useorbit.org"
      mail_from_domain  = "bounce.send.useorbit.org"
      configuration_set = "orbit-transactional"
    }
    marketing = {
      domain            = "updates.useorbit.org"
      mail_from_domain  = "bounce.updates.useorbit.org"
      configuration_set = "orbit-marketing"
    }
  }
}

resource "aws_sesv2_email_identity" "orbit" {
  for_each       = local.ses_streams
  email_identity = each.value.domain

  dkim_signing_attributes {
    next_signing_key_length = "RSA_2048_BIT"
  }
}

resource "aws_sesv2_email_identity_mail_from_attributes" "orbit" {
  for_each               = local.ses_streams
  email_identity         = aws_sesv2_email_identity.orbit[each.key].email_identity
  mail_from_domain       = each.value.mail_from_domain
  behavior_on_mx_failure = "REJECT_MESSAGE"
}

resource "cloudflare_dns_record" "ses_dkim" {
  for_each = {
    for pair in flatten([
      for stream in keys(local.ses_streams) : [
        for index in range(3) : {
          key    = "${stream}-${index}"
          stream = stream
          index  = index
        }
      ]
    ]) : pair.key => pair
  }

  zone_id = cloudflare_zone.orbit.id
  name    = "${aws_sesv2_email_identity.orbit[each.value.stream].dkim_signing_attributes[0].tokens[each.value.index]}._domainkey.${local.ses_streams[each.value.stream].domain}"
  type    = "CNAME"
  content = "${aws_sesv2_email_identity.orbit[each.value.stream].dkim_signing_attributes[0].tokens[each.value.index]}.dkim.amazonses.com"
  ttl     = 1
  proxied = false
}

resource "cloudflare_dns_record" "ses_mail_from_mx" {
  for_each = aws_sesv2_email_identity_mail_from_attributes.orbit

  zone_id  = cloudflare_zone.orbit.id
  name     = each.value.mail_from_domain
  type     = "MX"
  content  = "feedback-smtp.us-east-2.amazonses.com"
  priority = 10
  ttl      = 1
  proxied  = false
}

resource "cloudflare_dns_record" "ses_mail_from_spf" {
  for_each = aws_sesv2_email_identity_mail_from_attributes.orbit

  zone_id = cloudflare_zone.orbit.id
  name    = each.value.mail_from_domain
  type    = "TXT"
  content = "v=spf1 include:amazonses.com ~all"
  ttl     = 1
  proxied = false
}

resource "aws_sesv2_configuration_set" "orbit" {
  for_each               = local.ses_streams
  configuration_set_name = each.value.configuration_set
}

resource "aws_sns_topic" "ses_events" {
  name              = "orbit-ses-events"
  signature_version = 2
}

resource "aws_sns_topic_policy" "ses_events" {
  arn = aws_sns_topic.ses_events.arn
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "AllowSesEvents"
      Effect    = "Allow"
      Principal = { Service = "ses.amazonaws.com" }
      Action    = "SNS:Publish"
      Resource  = aws_sns_topic.ses_events.arn
      Condition = {
        StringEquals = { "AWS:SourceAccount" = "713285551626" }
        ArnEquals    = { "AWS:SourceArn" = [for configuration in aws_sesv2_configuration_set.orbit : configuration.arn] }
      }
    }]
  })
}

resource "aws_sesv2_configuration_set_event_destination" "sns" {
  for_each               = aws_sesv2_configuration_set.orbit
  configuration_set_name = each.value.configuration_set_name
  event_destination_name = "orbit-sns"

  event_destination {
    matching_event_types = ["BOUNCE", "COMPLAINT", "REJECT"]
    sns_destination {
      topic_arn = aws_sns_topic.ses_events.arn
    }
  }

  depends_on = [aws_sns_topic_policy.ses_events]
}

resource "aws_sns_topic_subscription" "ses_events_api" {
  topic_arn = aws_sns_topic.ses_events.arn
  protocol  = "https"
  endpoint  = "https://api.useorbit.org/api/email/ses-events"
}

resource "aws_sesv2_account_suppression_attributes" "orbit" {
  suppressed_reasons = ["BOUNCE", "COMPLAINT"]
}

resource "aws_iam_user" "api_ses" {
  name = "orbit-api-ses"
}

resource "aws_iam_user_policy" "api_ses" {
  name = "orbit-api-ses-send"
  user = aws_iam_user.api_ses.name
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect = "Allow"
      Action = ["ses:SendEmail", "ses:SendRawEmail"]
      Resource = concat(
        [for identity in aws_sesv2_email_identity.orbit : identity.arn],
        [for configuration in aws_sesv2_configuration_set.orbit : configuration.arn]
      )
    }]
  })
}

resource "aws_iam_access_key" "api_ses" {
  user = aws_iam_user.api_ses.name
}

resource "aws_ssm_parameter" "api_ses_access_key_id" {
  for_each = toset(["production", "staging"])
  name     = "/orbit/${each.key}/api/Ses__AccessKeyId"
  type     = "SecureString"
  value    = aws_iam_access_key.api_ses.id
}

resource "aws_ssm_parameter" "api_ses_secret_access_key" {
  for_each = toset(["production", "staging"])
  name     = "/orbit/${each.key}/api/Ses__SecretAccessKey"
  type     = "SecureString"
  value    = aws_iam_access_key.api_ses.secret
}
