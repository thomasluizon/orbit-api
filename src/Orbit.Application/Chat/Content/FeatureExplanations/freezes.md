---
key: freezes
display_name: Streak Freezes
related_capabilities: [gamification.read]
related_surfaces: [gamification]
version: 3
derived_from:
  - src/Orbit.Domain/Entities/User.cs AwardStreakFreezeIfEligible
  - src/Orbit.Domain/Entities/User.cs ConsumeStreakFreeze
  - src/Orbit.Application/Gamification/Commands/RepairStreakCommand.cs Handle
  - src/Orbit.Infrastructure/Services/StreakFreezeAutoActivationService.cs ActivateMissedDayFreezes
  - src/Orbit.Application/Common/AppConstants.cs MaxStreakFreezesAccumulated
---

# Streak Freezes

A streak freeze protects your streak on a day you couldn't complete a habit. **Streak freezes are a Pro feature.**

## Earning freezes

You earn **1 freeze for every 7 streak-days** (`StreakDaysPerFreeze` = 7). You can bank up to **3** freezes at once (`MaxStreakFreezesAccumulated` = 3); once you're at the cap, new milestones don't add more until one is spent.

## How freezes are used

Orbit automatically spends one banked freeze when an eligible scheduled day is missed. The covered date and remaining bank are reported so the spend is visible. A freeze only **preserves** the streak across the missed day; it does not extend or increase it.

If automatic coverage did not cover a gap, a manual repair can be offered for local yesterday when it was scheduled, remains incomplete and unfrozen, and covering it restores a streak that would otherwise be broken. At most **one** freeze is spent per day, and at most **3** are spent per calendar month (`MaxStreakFreezesPerMonth` = 3). Logging the covered day later does not refund the freeze.
