using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Domain.Entities;

public sealed class ProactiveItem : Entity
{
    public Guid UserId { get; private set; }
    public ProactiveTrigger Trigger { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public string FactsJson { get; private set; } = null!;
    public Guid? GoalId { get; private set; }
    public string Text { get; private set; } = null!;
    public string ActionLabel { get; private set; } = null!;
    public string ActionPrompt { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ShownAtUtc { get; private set; }
    public DateTime? PushedAtUtc { get; private set; }
    public DateTime? OpenedAtUtc { get; private set; }
    public DateTime? ActedAtUtc { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }
    public ProactiveEndReason? EndReason { get; private set; }

    private ProactiveItem() { }

    public static ProactiveItem Create(Guid userId, ProactiveTrigger trigger, DateOnly localDate,
        string factsJson, string text, string actionLabel, string actionPrompt, DateTime createdAtUtc,
        Guid? goalId = null)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User is required.", nameof(userId));
        if (!Enum.IsDefined(trigger)) throw new ArgumentOutOfRangeException(nameof(trigger));
        if (localDate == default) throw new ArgumentException("Local date is required.", nameof(localDate));
        using var facts = JsonDocument.Parse(factsJson);
        if (facts.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Facts must be an object.", nameof(factsJson));
        if (string.IsNullOrWhiteSpace(text) || text.Length > ProactiveRules.MaxTextLength)
            throw new ArgumentException("Text must contain at most 140 characters.", nameof(text));
        if (string.IsNullOrWhiteSpace(actionLabel) || string.IsNullOrWhiteSpace(actionPrompt))
            throw new ArgumentException("An action label and prompt are required.", nameof(actionLabel));
        if (trigger == ProactiveTrigger.GoalBehind && (!goalId.HasValue || goalId == Guid.Empty))
            throw new ArgumentException("A goal is required.", nameof(goalId));
        RequireUtc(createdAtUtc);
        return new ProactiveItem
        {
            UserId = userId,
            Trigger = trigger,
            LocalDate = localDate,
            FactsJson = factsJson,
            Text = text.Trim(),
            ActionLabel = actionLabel.Trim(),
            ActionPrompt = actionPrompt.Trim(),
            CreatedAtUtc = createdAtUtc,
            GoalId = goalId
        };
    }

    public bool Ignored => (ShownAtUtc.HasValue || PushedAtUtc.HasValue)
        && EndedAtUtc.HasValue && !OpenedAtUtc.HasValue && !ActedAtUtc.HasValue;

    public ProactiveEndReason? EvaluateEndReason(DateOnly localDate, int localHour, bool conditionStillTrue)
    {
        if (localDate == default) throw new ArgumentException("Local date is required.", nameof(localDate));
        if (localHour is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(localHour));
        if (EndedAtUtc.HasValue) return EndReason;
        if (ActedAtUtc.HasValue) return ProactiveEndReason.Acted;
        if (LocalDate != localDate) return ProactiveEndReason.DayEnded;
        if (!ProactiveRules.InWindow(Trigger, localHour)) return ProactiveEndReason.WindowClosed;
        return conditionStillTrue ? null : ProactiveEndReason.ConditionResolved;
    }

    public void Show(DateTime nowUtc)
    {
        RequireUtc(nowUtc);
        if (!EndedAtUtc.HasValue) ShownAtUtc ??= nowUtc;
    }

    public void Push(DateTime nowUtc)
    {
        RequireUtc(nowUtc);
        if (!ProactiveRules.MayPush(Trigger)) throw new InvalidOperationException("This trigger cannot push.");
        if (!EndedAtUtc.HasValue) PushedAtUtc ??= nowUtc;
    }

    public void Open(DateTime nowUtc)
    {
        RequireUtc(nowUtc);
        OpenedAtUtc ??= nowUtc;
    }

    public bool Act(DateTime nowUtc)
    {
        RequireUtc(nowUtc);
        ActedAtUtc ??= nowUtc;
        return End(ProactiveEndReason.Acted, nowUtc);
    }

    public bool End(ProactiveEndReason reason, DateTime nowUtc)
    {
        RequireUtc(nowUtc);
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (EndedAtUtc.HasValue) return false;
        if (reason == ProactiveEndReason.ConditionResolved && (ShownAtUtc.HasValue || PushedAtUtc.HasValue))
            ActedAtUtc ??= nowUtc;
        EndedAtUtc = nowUtc;
        EndReason = reason;
        return true;
    }

    private static void RequireUtc(DateTime timestamp)
    {
        if (timestamp.Kind != DateTimeKind.Utc)
            throw new ArgumentException("An UTC timestamp is required.", nameof(timestamp));
    }
}
