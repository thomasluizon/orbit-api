production_web_digest             = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
staging_web_digest                = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
production_ses_dlq_alert_email    = ""
staging_ses_dlq_alert_email       = ""
production_postgres_ip_allow_list = []

# Set operator addresses in local.tfvars, for example:
# production_postgres_ip_allow_list = [{ cidr_block = "192.0.2.1/32", description = "Operator" }]
# The staging database root accepts this shape only at creation; later access is managed through the Render API.
production_storage_provider = "Supabase"
staging_storage_provider    = "Supabase"
