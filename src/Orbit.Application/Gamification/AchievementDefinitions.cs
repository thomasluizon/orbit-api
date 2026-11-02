using Orbit.Application.Gamification.Models;
using Orbit.Domain.Enums;

namespace Orbit.Application.Gamification;

public static class AchievementDefinitions
{
    public const string FirstOrbit = "first_orbit";
    public const string Liftoff = "liftoff";
    public const string MissionControl = "mission_control";
    public const string OnboardingComplete = "onboarding_complete";
    public const string WeekWarrior = "week_warrior";
    public const string FortnightFocus = "fortnight_focus";
    public const string MonthlyMaster = "monthly_master";
    public const string QuarterChampion = "quarter_champion";
    public const string Centurion = "centurion";
    public const string YearOfDiscipline = "year_of_discipline";
    public const string HalfYearHero = "half_year_hero";
    public const string StreakTitan = "streak_titan";
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
    public const string FirstCheer = "first_cheer";
    public const string FirstFriend = "first_friend";
    public const string SquadGoals = "squad_goals";
    public const string Cheerleader = "cheerleader";
    public const string ShowOff = "show_off";
    public const string YearInReview = "year_in_review";
    public const string StreakImmortal = "streak_immortal";
    public const string Unstoppable = "unstoppable";

    private static readonly List<AchievementDefinition> _all =
    [
        new(FirstOrbit, "First Orbit", "Create your first habit", AchievementCategory.GettingStarted, AchievementRarity.Common, 25, "first_orbit", NamePt: "Primeira órbita", DescriptionPt: "Crie seu primeiro hábito"),
        new(Liftoff, "Liftoff", "Complete your first habit", AchievementCategory.GettingStarted, AchievementRarity.Common, 25, "liftoff", NamePt: "Decolagem", DescriptionPt: "Complete seu primeiro hábito"),
        new(MissionControl, "Mission control", "Create your first goal", AchievementCategory.GettingStarted, AchievementRarity.Common, 25, "mission_control", NamePt: "Controle de missão", DescriptionPt: "Crie sua primeira meta"),
        new(OnboardingComplete, "All systems go", "Complete your setup checklist", AchievementCategory.GettingStarted, AchievementRarity.Common, 50, "onboarding_complete", NamePt: "Tudo pronto", DescriptionPt: "Conclua sua lista de configuração"),
        new(WeekWarrior, "Week warrior", "Achieve a 7-day streak on any habit", AchievementCategory.Consistency, AchievementRarity.Uncommon, 50, "week_warrior", ProgressMetric.CurrentStreak, 7, NamePt: "Guerreiro da semana", DescriptionPt: "Alcance uma sequência de 7 dias"),
        new(FortnightFocus, "Fortnight focus", "Achieve a 14-day streak", AchievementCategory.Consistency, AchievementRarity.Uncommon, 75, "fortnight_focus", ProgressMetric.CurrentStreak, 14, NamePt: "Foco quinzenal", DescriptionPt: "Alcance uma sequência de 14 dias"),
        new(MonthlyMaster, "Monthly master", "Achieve a 30-day streak", AchievementCategory.Consistency, AchievementRarity.Rare, 100, "monthly_master", ProgressMetric.CurrentStreak, 30, NamePt: "Mestre mensal", DescriptionPt: "Alcance uma sequência de 30 dias"),
        new(QuarterChampion, "Quarter champion", "Achieve a 90-day streak", AchievementCategory.Consistency, AchievementRarity.Epic, 250, "quarter_champion", ProgressMetric.CurrentStreak, 90, NamePt: "Campeão trimestral", DescriptionPt: "Alcance uma sequência de 90 dias"),
        new(Centurion, "Centurion", "Achieve a 100-day streak", AchievementCategory.Consistency, AchievementRarity.Epic, 250, "centurion", ProgressMetric.CurrentStreak, 100, NamePt: "Centurião", DescriptionPt: "Alcance uma sequência de 100 dias"),
        new(YearOfDiscipline, "Year of discipline", "Achieve a 365-day streak", AchievementCategory.Consistency, AchievementRarity.Legendary, 500, "year_of_discipline", ProgressMetric.CurrentStreak, 365, NamePt: "Ano de disciplina", DescriptionPt: "Alcance uma sequência de 365 dias"),
        new(HalfYearHero, "Half-year hero", "Achieve a 180-day streak", AchievementCategory.Consistency, AchievementRarity.Epic, 350, "half_year_hero", ProgressMetric.CurrentStreak, 180, NamePt: "Herói do semestre", DescriptionPt: "Alcance uma sequência de 180 dias"),
        new(StreakTitan, "Streak titan", "Achieve a 500-day streak", AchievementCategory.Consistency, AchievementRarity.Legendary, 750, "streak_titan", ProgressMetric.CurrentStreak, 500, NamePt: "Titã da sequência", DescriptionPt: "Alcance uma sequência de 500 dias"),
        new(GettingMomentum, "Gaining momentum", "Complete 10 habits total", AchievementCategory.Volume, AchievementRarity.Common, 25, "getting_momentum", ProgressMetric.TotalCompletions, 10, NamePt: "Ganhando ritmo", DescriptionPt: "Complete 10 hábitos no total"),
        new(BuildingHabits, "Building habits", "Complete 50 habits total", AchievementCategory.Volume, AchievementRarity.Uncommon, 50, "building_habits", ProgressMetric.TotalCompletions, 50, NamePt: "Construindo hábitos", DescriptionPt: "Complete 50 hábitos no total"),
        new(Dedicated, "Dedicated", "Complete 100 habits total", AchievementCategory.Volume, AchievementRarity.Rare, 100, "dedicated", ProgressMetric.TotalCompletions, 100, NamePt: "Dedicado", DescriptionPt: "Complete 100 hábitos no total"),
        new(Relentless, "Relentless", "Complete 500 habits total", AchievementCategory.Volume, AchievementRarity.Epic, 250, "relentless", ProgressMetric.TotalCompletions, 500, NamePt: "Imparável", DescriptionPt: "Complete 500 hábitos no total"),
        new(LegendaryVolume, "Legendary", "Complete 1,000 habits total", AchievementCategory.Volume, AchievementRarity.Legendary, 500, "legendary", ProgressMetric.TotalCompletions, 1000, NamePt: "Lendário", DescriptionPt: "Complete 1.000 hábitos no total"),
        new(GoalSetter, "Goal setter", "Create 3 goals", AchievementCategory.Goals, AchievementRarity.Uncommon, 50, "goal_setter", ProgressMetric.GoalsCreated, 3, NamePt: "Metas traçadas", DescriptionPt: "Crie 3 metas"),
        new(GoalCrusher, "Goal crusher", "Complete your first goal", AchievementCategory.Goals, AchievementRarity.Uncommon, 75, "goal_crusher", NamePt: "Meta batida", DescriptionPt: "Complete sua primeira meta"),
        new(Overachiever, "Overachiever", "Complete 5 goals", AchievementCategory.Goals, AchievementRarity.Rare, 150, "overachiever", ProgressMetric.GoalsCompleted, 5, NamePt: "Acima das expectativas", DescriptionPt: "Complete 5 metas"),
        new(DreamMaker, "Dream maker", "Complete 10 goals", AchievementCategory.Goals, AchievementRarity.Epic, 250, "dream_maker", ProgressMetric.GoalsCompleted, 10, NamePt: "Realizador de sonhos", DescriptionPt: "Complete 10 metas"),
        new(PerfectDay, "Perfect day", "Complete all habits in a day", AchievementCategory.Perfection, AchievementRarity.Uncommon, 50, "perfect_day", NamePt: "Dia perfeito", DescriptionPt: "Complete todos os hábitos em um dia"),
        new(PerfectWeek, "Perfect week", "Complete all habits for 7 consecutive days", AchievementCategory.Perfection, AchievementRarity.Rare, 150, "perfect_week", NamePt: "Semana perfeita", DescriptionPt: "Complete todos os hábitos por 7 dias consecutivos"),
        new(PerfectMonth, "Perfect month", "Complete all habits for 30 consecutive days", AchievementCategory.Perfection, AchievementRarity.Legendary, 500, "perfect_month", NamePt: "Mês perfeito", DescriptionPt: "Complete todos os hábitos por 30 dias consecutivos"),
        new(EarlyBird, "Early bird", "Complete a habit before 7 AM (10 times)", AchievementCategory.Special, AchievementRarity.Rare, 100, "early_bird", ProgressMetric.EarlyLogs, 10, NamePt: "Madrugador", DescriptionPt: "Complete um hábito antes das 7h (10 vezes)"),
        new(NightOwl, "Night owl", "Complete a habit after 10 PM (10 times)", AchievementCategory.Special, AchievementRarity.Rare, 100, "night_owl", ProgressMetric.NightLogs, 10, NamePt: "Coruja noturna", DescriptionPt: "Complete um hábito após as 22h (10 vezes)"),
        new(Comeback, "Comeback", "Resume after 7+ days of inactivity", AchievementCategory.Special, AchievementRarity.Uncommon, 50, "comeback", NamePt: "Retorno", DescriptionPt: "Volte depois de 7 dias ou mais sem atividade"),
        new(BadHabitBreaker, "Bad habit breaker", "Achieve a 30-day streak on a bad habit", AchievementCategory.Special, AchievementRarity.Rare, 150, "bad_habit_breaker", NamePt: "Livre do mau hábito", DescriptionPt: "Resista a um mau hábito por 30 dias consecutivos"),
        new(FirstCheer, "Good vibes", "Send or receive your first cheer", AchievementCategory.Special, AchievementRarity.Common, 50, "first_cheer", IsRetired: true),
        new(FirstFriend, "First friend", "Add your first friend", AchievementCategory.Social, AchievementRarity.Common, 50, "first_friend", IsRetired: true),
        new(SquadGoals, "Squad goals", "Reach 5 friends", AchievementCategory.Social, AchievementRarity.Rare, 150, "squad_goals", IsRetired: true),
        new(Cheerleader, "Cheerleader", "Send 25 cheers", AchievementCategory.Social, AchievementRarity.Rare, 150, "cheerleader", IsRetired: true),
        new(ShowOff, "Show off", "Share your first card", AchievementCategory.Sharing, AchievementRarity.Uncommon, 75, "show_off", NamePt: "Exibido", DescriptionPt: "Compartilhe seu primeiro card"),
        new(YearInReview, "Year in review", "View your first Wrapped", AchievementCategory.Sharing, AchievementRarity.Uncommon, 75, "year_in_review", NamePt: "Retrospectiva", DescriptionPt: "Veja seu primeiro Wrapped"),
        new(StreakImmortal, "Streak immortal", "Achieve a 1000-day streak", AchievementCategory.Consistency, AchievementRarity.Legendary, 1500, "streak_immortal", ProgressMetric.CurrentStreak, 1000, NamePt: "Sequência imortal", DescriptionPt: "Alcance uma sequência de 1000 dias"),
        new(Unstoppable, "Unstoppable", "Complete 2500 habits total", AchievementCategory.Volume, AchievementRarity.Legendary, 1000, "unstoppable", ProgressMetric.TotalCompletions, 2500, NamePt: "Imbatível", DescriptionPt: "Complete 2500 hábitos no total"),
    ];

    public static IReadOnlyList<AchievementDefinition> All => _all;
    public static IReadOnlyList<AchievementDefinition> Active { get; } = _all.Where(a => !a.IsRetired).ToList();

    public static AchievementDefinition? GetById(string id) =>
        _all.FirstOrDefault(a => a.Id == id);
}
