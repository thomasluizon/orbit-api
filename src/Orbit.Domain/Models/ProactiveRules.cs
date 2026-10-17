using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

public static class ProactiveRules
{
    public const int RestartLookbackDays = 14;
    public const int StreakMentionMinimum = 2;
    public const int BusyEventMinimum = 4;
    public const int BusyMinutesMinimum = 240;
    public const int GoalCooldownDays = 3;
    public const int IgnoredItemsBeforeRest = 3;
    public const int RestDays = 7;
    public const int WeekDays = 7;
    public const int MaxTextLength = 140;
    public const int QuietStartHour = 22;
    public const int QuietEndHour = 8;
    public const double Temperature = 0.3;

    public const int RestartStartHour = 8;
    public const int RestartEndHour = 12;
    public const int EveningStartHour = 18;
    public const int EveningEndHour = 22;
    public const int BusyCalendarStartHour = 8;
    public const int BusyCalendarEndHour = 11;
    public const int GoalStartHour = 9;
    public const int GoalEndHour = 21;
    public const int ReviewStartHour = 8;
    public const int ReviewEndHour = 21;
    public const int MorningStartHour = 6;
    public const int MorningEndHour = 12;

    public static (int Start, int End) Window(ProactiveTrigger trigger) => trigger switch
    {
        ProactiveTrigger.RestartAfterMiss => (RestartStartHour, RestartEndHour),
        ProactiveTrigger.EveningCheckin => (EveningStartHour, EveningEndHour),
        ProactiveTrigger.BusyCalendarDay => (BusyCalendarStartHour, BusyCalendarEndHour),
        ProactiveTrigger.GoalBehind => (GoalStartHour, GoalEndHour),
        ProactiveTrigger.WeeklyReview => (ReviewStartHour, ReviewEndHour),
        ProactiveTrigger.MorningPlan => (MorningStartHour, MorningEndHour),
        _ => throw new ArgumentOutOfRangeException(nameof(trigger))
    };

    public static bool InWindow(ProactiveTrigger trigger, int hour)
    {
        var (start, end) = Window(trigger);
        return hour >= start && hour < end;
    }

    public static bool MayPush(ProactiveTrigger trigger) => trigger is
        ProactiveTrigger.RestartAfterMiss or ProactiveTrigger.EveningCheckin or ProactiveTrigger.BusyCalendarDay;

    public static bool IsQuiet(int hour) => hour >= QuietStartHour || hour < QuietEndHour;
}
