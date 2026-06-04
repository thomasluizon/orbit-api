---
key: freezes
display_name: Streak Freezes
related_capabilities: [gamification.read]
related_surfaces: [gamification]
version: 1
derived_from:
  - src/Orbit.Domain/Entities/User.cs AwardStreakFreezeIfEligible
  - src/Orbit.Domain/Entities/User.cs ApplyStreakFreeze
  - src/Orbit.Application/Gamification/Commands/ActivateStreakFreezeCommand.cs Handle
  - src/Orbit.Application/Common/AppConstants.cs MaxStreakFreezesAccumulated
---

# Streak Freezes

A streak freeze protects your streak on a day you couldn't complete a habit. **Streak freezes are a Pro feature.**

## Earning freezes

You earn **1 freeze for every 7 streak-days** (`StreakDaysPerFreeze` = 7). You can bank up to **3** freezes at once (`MaxStreakFreezesAccumulated` = 3); once you're at the cap, new milestones don't add more until one is spent.

## How freezes are used

Freezes are **automatic** — there's nothing to tap. When you miss a day on your streak, a banked freeze is spent for you to bridge the gap, so the next completion continues the run instead of starting over. A freeze only **preserves** the streak across a missed day; it does not extend or increase it.

A freeze is spent automatically only when there's a streak worth protecting and you actually missed the day. It won't be used if your current streak is 0, if you already completed a habit that day, or if you've run out of banked freezes. At most **one** freeze is spent per day, and at most **3** are spent per calendar month (`MaxStreakFreezesPerMonth` = 3) — beyond that, a missed day breaks the streak as usual.
