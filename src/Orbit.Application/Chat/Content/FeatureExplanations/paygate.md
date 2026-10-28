---
key: paygate
display_name: Free vs Pro
related_capabilities: [subscriptions.read]
related_surfaces: [subscriptions]
version: 1
derived_from:
  - src/Orbit.Application/Common/PayGateService.cs CanCreateHabits
  - src/Orbit.Application/Common/PayGateService.cs TryConsumeAiMessage
  - src/Orbit.Application/Common/PayGateService.cs CanUseRetrospective
  - src/Orbit.Application/Common/AppConstants.cs DefaultFreeMaxHabits
---

# Free vs Pro

Orbit has a free plan and a Pro plan. The free plan is fully usable for daily habit tracking; Pro raises the AI message limit and unlocks advanced features. Sub-habits are available on every plan.

## Limits on the free plan

- **Habits** have an abuse guard of **1000** live top-level habits on both the free and Pro plans. Sub-habits, completed habits, and soft-deleted habits don't count toward the guard.
- **AI messages** are capped at **5** per day. Pro includes **250** per local calendar week, resetting at midnight on the person's chosen week start day in their time zone.

## What Pro unlocks

Upgrading to Pro unlocks:

- The daily AI summary
- Calendar integration
- Retrospectives
