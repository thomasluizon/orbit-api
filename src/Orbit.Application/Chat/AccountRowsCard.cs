using System.Globalization;
using Orbit.Application.Profile.Queries;
using Orbit.Application.Referrals.Queries;

namespace Orbit.Application.Chat;

public record AccountRow(string Key, string? Value, string ValueType);

public record AccountRowsCard(
    string Kind, IReadOnlyList<AccountRow> Rows,
    string? ReferralCode = null, string? ReferralLink = null,
    string SurfaceId = "profile");

public static class AccountRowsCardBuilder
{
    public const string ProfileDirective = "[[orbit:account:profile]]";
    public const string PlanDirective = "[[orbit:account:plan]]";
    public const string ReferralDirective = "[[orbit:account:referral]]";
    public const string PromptInstruction = "For successful get_profile, get_subscription_overview, or get_referral_overview, write one short line without repeating account figures. Then emit the matching [[orbit:account:profile]], [[orbit:account:plan]], or [[orbit:account:referral]] directive before optional follow-ups. Never mention prices or upgrade actions.";

    public static AccountRowsCard Profile(ProfileResponse profile) =>
        new("profile",
        [
            new("name", profile.Name, "text"),
            new("email", profile.Email, "text"),
            new("language", profile.Language, "enum"),
            new("timeZone", profile.TimeZone, "text"),
            new("weekStartDay", profile.WeekStartDay.ToString(CultureInfo.InvariantCulture), "enum"),
            new("themePreference", profile.ThemePreference, "enum"),
            new("aiSummary", profile.AiSummaryEnabled.ToString().ToLowerInvariant(), "boolean")
        ]);

    public static AccountRowsCard Plan(ProfileResponse profile) =>
        new("plan",
        [
            new("plan", profile.Plan == "free" ? "Free" : "Pro", "enum"),
            new("trialEnd", Iso(profile.TrialEndsAt), "date"),
            new("planExpiry", Iso(profile.PlanExpiresAt), "date"),
            new("interval", profile.SubscriptionInterval, "enum"),
            new("source", profile.SubscriptionSource, "enum"),
            new("lifetime", profile.IsLifetimePro.ToString().ToLowerInvariant(), "boolean"),
            new("astraAllowance", $"{profile.AiMessagesUsed}/{profile.AiMessagesLimit}", "count")
        ]);

    public static AccountRowsCard Referral(ReferralDashboardResponse dashboard) =>
        new("referral",
        [
            new("successfulReferrals", dashboard.Stats.SuccessfulReferrals.ToString(CultureInfo.InvariantCulture), "count"),
            new("pendingReferrals", dashboard.Stats.PendingReferrals.ToString(CultureInfo.InvariantCulture), "count"),
            new("maxReferrals", dashboard.Stats.MaxReferrals.ToString(CultureInfo.InvariantCulture), "count"),
            new("rewardType", dashboard.Stats.RewardType, "enum"),
            new("discountPercent", dashboard.Stats.DiscountPercent.ToString(CultureInfo.InvariantCulture), "count")
        ], dashboard.Code, dashboard.Link);

    private static string? Iso(DateTime? date) => date?.ToString("O", CultureInfo.InvariantCulture);
}
