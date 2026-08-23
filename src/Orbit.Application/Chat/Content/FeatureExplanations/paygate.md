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

- **Habits** are capped at **10** top-level habits. Sub-habits, completed habits, and soft-deleted habits don't count toward the cap. Pro removes the cap.
- **AI messages** are capped at **5** per day. Pro raises this to **50** per day.

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
- Retrospectives
