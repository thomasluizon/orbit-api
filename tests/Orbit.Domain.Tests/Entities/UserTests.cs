using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class UserTests
{
    private static User CreateValidUser(string name = "Thomas", string email = "thomas@example.com")
    {
        var result = User.Create(name, email);
        return result.Value;
    }

    [Fact]
    public void Create_ValidInput_ReturnsSuccess()
    {
        var result = User.Create("Thomas", "thomas@example.com");

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Thomas");
        result.Value.Email.Should().Be("thomas@example.com");
    }

    [Fact]
    public void Create_EmptyName_ReturnsFailure()
    {
        var result = User.Create("", "thomas@example.com");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name is required");
    }

    [Fact]
    public void Create_WhitespaceName_ReturnsFailure()
    {
        var result = User.Create("   ", "thomas@example.com");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Name is required");
    }

    [Fact]
    public void Create_EmptyEmail_ReturnsFailure()
    {
        var result = User.Create("Thomas", "");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Email is required");
    }

    [Fact]
    public void Create_InvalidEmailFormat_ReturnsFailure()
    {
        var result = User.Create("Thomas", "not-an-email");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid email format");
    }

    [Fact]
    public void Create_TrimsNameAndEmail()
    {
        var result = User.Create("  Thomas  ", "  thomas@example.com  ");

        result.Value.Name.Should().Be("Thomas");
        result.Value.Email.Should().Be("thomas@example.com");
    }

    [Fact]
    public void Create_LowercasesEmail()
    {
        var result = User.Create("Thomas", "Thomas@EXAMPLE.com");

        result.Value.Email.Should().Be("thomas@example.com");
    }

    [Fact]
    public void Create_SetsTrial7Days()
    {
        var before = DateTime.UtcNow.AddDays(7).AddSeconds(-5);
        var user = CreateValidUser();
        var after = DateTime.UtcNow.AddDays(7).AddSeconds(5);

        user.TrialEndsAt.Should().NotBeNull();
        user.TrialEndsAt!.Value.Should().BeOnOrAfter(before);
        user.TrialEndsAt!.Value.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void Create_SetsCreatedAtUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var user = CreateValidUser();
        var after = DateTime.UtcNow.AddSeconds(1);

        user.CreatedAtUtc.Should().BeOnOrAfter(before);
        user.CreatedAtUtc.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void IsPro_LifetimePro_ReturnsTrue()
    {
        var user = CreateValidUser();
        typeof(User).GetProperty("IsLifetimePro")!.SetValue(user, true);

        user.IsPro.Should().BeTrue();
    }

    [Fact]
    public void IsPro_ProPlanNotExpired_ReturnsTrue()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));

        user.IsPro.Should().BeTrue();
    }

    [Fact]
    public void IsPro_ProPlanExpired_ReturnsFalse()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(-1));

        user.IsPro.Should().BeFalse();
    }

    [Fact]
    public void IsPro_FreePlan_ReturnsFalse()
    {
        var user = CreateValidUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));

        user.IsPro.Should().BeFalse();
    }

    [Fact]
    public void IsTrialActive_FutureTrialEnd_ReturnsTrue()
    {
        var user = CreateValidUser();
        user.IsTrialActive.Should().BeTrue();
    }

    [Fact]
    public void IsTrialActive_PastTrialEnd_ReturnsFalse()
    {
        var user = CreateValidUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));

        user.IsTrialActive.Should().BeFalse();
    }

    [Fact]
    public void HasProAccess_ProOrTrial_ReturnsTrue()
    {
        var user = CreateValidUser();
        user.HasProAccess.Should().BeTrue();

        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));
        user.HasProAccess.Should().BeTrue();
    }

    [Fact]
    public void SetTimeZone_ValidIana_Success()
    {
        var user = CreateValidUser();

        var result = user.SetTimeZone("UTC");

        result.IsSuccess.Should().BeTrue();
        user.TimeZone.Should().Be("UTC");
    }

    [Fact]
    public void SetTimeZone_Invalid_ReturnsFailure()
    {
        var user = CreateValidUser();

        var result = user.SetTimeZone("Invalid/Timezone");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid timezone");
    }

    [Fact]
    public void SetStripeSubscription_SetsPlanToPro()
    {
        var user = CreateValidUser();
        var expires = DateTime.UtcNow.AddDays(30);

        user.SetStripeSubscription("sub_123", expires);

        user.Plan.Should().Be(UserPlan.Pro);
        user.StripeSubscriptionId.Should().Be("sub_123");
        user.PlanExpiresAt.Should().Be(expires);
    }

    [Fact]
    public void CancelStripeSubscription_WhenStripeIsSource_ClearsEntitlement()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));

        user.CancelStripeSubscription();

        user.Plan.Should().Be(UserPlan.Free);
        user.StripeSubscriptionId.Should().BeNull();
        user.PlanExpiresAt.Should().BeNull();
        user.SubscriptionSource.Should().BeNull();
    }

    [Fact]
    public void SetStripeSubscription_SetsSourceToStripe()
    {
        var user = CreateValidUser();

        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);

        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
    }

    [Fact]
    public void SetPlaySubscription_SetsPlanProSourceAndToken()
    {
        var user = CreateValidUser();
        var expires = DateTime.UtcNow.AddDays(30);

        user.SetPlaySubscription("play_token_123", expires, SubscriptionInterval.Yearly);

        user.Plan.Should().Be(UserPlan.Pro);
        user.PlayPurchaseToken.Should().Be("play_token_123");
        user.PlanExpiresAt.Should().Be(expires);
        user.SubscriptionSource.Should().Be(SubscriptionSource.GooglePlay);
        user.SubscriptionInterval.Should().Be(SubscriptionInterval.Yearly);
        user.IsPro.Should().BeTrue();
    }

    [Fact]
    public void CancelPlaySubscription_WhenPlayIsSource_ClearsEntitlementAndToken()
    {
        var user = CreateValidUser();
        user.SetPlaySubscription("play_token_123", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);

        user.CancelPlaySubscription();

        user.Plan.Should().Be(UserPlan.Free);
        user.PlayPurchaseToken.Should().BeNull();
        user.SubscriptionSource.Should().BeNull();
        user.SubscriptionInterval.Should().BeNull();
    }

    [Fact]
    public void CancelStripeSubscription_WhenPlayIsActiveSource_KeepsPlayEntitlement()
    {
        var user = CreateValidUser();
        var playExpiry = DateTime.UtcNow.AddDays(20);
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddMonths(6));
        user.SetPlaySubscription("play_token_123", playExpiry, SubscriptionInterval.Monthly);

        user.CancelStripeSubscription();

        user.StripeSubscriptionId.Should().BeNull();
        user.Plan.Should().Be(UserPlan.Pro);
        user.IsPro.Should().BeTrue();
        user.PlayPurchaseToken.Should().Be("play_token_123");
        user.SubscriptionSource.Should().Be(SubscriptionSource.GooglePlay);
        user.PlanExpiresAt.Should().Be(playExpiry);
    }

    [Fact]
    public void CancelStripeSubscription_WhenStripeIsSourceWithLinkedPlayToken_PreservesPlayTokenForRecovery()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddMonths(6));
        user.LinkPlayPurchaseToken("play_token_123");

        user.CancelStripeSubscription();

        user.Plan.Should().Be(UserPlan.Free);
        user.StripeSubscriptionId.Should().BeNull();
        user.SubscriptionSource.Should().BeNull();
        user.PlayPurchaseToken.Should().Be("play_token_123");
    }

    [Fact]
    public void CancelPlaySubscription_WhenStripeIsActiveSource_KeepsStripeEntitlement()
    {
        var user = CreateValidUser();
        var stripeExpiry = DateTime.UtcNow.AddMonths(6);
        user.SetPlaySubscription("play_token_123", DateTime.UtcNow.AddDays(20), SubscriptionInterval.Monthly);
        user.SetStripeSubscription("sub_123", stripeExpiry);

        user.CancelPlaySubscription();

        user.PlayPurchaseToken.Should().BeNull();
        user.Plan.Should().Be(UserPlan.Pro);
        user.IsPro.Should().BeTrue();
        user.StripeSubscriptionId.Should().Be("sub_123");
        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
        user.PlanExpiresAt.Should().Be(stripeExpiry);
    }

    [Fact]
    public void IncrementAiMessageCount_FirstTime_ResetsAndIncrements()
    {
        var user = CreateValidUser();

        user.IncrementAiMessageCount();

        user.AiMessagesUsedThisMonth.Should().Be(1);
        user.AiMessagesResetAt.Should().NotBeNull();
    }

    [Fact]
    public void IncrementAiMessageCount_WithinPeriod_JustIncrements()
    {
        var user = CreateValidUser();
        user.IncrementAiMessageCount();        var resetAt = user.AiMessagesResetAt;

        user.IncrementAiMessageCount();

        user.AiMessagesUsedThisMonth.Should().Be(2);
        user.AiMessagesResetAt.Should().Be(resetAt);    }

    [Fact]
    public void IncrementAiMessageCount_PastReset_ResetsCounter()
    {
        var user = CreateValidUser();
        user.IncrementAiMessageCount();
        user.IncrementAiMessageCount();
        user.AiMessagesUsedThisMonth.Should().Be(2);

        typeof(User).GetProperty("AiMessagesResetAt")!.SetValue(user, DateTime.UtcNow.AddDays(-1));

        user.IncrementAiMessageCount();

        user.AiMessagesUsedThisMonth.Should().Be(1);
    }

    [Fact]
    public void SetReferralCode_SetsCode()
    {
        var user = CreateValidUser();

        user.SetReferralCode("ABC12345");

        user.ReferralCode.Should().Be("ABC12345");
    }

    [Fact]
    public void SetReferredBy_SetsReferrerUserId()
    {
        var user = CreateValidUser();
        var referrerId = Guid.NewGuid();

        user.SetReferredBy(referrerId);

        user.ReferredByUserId.Should().Be(referrerId);
    }

    [Fact]
    public void ExtendTrial_ExpiredTrial_ExtendsFromNow()
    {
        var user = CreateValidUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));

        var before = DateTime.UtcNow.AddDays(10).AddSeconds(-1);

        user.ExtendTrial(10);

        var after = DateTime.UtcNow.AddDays(10).AddSeconds(1);

        user.TrialEndsAt.Should().NotBeNull();
        user.TrialEndsAt!.Value.Should().BeOnOrAfter(before);
        user.TrialEndsAt!.Value.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void ExtendTrial_NullTrial_ExtendsFromNow()
    {
        var user = CreateValidUser();
        typeof(User).GetProperty("TrialEndsAt")!.SetValue(user, null);

        var before = DateTime.UtcNow.AddDays(10).AddSeconds(-1);

        user.ExtendTrial(10);

        var after = DateTime.UtcNow.AddDays(10).AddSeconds(1);

        user.TrialEndsAt.Should().NotBeNull();
        user.TrialEndsAt!.Value.Should().BeOnOrAfter(before);
        user.TrialEndsAt!.Value.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void ExtendTrial_ActiveTrial_ExtendsFromExistingDate()
    {
        var user = CreateValidUser();
        var existingTrialEnd = user.TrialEndsAt!.Value;

        user.ExtendTrial(10);

        user.TrialEndsAt.Should().NotBeNull();
        user.TrialEndsAt!.Value.Should().BeCloseTo(existingTrialEnd.AddDays(10), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AddXp_PositiveAmount_IncrementsTotalXp()
    {
        var user = CreateValidUser();
        user.TotalXp.Should().Be(0);

        user.AddXp(50);

        user.TotalXp.Should().Be(50);
    }

    [Fact]
    public void AddXp_MultipleCalls_Accumulates()
    {
        var user = CreateValidUser();

        user.AddXp(25);
        user.AddXp(75);

        user.TotalXp.Should().Be(100);
    }

    [Fact]
    public void AddXp_ZeroAmount_NoChange()
    {
        var user = CreateValidUser();
        user.AddXp(50);

        user.AddXp(0);

        user.TotalXp.Should().Be(50);
    }

    [Fact]
    public void AddXp_NegativeAmount_NoChange()
    {
        var user = CreateValidUser();
        user.AddXp(50);

        user.AddXp(-10);

        user.TotalXp.Should().Be(50);
    }

    [Fact]
    public void SetLevel_ValidLevel_UpdatesLevel()
    {
        var user = CreateValidUser();
        user.Level.Should().Be(1);

        user.SetLevel(5);

        user.Level.Should().Be(5);
    }

    [Fact]
    public void SetLevel_MaxLevel_UpdatesLevel()
    {
        var user = CreateValidUser();

        user.SetLevel(10);

        user.Level.Should().Be(10);
    }

    [Fact]
    public void SetLevel_BelowMinimum_NoChange()
    {
        var user = CreateValidUser();

        user.SetLevel(0);

        user.Level.Should().Be(1);
    }

    [Fact]
    public void SetLevel_AboveMaximum_NoChange()
    {
        var user = CreateValidUser();

        user.SetLevel(11);

        user.Level.Should().Be(1);
    }

    [Fact]
    public void Create_DefaultXpAndLevel()
    {
        var user = CreateValidUser();

        user.TotalXp.Should().Be(0);
        user.Level.Should().Be(1);
    }

    [Fact]
    public void Create_DefaultStreakFields()
    {
        var user = CreateValidUser();

        user.CurrentStreak.Should().Be(0);
        user.LongestStreak.Should().Be(0);
        user.LastActiveDate.Should().BeNull();
    }

    [Fact]
    public void UpdateStreak_FirstActivity_SetsStreakToOne()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 30);

        user.UpdateStreak(today);

        user.CurrentStreak.Should().Be(1);
        user.LongestStreak.Should().Be(1);
        user.LastActiveDate.Should().Be(today);
    }

    [Fact]
    public void UpdateStreak_ConsecutiveDays_IncrementsStreak()
    {
        var user = CreateValidUser();
        var day1 = new DateOnly(2026, 3, 28);
        var day2 = new DateOnly(2026, 3, 29);
        var day3 = new DateOnly(2026, 3, 30);

        user.UpdateStreak(day1);
        user.UpdateStreak(day2);
        user.UpdateStreak(day3);

        user.CurrentStreak.Should().Be(3);
        user.LongestStreak.Should().Be(3);
        user.LastActiveDate.Should().Be(day3);
    }

    [Fact]
    public void UpdateStreak_GapInDays_ResetsToOne()
    {
        var user = CreateValidUser();
        var day1 = new DateOnly(2026, 3, 28);
        var day3 = new DateOnly(2026, 3, 30);
        user.UpdateStreak(day1);
        user.UpdateStreak(day3);

        user.CurrentStreak.Should().Be(1);
        user.LongestStreak.Should().Be(1);
        user.LastActiveDate.Should().Be(day3);
    }

    [Fact]
    public void UpdateStreak_SameDay_IsIdempotent()
    {
        var user = CreateValidUser();
        var today = new DateOnly(2026, 3, 30);

        user.UpdateStreak(today);
        user.UpdateStreak(today);
        user.UpdateStreak(today);

        user.CurrentStreak.Should().Be(1);
        user.LongestStreak.Should().Be(1);
    }

    [Fact]
    public void UpdateStreak_TracksLongestStreak()
    {
        var user = CreateValidUser();

        for (int i = 0; i < 5; i++)
            user.UpdateStreak(new DateOnly(2026, 3, 1).AddDays(i));

        user.CurrentStreak.Should().Be(5);
        user.LongestStreak.Should().Be(5);

        user.UpdateStreak(new DateOnly(2026, 3, 10));
        user.CurrentStreak.Should().Be(1);
        user.LongestStreak.Should().Be(5);
        user.UpdateStreak(new DateOnly(2026, 3, 11));
        user.UpdateStreak(new DateOnly(2026, 3, 12));
        user.CurrentStreak.Should().Be(3);
        user.LongestStreak.Should().Be(5);    }

    [Fact]
    public void UpdateStreak_FreezeBridge_ContinuesStreak()
    {
        var user = CreateValidUser();
        var day1 = new DateOnly(2026, 3, 28);
        var freezeDay = new DateOnly(2026, 3, 29);
        var day3 = new DateOnly(2026, 3, 30);

        user.UpdateStreak(day1);
        user.CurrentStreak.Should().Be(1);

        user.ApplyStreakFreeze(freezeDay);
        user.CurrentStreak.Should().Be(1);        user.LastActiveDate.Should().Be(freezeDay);

        user.UpdateStreak(day3);
        user.CurrentStreak.Should().Be(2);
        user.LongestStreak.Should().Be(2);
    }

    [Fact]
    public void ApplyStreakFreeze_SetsLastActiveDate_DoesNotChangeStreak()
    {
        var user = CreateValidUser();
        var day1 = new DateOnly(2026, 3, 28);
        var freezeDay = new DateOnly(2026, 3, 29);

        user.UpdateStreak(day1);
        var streakBefore = user.CurrentStreak;
        var longestBefore = user.LongestStreak;

        user.ApplyStreakFreeze(freezeDay);

        user.CurrentStreak.Should().Be(streakBefore);
        user.LongestStreak.Should().Be(longestBefore);
        user.LastActiveDate.Should().Be(freezeDay);
    }

    [Fact]
    public void UpdateStreak_LongerStreakUpdatesLongest()
    {
        var user = CreateValidUser();

        user.UpdateStreak(new DateOnly(2026, 1, 1));
        user.UpdateStreak(new DateOnly(2026, 1, 2));
        user.LongestStreak.Should().Be(2);

        user.UpdateStreak(new DateOnly(2026, 2, 1));

        user.UpdateStreak(new DateOnly(2026, 2, 2));
        user.UpdateStreak(new DateOnly(2026, 2, 3));
        user.CurrentStreak.Should().Be(3);
        user.LongestStreak.Should().Be(3);
    }
}
