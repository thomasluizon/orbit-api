---
key: gamification
display_name: XP, Levels, and Achievements
related_capabilities: [gamification.read]
related_surfaces: [gamification]
version: 1
derived_from:
  - src/Orbit.Application/Gamification/Services/GamificationService.cs ProcessHabitLogged
  - src/Orbit.Application/Gamification/Services/GamificationService.cs ProcessGoalCompleted
  - src/Orbit.Application/Gamification/LevelDefinitions.cs All
  - src/Orbit.Application/Gamification/Services/GamificationService.cs CheckConsistencyAchievements
---

# XP, Levels, and Achievements

Gamification rewards consistency with experience points (XP), levels, and achievements. **All of gamification — XP, levels, and achievements — is a Pro feature.** On the free plan no XP is earned and no achievements unlock.

## Earning XP

- **Logging a habit** earns **10 + your current streak** XP. A habit logged on a 5-day streak gives 15 XP; the longer your streak, the more each completion is worth.
- **Completing a goal** earns **+100** XP.
- Unlocking an achievement also grants that achievement's own XP reward on top.

## Levels

Your total XP places you on a level from 1 to 10. The thresholds are:

| Level | Title | XP required |
|---|---|---|
| 1 | Starter | 0 |
| 2 | Explorer | 100 |
| 3 | Orbiter | 300 |
| 4 | Navigator | 600 |
| 5 | Pilot | 1000 |
| 6 | Captain | 1500 |
| 7 | Commander | 2500 |
| 8 | Admiral | 4000 |
| 9 | Elite | 6000 |
| 10 | Legend | 10000 |

Level 10 (Legend) is the top — there is no XP-to-next once you reach it.

## Achievements

Achievements unlock automatically as you hit milestones:

- **Consistency** — streaks of 7, 14, 30, 90, 100, and 365 days.
- **Volume** — 10, 50, 100, 500, and 1000 total completions.
- **Perfect runs** — Perfect Day (every scheduled habit done in a day), then Perfect Week (7 consecutive perfect days) and Perfect Month (30 consecutive perfect days).
- **Time of day** — Early Bird (complete a habit before 7am, 10 times) and Night Owl (after 10pm, 10 times).
- **Comeback** — return and log after 7+ days of inactivity.
- **Bad Habit Breaker** — resist a bad habit for 30 consecutive days.

There are also first-time achievements for creating your first habit and goal, and goal-completion tiers for completing 1, 5, and 10 goals.
