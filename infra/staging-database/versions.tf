terraform {
  required_version = ">= 1.16.0"

  required_providers {
    render = {
      source  = "render-oss/render"
      version = "= 1.9.1"
    }
  }

  backend "s3" {
    bucket       = "orbit-terraform-state-713285551626"
    key          = "render/staging-database.tfstate"
    region       = "us-east-2"
    encrypt      = true
    use_lockfile = true
  }
}

provider "render" {
  owner_id = var.render_owner_id
}
