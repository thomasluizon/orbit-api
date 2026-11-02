resource "aws_iam_openid_connect_provider" "github_actions" {
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
}

data "aws_iam_policy_document" "staging_reseed_trust" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]
    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github_actions.arn]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:thomasluizon/orbit-api:environment:render-operations"]
    }
  }
}

resource "aws_iam_role" "staging_reseed" {
  name               = "orbit-staging-reseed"
  assume_role_policy = data.aws_iam_policy_document.staging_reseed_trust.json
}

data "aws_iam_policy_document" "staging_reseed" {
  statement {
    actions   = ["s3:ListBucket"]
    resources = ["arn:aws:s3:::orbit-terraform-state-713285551626"]
    condition {
      test     = "StringLike"
      variable = "s3:prefix"
      values   = ["render/staging-database.tfstate", "render/staging-database.tfstate.tflock"]
    }
  }
  statement {
    actions   = ["s3:GetObject", "s3:PutObject"]
    resources = ["arn:aws:s3:::orbit-terraform-state-713285551626/render/staging-database.tfstate", "arn:aws:s3:::orbit-terraform-state-713285551626/render/staging-database.tfstate.tflock"]
  }
  statement {
    actions   = ["s3:DeleteObject"]
    resources = ["arn:aws:s3:::orbit-terraform-state-713285551626/render/staging-database.tfstate.tflock"]
  }
  statement {
    actions   = ["ssm:GetParameter", "ssm:GetParameters"]
    resources = ["arn:aws:ssm:us-east-2:713285551626:parameter/orbit/staging/*"]
  }
}

resource "aws_iam_role_policy" "staging_reseed" {
  role   = aws_iam_role.staging_reseed.id
  policy = data.aws_iam_policy_document.staging_reseed.json
}

output "staging_reseed_role_arn" {
  value = aws_iam_role.staging_reseed.arn
}

resource "aws_iam_role" "production_plan" {
  name               = "orbit-production-plan"
  assume_role_policy = data.aws_iam_policy_document.staging_reseed_trust.json
}

data "aws_iam_policy_document" "production_plan" {
  statement {
    actions   = ["s3:ListBucket"]
    resources = ["arn:aws:s3:::orbit-terraform-state-713285551626"]
    condition {
      test     = "StringEquals"
      variable = "s3:prefix"
      values   = ["render/terraform.tfstate"]
    }
  }
  statement {
    actions   = ["s3:GetObject"]
    resources = ["arn:aws:s3:::orbit-terraform-state-713285551626/render/terraform.tfstate"]
  }
  statement {
    actions   = ["ssm:GetParameter", "ssm:GetParameters", "ssm:GetParametersByPath", "ssm:ListTagsForResource"]
    resources = [for environment in ["production", "staging"] : "arn:aws:ssm:us-east-2:713285551626:parameter/orbit/${environment}/api/*"]
  }
  statement {
    actions   = ["ssm:DescribeParameters"]
    resources = ["*"]
    condition {
      test     = "StringEquals"
      variable = "aws:RequestedRegion"
      values   = ["us-east-2"]
    }
  }
  statement {
    actions   = ["iam:GetUser", "iam:ListAccessKeys", "iam:GetAccessKeyLastUsed"]
    resources = [for name in ["orbit-api-ses", "orbit-api-ses-staging", "orbit-api-uploads-production", "orbit-api-uploads-staging"] : "arn:aws:iam::713285551626:user/${name}"]
  }
  statement {
    actions = [
      "s3:ListBucket", "s3:GetBucketLocation", "s3:GetBucketPolicy", "s3:GetBucketAcl",
      "s3:GetBucketCORS", "s3:GetBucketWebsite", "s3:GetBucketVersioning", "s3:GetAccelerateConfiguration",
      "s3:GetBucketRequestPayment", "s3:GetBucketLogging", "s3:GetLifecycleConfiguration",
      "s3:GetReplicationConfiguration", "s3:GetEncryptionConfiguration", "s3:GetBucketObjectLockConfiguration", "s3:GetBucketTagging",
    ]
    resources = [for environment in ["production", "staging"] : "arn:aws:s3:::orbit-uploads-${environment}-713285551626"]
  }
  statement {
    actions   = ["ses:GetConfigurationSet", "ses:ListTagsForResource"]
    resources = [for stream in ["transactional", "marketing"] : "arn:aws:ses:us-east-2:713285551626:configuration-set/orbit-${stream}"]
  }
  statement {
    actions   = ["sns:GetTopicAttributes", "sns:ListTagsForResource"]
    resources = ["arn:aws:sns:us-east-2:713285551626:orbit-ses-events"]
  }
  statement {
    actions   = ["kms:DescribeKey", "kms:GetKeyPolicy", "kms:GetKeyRotationStatus", "kms:ListResourceTags"]
    resources = [aws_kms_key.ses_events.arn]
  }
  statement {
    actions   = ["kms:Decrypt"]
    resources = ["arn:aws:kms:us-east-2:713285551626:key/*"]
    condition {
      test     = "StringEquals"
      variable = "kms:ViaService"
      values   = ["ssm.us-east-2.amazonaws.com"]
    }
    condition {
      test     = "StringLike"
      variable = "kms:EncryptionContext:PARAMETER_ARN"
      values   = [for environment in ["production", "staging"] : "arn:aws:ssm:us-east-2:713285551626:parameter/orbit/${environment}/api/*"]
    }
  }
}

resource "aws_iam_role_policy" "production_plan" {
  role   = aws_iam_role.production_plan.id
  policy = data.aws_iam_policy_document.production_plan.json
}

output "production_plan_role_arn" {
  value = aws_iam_role.production_plan.arn
}
