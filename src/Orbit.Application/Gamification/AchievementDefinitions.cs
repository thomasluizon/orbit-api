using Orbit.Application.Gamification.Models;
using Orbit.Domain.Enums;

namespace Orbit.Application.Gamification;

public static class AchievementDefinitions
{
    // Keys
    public const string FirstOrbit = "first_orbit";
    public const string Liftoff = "liftoff";
    public const string MissionControl = "mission_control";
    public const string WeekWarrior = "week_warrior";
    public const string FortnightFocus = "fortnight_focus";
    public const string MonthlyMaster = "monthly_master";
    public const string QuarterChampion = "quarter_champion";
    public const string Centurion = "centurion";
    public const string YearOfDiscipline = "year_of_discipline";
    public const string GettingMomentum = "getting_momentum";
    public const string BuildingHabits = "building_habits";
    public const string Dedicated = "dedicated";
    public const string Relentless = "relentless";
    public const string LegendaryVolume = "legendary";
    public const string GoalSetter = "goal_setter";
    public const string GoalCrusher = "goal_crusher";
    public const string Overachiever = "overachiever";
    public const string DreamMaker = "dream_maker";
    public const string PerfectDay = "perfect_day";
    public const string PerfectWeek = "perfect_week";
    public const string PerfectMonth = "perfect_month";
    public const string EarlyBird = "early_bird";
    public const string NightOwl = "night_owl";
    public const string Comeback = "comeback";
    public const string BadHabitBreaker = "bad_habit_breaker";

    private static readonly List<AchievementDefinition> _all =
    [
        // Getting Started (3)
        new(FirstOrbit, "First Orbit", "Create your first habit", AchievementCategory.GettingStarted, AchievementRarity.Common, 25, "first_orbit"),
        new(Liftoff, "Liftoff", "Complete your first habit", AchievementCategory.GettingStarted, AchievementRarity.Common, 25, "liftoff"),
        new(MissionControl, "Mission Control", "Create your first goal", AchievementCategory.GettingStarted, AchievementRarity.Common, 25, "mission_control"),
        // Consistency (6)
        new(WeekWarrior, "Week Warrior", "Achieve a 7-day streak on any habit", AchievementCategory.Consistency, AchievementRarity.Uncommon, 50, "week_warrior"),
        new(FortnightFocus, "Fortnight Focus", "Achieve a 14-day streak", AchievementCategory.Consistency, AchievementRarity.Uncommon, 75, "fortnight_focus"),
        new(MonthlyMaster, "Monthly Master", "Achieve a 30-day streak", AchievementCategory.Consistency, AchievementRarity.Rare, 100, "monthly_master"),
        new(QuarterChampion, "Quarter Champion", "Achieve a 90-day streak", AchievementCategory.Consistency, AchievementRarity.Epic, 250, "quarter_champion"),
        new(Centurion, "Centurion", "Achieve a 100-day streak", AchievementCategory.Consistency, AchievementRarity.Epic, 250, "centurion"),
        new(YearOfDiscipline, "Year of Discipline", "Achieve a 365-day streak", AchievementCategory.Consistency, AchievementRarity.Legendary, 500, "year_of_discipline"),
        // Volume (5)
        new(GettingMomentum, "Getting Momentum", "Complete 10 habits total", AchievementCategory.Volume, AchievementRarity.Common, 25, "getting_momentum"),
        new(BuildingHabits, "Building Habits", "Complete 50 habits total", AchievementCategory.Volume, AchievementRarity.Uncommon, 50, "building_habits"),
        new(Dedicated, "Dedicated", "Complete 100 habits total", AchievementCategory.Volume, AchievementRarity.Rare, 100, "dedicated"),
        new(Relentless, "Relentless", "Complete 500 habits total", AchievementCategory.Volume, AchievementRarity.Epic, 250, "relentless"),
        new(LegendaryVolume, "Legendary", "Complete 1000 habits total", AchievementCategory.Volume, AchievementRarity.Legendary, 500, "legendary"),
        // Goals (4)
        new(GoalSetter, "Goal Setter", "Create 3 goals", AchievementCategory.Goals, AchievementRarity.Uncommon, 50, "goal_setter"),
        new(GoalCrusher, "Goal Crusher", "Complete your first goal", AchievementCategory.Goals, AchievementRarity.Uncommon, 75, "goal_crusher"),
        new(Overachiever, "Overachiever", "Complete 5 goals", AchievementCategory.Goals, AchievementRarity.Rare, 150, "overachiever"),
        new(DreamMaker, "Dream Maker", "Complete 10 goals", AchievementCategory.Goals, AchievementRarity.Epic, 250, "dream_maker"),
        // Perfection (3)
        new(PerfectDay, "Perfect Day", "Complete all habits in a day", AchievementCategory.Perfection, AchievementRarity.Uncommon, 50, "perfect_day"),
        new(PerfectWeek, "Perfect Week", "Complete all habits for 7 consecutive days", AchievementCategory.Perfection, AchievementRarity.Rare, 150, "perfect_week"),
        new(PerfectMonth, "Perfect Month", "Complete all habits for 30 consecutive days", AchievementCategory.Perfection, AchievementRarity.Legendary, 500, "perfect_month"),
        // Special (4)
        new(EarlyBird, "Early Bird", "Complete a habit before 7 AM (10 times)", AchievementCategory.Special, AchievementRarity.Rare, 100, "early_bird"),
        new(NightOwl, "Night Owl", "Complete a habit after 10 PM (10 times)", AchievementCategory.Special, AchievementRarity.Rare, 100, "night_owl"),
        new(Comeback, "Comeback", "Resume after 7+ days of inactivity", AchievementCategory.Special, AchievementRarity.Uncommon, 50, "comeback"),
        new(BadHabitBreaker, "Bad Habit Breaker", "Achieve a 30-day streak on a bad habit", AchievementCategory.Special, AchievementRarity.Rare, 150, "bad_habit_breaker"),
    ];

    public static IReadOnlyList<AchievementDefinition> All => _all;

    public static AchievementDefinition? GetById(string id) =>
        _all.FirstOrDefault(a => a.Id == id);
}
