using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Profile.Models;

/// <summary>
/// Portable snapshot of everything a user owns, returned by the data-export endpoint.
/// Shape is a stable, JSON-friendly projection of the domain entities (LGPD Art. 18 / GDPR Art. 20).
/// Sensitive fields (Google OAuth tokens, Stripe identifiers) are intentionally excluded.
/// </summary>
public sealed record UserDataExport(
    DateTime ExportedAtUtc,
    ExportedAccount Account,
    ExportedSettings Settings,
    ExportedSubscription Subscription,
    IReadOnlyList<ExportedHabit> Habits,
    IReadOnlyList<ExportedGoal> Goals,
    IReadOnlyList<ExportedTag> Tags,
    IReadOnlyList<ExportedUserFact> Facts,
    IReadOnlyList<ExportedNotification> Notifications,
    IReadOnlyList<ExportedChecklistTemplate> ChecklistTemplates,
    IReadOnlyList<ExportedAchievement> Achievements,
    IReadOnlyList<ExportedStreakFreeze> StreakFreezes,
    IReadOnlyList<ExportedReferral> Referrals,
    IReadOnlyList<ExportedApiKey> ApiKeys,
    IReadOnlyList<ExportedFriendship> Friendships,
    IReadOnlyList<ExportedCheer> Cheers,
    IReadOnlyList<ExportedBlockedUser> BlockedUsers,
    IReadOnlyList<ExportedReport> Reports,
    IReadOnlyList<ExportedFriendFeedEvent> FriendFeedEvents);

public sealed record ExportedAccount(
    string Name,
    string Email,
    DateTime CreatedAtUtc,
    string Plan);

public sealed record ExportedSettings(
    string? TimeZone,
    string? Language,
    int WeekStartDay,
    string? ThemePreference,
    string? ColorScheme,
    bool AiMemoryEnabled,
    bool AiSummaryEnabled,
    bool ProactiveAstraEnabled);

public sealed record ExportedHabit(
    Guid Id,
    string Title,
    string? Description,
    string? Emoji,
    bool IsBadHabit,
    bool IsGeneral,
    DateOnly DueDate,
    DateOnly? EndDate,
    string? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<string> Days,
    IReadOnlyList<ChecklistItem> ChecklistItems,
    DateTime CreatedAtUtc,
    IReadOnlyList<ExportedHabitLog> Logs);

public sealed record ExportedHabitLog(
    DateOnly Date,
    decimal Value,
    string? Note,
    DateTime CreatedAtUtc);

public sealed record ExportedGoal(
    Guid Id,
    string Title,
    string? Description,
    decimal TargetValue,
    decimal CurrentValue,
    string Unit,
    string Status,
    string Type,
    DateOnly? Deadline,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<ExportedGoalProgressLog> ProgressLogs);

public sealed record ExportedGoalProgressLog(
    decimal Value,
    decimal PreviousValue,
    string? Note,
    DateTime CreatedAtUtc);

public sealed record ExportedTag(
    Guid Id,
    string Name,
    string Color,
    DateTime CreatedAtUtc);

public sealed record ExportedUserFact(
    string FactText,
    string? Category,
    DateTime ExtractedAtUtc);

public sealed record ExportedSubscription(
    string Plan,
    bool IsLifetimePro,
    string? Source,
    string? Interval,
    DateTime? PlanExpiresAtUtc,
    DateTime? TrialEndsAtUtc);

public sealed record ExportedNotification(
    Guid Id,
    string Title,
    string Body,
    string? Url,
    bool IsRead,
    DateTime CreatedAtUtc);

public sealed record ExportedChecklistTemplate(
    Guid Id,
    string Name,
    IReadOnlyList<string> Items,
    DateTime CreatedAtUtc);

public sealed record ExportedAchievement(
    string AchievementId,
    DateTime EarnedAtUtc);

public sealed record ExportedStreakFreeze(
    DateOnly UsedOnDate,
    DateTime CreatedAtUtc);

public sealed record ExportedReferral(
    string Status,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? RewardGrantedAtUtc);

public sealed record ExportedFriendship(
    Guid RequesterId,
    Guid AddresseeId,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? RespondedAtUtc);

public sealed record ExportedCheer(
    Guid SenderId,
    Guid RecipientId,
    Guid? HabitId,
    string? Note,
    DateTime CreatedAtUtc);

public sealed record ExportedBlockedUser(
    Guid BlockerId,
    Guid BlockedId,
    DateTime CreatedAtUtc);

public sealed record ExportedReport(
    Guid ReportedUserId,
    string Reason,
    string? Details,
    Guid? CheerId,
    string Status,
    DateTime CreatedAtUtc);

public sealed record ExportedFriendFeedEvent(
    string Type,
    int? Value,
    string? AchievementId,
    DateTime CreatedAtUtc);

/// <summary>
/// API key metadata only. The bcrypt secret hash (<c>KeyHash</c>) is intentionally never exported;
/// <c>KeyPrefix</c> is the non-secret display prefix already shown to the owner in the UI.
/// </summary>
public sealed record ExportedApiKey(
    Guid Id,
    string Name,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    bool IsReadOnly,
    DateTime CreatedAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? LastUsedAtUtc,
    bool IsRevoked);
