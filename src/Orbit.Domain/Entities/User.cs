using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class User : Entity
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string? TimeZone { get; private set; }
    public bool AiMemoryEnabled { get; private set; } = true;
    public bool AiSummaryEnabled { get; private set; } = true;
    public bool HasCompletedOnboarding { get; private set; } = false;
    public bool HasDismissedMissions { get; private set; } = false;
    public string? CompletedTours { get; private set; }
    public string? Language { get; private set; }
    public UserPlan Plan { get; private set; } = UserPlan.Free;
    public string? StripeCustomerId { get; private set; }
    public string? StripeSubscriptionId { get; private set; }
    public DateTime? PlanExpiresAt { get; private set; }
    public DateTime? TrialEndsAt { get; private set; }
    public bool IsLifetimePro { get; private set; } = false;
    public int AiMessagesUsedThisMonth { get; private set; } = 0;
    public DateTime? AiMessagesResetAt { get; private set; }
    public SubscriptionInterval? SubscriptionInterval { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public bool HasImportedCalendar { get; private set; } = false;
    public string? GoogleAccessToken { get; private set; }
    public string? GoogleRefreshToken { get; private set; }
    public bool IsDeactivated { get; private set; }
    public DateTime? DeactivatedAt { get; private set; }
    public DateTime? ScheduledDeletionAt { get; private set; }
    public int WeekStartDay { get; private set; } = 1;
    public string? ReferralCode { get; private set; }
    public Guid? ReferredByUserId { get; private set; }
    public int TotalXp { get; private set; } = 0;
    public int Level { get; private set; } = 1;

    [NotMapped]
    public bool IsPro => IsLifetimePro || (Plan == UserPlan.Pro && PlanExpiresAt.HasValue && PlanExpiresAt.Value > DateTime.UtcNow);

    [NotMapped]
    public bool IsTrialActive => !IsPro && TrialEndsAt.HasValue && TrialEndsAt.Value > DateTime.UtcNow;

    [NotMapped]
    public bool HasProAccess => IsPro || IsTrialActive;

    [NotMapped]
    public bool IsYearlyPro => IsPro && (IsLifetimePro || SubscriptionInterval == Enums.SubscriptionInterval.Yearly);

    private User() { }

    public static Result<User> Create(string name, string email)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<User>("Name is required");

        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<User>("Email is required");

        var trimmedEmail = email.Trim();
        if (!EmailRegex.IsMatch(trimmedEmail))
            return Result.Failure<User>("Invalid email format");

        return Result.Success(new User
        {
            Name = name.Trim(),
            Email = trimmedEmail.ToLowerInvariant(),
            CreatedAtUtc = DateTime.UtcNow,
            TrialEndsAt = DateTime.UtcNow.AddDays(7)
        });
    }

    public void UpdateProfile(string name, string email)
    {
        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
    }

    public Result SetTimeZone(string ianaTimeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
            TimeZone = ianaTimeZoneId;
            return Result.Success();
        }
        catch (TimeZoneNotFoundException)
        {
            return Result.Failure($"Invalid timezone: {ianaTimeZoneId}");
        }
    }

    public void ClearTimeZone() => TimeZone = null;

    public void SetAiMemory(bool enabled) => AiMemoryEnabled = enabled;

    public void SetAiSummary(bool enabled) => AiSummaryEnabled = enabled;

    public void SetLanguage(string? language) => Language = language;

    public void CompleteOnboarding() => HasCompletedOnboarding = true;

    public void DismissMissions() => HasDismissedMissions = true;

    public void MarkTourCompleted(string pageName)
    {
        var tours = string.IsNullOrEmpty(CompletedTours)
            ? new HashSet<string>()
            : new HashSet<string>(CompletedTours.Split(','));
        tours.Add(pageName);
        CompletedTours = string.Join(",", tours);
    }

    public void SetStripeCustomerId(string customerId) => StripeCustomerId = customerId;

    public void SetStripeSubscription(string subscriptionId, DateTime expiresAt, SubscriptionInterval? interval = null)
    {
        StripeSubscriptionId = subscriptionId;
        PlanExpiresAt = expiresAt;
        Plan = UserPlan.Pro;
        if (interval.HasValue)
            SubscriptionInterval = interval.Value;
    }

    public void CancelSubscription()
    {
        Plan = UserPlan.Free;
        StripeSubscriptionId = null;
        PlanExpiresAt = null;
        SubscriptionInterval = null;
    }

    public void StartTrial(DateTime endsAt) => TrialEndsAt = endsAt;

    public void IncrementAiMessageCount()
    {
        if (!AiMessagesResetAt.HasValue || AiMessagesResetAt.Value <= DateTime.UtcNow)
        {
            AiMessagesUsedThisMonth = 0;
            AiMessagesResetAt = DateTime.UtcNow.AddDays(30);
        }
        AiMessagesUsedThisMonth++;
    }

    public void SetGoogleTokens(string accessToken, string? refreshToken)
    {
        GoogleAccessToken = accessToken;
        if (refreshToken is not null)
            GoogleRefreshToken = refreshToken;
    }

    public void ClearGoogleTokens()
    {
        GoogleAccessToken = null;
        GoogleRefreshToken = null;
    }

    public void MarkCalendarImported() => HasImportedCalendar = true;

    public void Deactivate(DateTime scheduledDeletion)
    {
        IsDeactivated = true;
        DeactivatedAt = DateTime.UtcNow;
        ScheduledDeletionAt = scheduledDeletion;
    }

    public void CancelDeactivation()
    {
        IsDeactivated = false;
        DeactivatedAt = null;
        ScheduledDeletionAt = null;
    }

    public Result SetWeekStartDay(int day)
    {
        if (day is not (0 or 1))
            return Result.Failure("Week start day must be 0 (Sunday) or 1 (Monday)");

        WeekStartDay = day;
        return Result.Success();
    }

    public void SetReferralCode(string code) => ReferralCode = code;

    public void SetReferredBy(Guid referrerUserId) => ReferredByUserId = referrerUserId;

    public void ExtendTrial(int days)
    {
        if (TrialEndsAt is null || TrialEndsAt.Value < DateTime.UtcNow)
            TrialEndsAt = DateTime.UtcNow.AddDays(days);
        else
            TrialEndsAt = TrialEndsAt.Value.AddDays(days);
    }

    public void AddXp(int amount)
    {
        if (amount <= 0) return;
        TotalXp += amount;
    }

    public void SetLevel(int level)
    {
        if (level < 1 || level > 10) return;
        Level = level;
    }
}
