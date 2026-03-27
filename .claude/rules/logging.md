---
globs: ["src/Orbit.Api/**"]
description: Structured logging convention -- format, categories, PascalCase properties
---

# Logging Convention

- All controllers inject `ILogger<T>` and log business events
- Format: `logger.LogInformation("Action {Property}", value)` -- structured properties in PascalCase, English only
- **Auth:** log code sends, login success/failure, Google auth
- **Habits:** log create, delete, bulk operations
- **Tags:** log create, update, delete operations
- **Email:** log send success/failure with status codes (ResendEmailService)
- **Payments:** log checkout creation, webhook events
- **Validation:** log failed fields and endpoint path
- **Profile:** log timezone/language changes
