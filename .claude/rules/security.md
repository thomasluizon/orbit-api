---
globs: ["src/Orbit.Api/**"]
description: Security rules -- Stripe, webhooks, headers, CORS, request limits
---

# Security

- **Stripe API key:** Set once globally in `Program.cs` at startup. Never set `StripeConfiguration.ApiKey` per-request in controllers.
- **Webhook verification:** Stripe webhooks MUST verify signatures. Reject if `WebhookSecret` is not configured.
- **Security headers:** `SecurityHeadersMiddleware` adds nosniff, DENY, referrer-policy, XSS headers to all responses.
- **CORS:** Restricted to explicit methods (GET/POST/PUT/DELETE/PATCH) and headers (Authorization, Content-Type). No `AllowAnyHeader()`/`AllowAnyMethod()`.
- **Request size:** 10MB global Kestrel limit. Chat endpoint has its own 10MB multipart limit.
- **Input validation:** Validate Stripe checkout intervals against whitelist. Validate chat history size before JSON deserialization.
