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
    public DateTime CreatedAtUtc { get; private set; }

    [NotMapped]
    public bool IsPro => IsLifetimePro || (Plan == UserPlan.Pro && PlanExpiresAt.HasValue && PlanExpiresAt.Value > DateTime.UtcNow);

    [NotMapped]
    public bool IsTrialActive => TrialEndsAt.HasValue && TrialEndsAt.Value > DateTime.UtcNow;

    [NotMapped]
    public bool HasProAccess => IsPro || IsTrialActive;

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

    public void SetStripeSubscription(string subscriptionId, DateTime expiresAt)
    {
        StripeSubscriptionId = subscriptionId;
        PlanExpiresAt = expiresAt;
        Plan = UserPlan.Pro;
    }

    public void CancelSubscription()
    {
        Plan = UserPlan.Free;
        StripeSubscriptionId = null;
        PlanExpiresAt = null;
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
}
