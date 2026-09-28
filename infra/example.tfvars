production_web_digest             = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
staging_web_digest                = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
production_email_provider         = "Resend"
staging_email_provider            = "Resend"
production_ses_dlq_alert_email    = ""
staging_ses_dlq_alert_email       = ""
production_postgres_ip_allow_list = []

# Set operator addresses in local.tfvars, for example:
# production_postgres_ip_allow_list = [{ cidr_block = "192.0.2.1/32", description = "Operator" }]
# In the staging database root, use staging_postgres_ip_allow_list with the same object shape.
production_storage_provider = "Supabase"
staging_storage_provider    = "Supabase"
