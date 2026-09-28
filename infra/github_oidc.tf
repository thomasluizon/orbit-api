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
