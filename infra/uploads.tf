locals {
  upload_environments = {
    production = "https://app.useorbit.org"
    staging    = "https://staging.useorbit.org"
  }
}

resource "aws_s3_bucket" "uploads" {
  for_each = local.upload_environments
  bucket   = "orbit-uploads-${each.key}-713285551626"
}

resource "aws_s3_bucket_public_access_block" "uploads" {
  for_each                = local.upload_environments
  bucket                  = aws_s3_bucket.uploads[each.key].id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "uploads" {
  for_each = local.upload_environments
  bucket   = aws_s3_bucket.uploads[each.key].id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_cors_configuration" "uploads" {
  for_each = local.upload_environments
  bucket   = aws_s3_bucket.uploads[each.key].id

  cors_rule {
    allowed_headers = ["Content-Type"]
    allowed_methods = ["PUT", "GET"]
    allowed_origins = [each.value]
    max_age_seconds = 300
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "uploads" {
  for_each = local.upload_environments
  bucket   = aws_s3_bucket.uploads[each.key].id

  rule {
    id     = "abort-incomplete-uploads"
    status = "Enabled"
    filter {}

    abort_incomplete_multipart_upload {
      days_after_initiation = 1
    }
  }
}

resource "aws_iam_user" "uploads" {
  for_each = local.upload_environments
  name     = "orbit-api-uploads-${each.key}"
}

resource "aws_iam_user_policy" "uploads" {
  for_each = local.upload_environments
  name     = "uploads-object-access"
  user     = aws_iam_user.uploads[each.key].name
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["s3:PutObject", "s3:GetObject"]
      Resource = "${aws_s3_bucket.uploads[each.key].arn}/*"
    }]
  })
}

resource "aws_iam_access_key" "uploads" {
  for_each = local.upload_environments
  user     = aws_iam_user.uploads[each.key].name
}

resource "aws_ssm_parameter" "uploads_access_key_id" {
  for_each = local.upload_environments
  name     = "/orbit/${each.key}/api/Storage__S3__AccessKeyId"
  type     = "SecureString"
  value    = aws_iam_access_key.uploads[each.key].id
}

resource "aws_ssm_parameter" "uploads_secret_access_key" {
  for_each = local.upload_environments
  name     = "/orbit/${each.key}/api/Storage__S3__SecretAccessKey"
  type     = "SecureString"
  value    = aws_iam_access_key.uploads[each.key].secret
}
