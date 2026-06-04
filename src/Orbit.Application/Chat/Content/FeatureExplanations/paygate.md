---
key: paygate
display_name: Free vs Pro
related_capabilities: [subscriptions.read]
related_surfaces: [subscriptions]
version: 1
derived_from:
  - src/Orbit.Application/Common/PayGateService.cs CanCreateHabits
  - src/Orbit.Application/Common/PayGateService.cs CanSendAiMessage
  - src/Orbit.Application/Common/PayGateService.cs CanUseRetrospective
  - src/Orbit.Application/Common/AppConstants.cs DefaultFreeMaxHabits
---

# Free vs Pro

Orbit has a free plan and a Pro plan. The free plan is fully usable for daily habit tracking; Pro raises the limits and unlocks the advanced features.

## Limits on the free plan

- **Habits** are capped at **10** top-level habits. Sub-habits and soft-deleted habits don't count toward the cap. Pro removes the cap.
- **AI messages** are capped at **20** per month. Pro raises this to **500** per month.

Both plans can also earn a small bonus of extra AI messages from ad rewards, added on top of the plan limit.

## What Pro unlocks

Upgrading to Pro unlocks:

- Goals
- Sub-habits
- The daily AI summary
- AI memory
- Calendar integration
- Premium color schemes
- Streak freezes
- Gamification: XP, levels, and achievements

## The retrospective is yearly-only

The **retrospective** is the one feature that needs the **yearly** Pro plan specifically. A monthly Pro subscription does not include it; the yearly plan does.
