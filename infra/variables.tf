variable "render_owner_id" {
  description = "Render workspace that owns both environments."
  type        = string
  default     = "tea-ctg9ljtumphs73dep1o0"
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

variable "production_postgres_ip_allow_list" {
  description = "Operator addresses allowed to connect to production Postgres. Copy the live Render list into local.tfvars before planning."
  type = list(object({
    cidr_block  = string
    description = string
  }))
  default = []
}

variable "production_storage_provider" {
  description = "Production upload storage provider."
  type        = string
  default     = "Supabase"

  validation {
    condition     = contains(["Supabase", "S3"], var.production_storage_provider)
    error_message = "production_storage_provider must be Supabase or S3."
  }
}

variable "staging_storage_provider" {
  description = "Staging upload storage provider."
  type        = string
  default     = "Supabase"

  validation {
    condition     = contains(["Supabase", "S3"], var.staging_storage_provider)
    error_message = "staging_storage_provider must be Supabase or S3."
  }
}

variable "production_ses_dlq_alert_email" {
  description = "Email recipient for production SES dead-letter queue alarms. An empty value leaves the operations topic unsubscribed."
  type        = string
  default     = ""
}

variable "staging_ses_dlq_alert_email" {
  description = "Email recipient for staging SES dead-letter queue alarms. An empty value leaves the operations topic unsubscribed."
  type        = string
  default     = ""
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
  default     = "/api/health"
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
  default     = ["app-staging.useorbit.org", "staging.useorbit.org"]
}

variable "dns_apex_target" {
  description = "CNAME hostname for the zone apex."
  type        = string
  default     = "orbit-landing-aaa7.onrender.com"

  validation {
    condition     = can(regex("^([a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\.)+[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$", var.dns_apex_target)) && length(var.dns_apex_target) <= 253
    error_message = "dns_apex_target must be a DNS hostname without a trailing dot."
  }
}

variable "dns_www_target" {
  description = "CNAME target for www."
  type        = string
  default     = "2def0cb46b05a9c9.vercel-dns-017.com"
}

variable "dns_app_target" {
  description = "CNAME target for app."
  type        = string
  default     = "853b076627ebd39e.vercel-dns-017.com"
}
