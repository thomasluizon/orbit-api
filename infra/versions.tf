terraform {
  required_version = ">= 1.16.0"

  required_providers {
    render = {
      source  = "render-oss/render"
      version = "= 1.9.1"
    }
    aws = {
      source  = "hashicorp/aws"
      version = "= 6.66.0"
    }
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "= 5.26.0"
    }
  }

  backend "s3" {
    bucket       = "orbit-terraform-state-713285551626"
    key          = "render/terraform.tfstate"
    region       = "us-east-2"
    encrypt      = true
    use_lockfile = true
  }
}

provider "render" {
  owner_id = var.render_owner_id
}

provider "aws" {
  region = "us-east-2"
}

provider "cloudflare" {}
