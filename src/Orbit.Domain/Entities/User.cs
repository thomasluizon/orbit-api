using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public partial class User : Entity
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmailRegex();

    public string Name { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string? TimeZone { get; private set; }
    public bool AiMemoryEnabled { get; private set; } = true;
    public bool AiSummaryEnabled { get; private set; } = true;
    public bool HasCompletedOnboarding { get; private set; } = false;
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
    public string? ReferralCouponId { get; private set; }
    public int AdRewardBonusMessages { get; private set; } = 0;
    public DateTime? LastAdRewardAt { get; private set; }
    public int AdRewardsClaimedToday { get; private set; } = 0;
    public int CurrentStreak { get; private set; } = 0;
    public int LongestStreak { get; private set; } = 0;
    public DateOnly? LastActiveDate { get; private set; }
    public string? ThemePreference { get; private set; }
    public string? ColorScheme { get; private set; }

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
        if (!EmailRegex().IsMatch(trimmedEmail))
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

    public void SetAiMemory(bool enabled) => AiMemoryEnabled = enabled;

    public void SetAiSummary(bool enabled) => AiSummaryEnabled = enabled;

    public void SetLanguage(string? language) => Language = language;

    public Result SetThemePreference(string? preference)
    {
        if (preference is not null && preference is not ("dark" or "light"))
            return Result.Failure("Invalid theme preference. Must be 'dark' or 'light'.");
        ThemePreference = preference;
        return Result.Success();
    }

    public Result SetColorScheme(string? colorScheme)
    {
        string[] valid = ["purple", "blue", "green", "rose", "orange", "cyan"];
        if (colorScheme is not null && !valid.Contains(colorScheme))
            return Result.Failure("Invalid color scheme.");
        ColorScheme = colorScheme;
        return Result.Success();
    }

    public void CompleteOnboarding() => HasCompletedOnboarding = true;

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
            AdRewardBonusMessages = 0;
            AiMessagesResetAt = DateTime.UtcNow.AddDays(30);
        }
        AiMessagesUsedThisMonth++;
    }

    public Result GrantAdReward(int bonusMessages = 5, int dailyCap = 3)
    {
        if (HasProAccess)
            return Result.Failure("Pro users do not see ads");

        if (!LastAdRewardAt.HasValue || LastAdRewardAt.Value.Date < DateTime.UtcNow.Date)
            AdRewardsClaimedToday = 0;

        if (AdRewardsClaimedToday >= dailyCap)
            return Result.Failure("Daily ad reward limit reached");

        AdRewardBonusMessages += bonusMessages;
        AdRewardsClaimedToday++;
        LastAdRewardAt = DateTime.UtcNow;
        return Result.Success();
    }

    public void SetGoogleTokens(string accessToken, string? refreshToken)
    {
        GoogleAccessToken = accessToken;
        if (refreshToken is not null)
            GoogleRefreshToken = refreshToken;
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

    public void SetReferralCoupon(string? couponId) => ReferralCouponId = couponId;

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

    public void UpdateStreak(DateOnly today)
    {
        if (LastActiveDate == today)
            return; // Already active today, no change

        if (LastActiveDate == today.AddDays(-1))
        {
            // Consecutive day (or freeze covered yesterday) - increment streak
            CurrentStreak++;
        }
        else
        {
            // Streak broken or first activity - reset to 1
            CurrentStreak = 1;
        }

        LastActiveDate = today;

        if (CurrentStreak > LongestStreak)
            LongestStreak = CurrentStreak;
    }

    public void ApplyStreakFreeze(DateOnly today)
    {
        // Bridge the gap - set LastActiveDate so tomorrow's completion continues the streak.
        // Do NOT increment CurrentStreak; freeze preserves, it does not extend.
        LastActiveDate = today;
    }

    /// <summary>
    /// Resets all user profile fields to their default state while preserving
    /// identity, preferences, and subscription data.
    /// </summary>
    public void ResetAccount()
    {
        HasCompletedOnboarding = false;
        TotalXp = 0;
        Level = 1;
        CurrentStreak = 0;
        LongestStreak = 0;
        LastActiveDate = null;
        AiMessagesUsedThisMonth = 0;
        AiMessagesResetAt = null;
        AdRewardBonusMessages = 0;
        AdRewardsClaimedToday = 0;
        LastAdRewardAt = null;
        HasImportedCalendar = false;
        GoogleAccessToken = null;
        GoogleRefreshToken = null;
    }
}
