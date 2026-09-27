variable "render_owner_id" {
  description = "Render workspace that owns both environments."
  type        = string
  default     = "tea-ctg9ljtumphs73dep1o0"
}

variable "api_auto_deploy" {
  description = "Keep production API deployment on each main commit until release workflows own it."
  type        = bool
  default     = true
}

variable "api_database" {
  description = "Production API database source during the data migration."
  type        = string
  default     = "supabase"

  validation {
    condition     = contains(["supabase", "render"], var.api_database)
    error_message = "api_database must be supabase or render."
  }
}

variable "production_web_digest" {
  description = "Published GHCR digest for the production web image."
  type        = string

  validation {
    condition     = can(regex("^sha256:[0-9a-f]{64}$", var.production_web_digest))
    error_message = "production_web_digest must be a sha256 image digest."
  }
}

variable "staging_web_digest" {
  description = "Published GHCR digest for the staging web image."
  type        = string

  validation {
    condition     = can(regex("^sha256:[0-9a-f]{64}$", var.staging_web_digest))
    error_message = "staging_web_digest must be a sha256 image digest."
  }
}

variable "web_health_check_path" {
  description = "Health endpoint supplied by the web image ticket."
  type        = string
  default     = "/"
}

variable "web_custom_domains" {
  description = "Production web custom domains after cutover."
  type        = set(string)
  default     = []
}

variable "landing_custom_domains" {
  description = "Landing custom domains after cutover."
  type        = set(string)
  default     = []
}

variable "staging_api_custom_domains" {
  description = "Staging API custom domains."
  type        = set(string)
  default     = ["api-staging.useorbit.org"]
}

variable "staging_web_custom_domains" {
  description = "Staging web custom domains."
  type        = set(string)
  default     = ["staging.useorbit.org"]
}
