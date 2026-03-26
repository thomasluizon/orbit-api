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
        // Clear trial so HasProAccess doesn't confuse things
        user.StartTrial(DateTime.UtcNow.AddDays(-1));

        user.IsPro.Should().BeFalse();
    }

    [Fact]
    public void IsTrialActive_FutureTrialEnd_ReturnsTrue()
    {
        var user = CreateValidUser();
        // Fresh user has 7-day trial
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
        // Fresh user has active trial
        user.HasProAccess.Should().BeTrue();

        // Also true when Pro
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
    public void MarkTourCompleted_AddsTour()
    {
        var user = CreateValidUser();

        user.MarkTourCompleted("today");

        user.CompletedTours.Should().Contain("today");
    }

    [Fact]
    public void MarkTourCompleted_Duplicate_NoDuplicates()
    {
        var user = CreateValidUser();

        user.MarkTourCompleted("today");
        user.MarkTourCompleted("today");

        user.CompletedTours!.Split(',').Where(t => t == "today").Should().HaveCount(1);
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
    public void CancelSubscription_RevertsFree_ClearsFields()
    {
        var user = CreateValidUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));

        user.CancelSubscription();

        user.Plan.Should().Be(UserPlan.Free);
        user.StripeSubscriptionId.Should().BeNull();
        user.PlanExpiresAt.Should().BeNull();
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
        user.IncrementAiMessageCount(); // sets reset to +30 days
        var resetAt = user.AiMessagesResetAt;

        user.IncrementAiMessageCount();

        user.AiMessagesUsedThisMonth.Should().Be(2);
        user.AiMessagesResetAt.Should().Be(resetAt); // unchanged
    }

    [Fact]
    public void IncrementAiMessageCount_PastReset_ResetsCounter()
    {
        var user = CreateValidUser();
        user.IncrementAiMessageCount();
        user.IncrementAiMessageCount();
        user.AiMessagesUsedThisMonth.Should().Be(2);

        // Force reset date to past via reflection
        typeof(User).GetProperty("AiMessagesResetAt")!.SetValue(user, DateTime.UtcNow.AddDays(-1));

        user.IncrementAiMessageCount();

        // Counter was reset (0) then incremented to 1
        user.AiMessagesUsedThisMonth.Should().Be(1);
    }

    // ----- Referral Methods -----

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

    // --- XP / Level tests ---

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
}
