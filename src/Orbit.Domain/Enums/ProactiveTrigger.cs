namespace Orbit.Domain.Enums;

public enum ProactiveTrigger
{
    RestartAfterMiss = 1,
    EveningCheckin = 2,
    BusyCalendarDay = 3,
    GoalBehind = 4,
    WeeklyReview = 5,
    MorningPlan = 6
}

public enum ProactiveEndReason
{
    WindowClosed,
    ConditionResolved,
    Acted,
    DayEnded
}
