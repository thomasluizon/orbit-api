---
key: freezes
display_name: Streak Freezes
related_capabilities: [gamification.read, gamification.write]
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

You earn **1 freeze for every 7 streak-days** (`StreakDaysPerFreeze` = 7). You can hold up to **3** freezes at once (`MaxStreakFreezesAccumulated` = 3); once you're at the cap, new milestones don't add more until you spend one.

## What a freeze does

A freeze **preserves** your streak across a missed day — it bridges the gap so the next completion continues the run. It does **not** extend or increase the streak; it only stops a missed day from breaking it.

## Activating a freeze

Activation is blocked, and you'll get a clear reason, if any of these are true:

- your current streak is 0 (there's nothing to protect),
- you have no freezes accumulated,
- you've already completed a habit today (no freeze needed),
- you already used a freeze today, or
- you've already used **3** freezes this month (`MaxStreakFreezesPerMonth` = 3).

When a freeze is used, one is consumed from your accumulated balance and the day is marked as frozen.
