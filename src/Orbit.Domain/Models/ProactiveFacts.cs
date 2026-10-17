using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

public sealed record ProactiveFacts(
    ProactiveTrigger Trigger,
    IReadOnlyList<string> HabitTitles,
    int? HabitCount = null,
    int? CurrentStreak = null,
    bool HasEstablishedStreak = false,
    Guid? GoalId = null,
    string? GoalTitle = null,
    string? TrackingStatus = null,
    int? TimedEventCount = null,
    int? TimedMinutes = null,
    DateOnly? PeriodFrom = null,
    DateOnly? PeriodTo = null,
    int? TotalCompletions = null,
    int? TotalScheduled = null,
    int? BadHabitSlips = null);

public sealed record ProactiveMessage(string Text, string ActionLabel, string ActionPrompt);
