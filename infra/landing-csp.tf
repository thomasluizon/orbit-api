locals {
  landing_script_hashes = [
    "'sha256-k1e/+bWIdT8Uk71F22RZrxB9jhVXHo0aDeugvXsMOoI='",
    "'sha256-MgAZSqwvMKKaCDkH6zMaQJGXaZgYMd62+D1j4/g/xGg='",
    "'sha256-VXGu3ijBQHccnEJv6/iI2EPmSvKz/3To4GNktGgsfVM='",
  ]
  landing_style_hashes = [
    "'sha256-ToGJyZnmvAQTDp5OmZMZhqM1qqRRZ0q3GF58fxTZeGA='",
    "'sha256-rjo/oE/zlFf1WPLat5zh4mfQfZuW1LJJoVIsPjvFDZU='",
    "'sha256-hZbgQ3xSIhIik3d8jPnzSZjpKVRyVkUefFOcVHzxpVw='",
  ]
  landing_style_attribute_hashes = [
    "'sha256-G8OlkJCBxpyX1wt0zk9/2k2PJF9fH5GLm3J8/T8buL8='",
    "'sha256-K7aatvhR2cxc1o8M7VBuiC36qi+QyiIbaEjcxiEH84U='",
    "'sha256-Af4cbr19oxIo4Y83I+1ni9EkDJYWS9rDEXWuj7Drk5M='",
    "'sha256-+RPvILNSL8AcrWSaeTg08BEHILtDlXR2B47zZgrB1DA='",
    "'sha256-5/LkdnOEzEX0yKRI9PjMXWYXvJlyF9icTNaUYHYU5hk='",
    "'sha256-1jzk3lFz4l2PXAOUe/L+3H2QN5nFvjOi+LcWNsdneIg='",
    "'sha256-Ws6VRf2/QatDw/v6+Z7cN+fslmD5NrDZWQWmypKE/N8='",
  ]
  landing_csp = {
    for environment, origin in { production = local.production_web_values.API_BASE, staging = local.staging_web_values.API_BASE } : environment => join("; ", [
      "default-src 'self'",
      "script-src 'self' https://challenges.cloudflare.com ${join(" ", local.landing_script_hashes)}",
      "frame-src https://challenges.cloudflare.com",
      "connect-src 'self' ${origin} https://challenges.cloudflare.com",
      "img-src 'self' data:",
      "style-src 'self' ${join(" ", local.landing_style_hashes)}",
      "style-src-attr 'unsafe-hashes' ${join(" ", local.landing_style_attribute_hashes)}",
      "font-src 'self'",
      "object-src 'none'",
      "base-uri 'self'",
      "form-action 'self'",
      "frame-ancestors 'none'",
    ])
  }
}

variable "staging_landing_csp_report_only" {
  description = "Use report-only during the staging console and waitlist validation pass before enforcing the landing policy."
  type        = bool
  default     = false
}
