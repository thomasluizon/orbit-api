using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class DurableDeliveryTests
{
    [Fact]
    public void MarketingDelivery_RejectsMissingIdentityAndDoesNotMoveItsSuccessStamp()
    {
        var dueAtUtc = DateTime.UtcNow;
        FluentActions.Invoking(() => MarketingDelivery.Create(Guid.Empty, Guid.NewGuid(), "recipient@example.com", "en",
            "Subject", "Assunto", "Body", "Corpo", dueAtUtc)).Should().Throw<ArgumentException>();
        var row = MarketingDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "recipient@example.com", "en",
            "Subject", "Assunto", "Body", "Corpo", dueAtUtc);
        FluentActions.Invoking(() => row.MarkSent(dueAtUtc.AddTicks(-1))).Should().Throw<ArgumentException>();
        row.MarkSent(dueAtUtc);
        row.MarkSent(dueAtUtc.AddMinutes(1));
        row.SentAtUtc.Should().Be(dueAtUtc);
    }

    [Fact]
    public void CouponCancellation_RejectsMissingIdentityAndKeepsRetryingBeyondTheInitialBudget()
    {
        FluentActions.Invoking(() => PendingCouponCancellation.Create(Guid.Empty, "coupon")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PendingCouponCancellation.Create(Guid.NewGuid(), "")).Should().Throw<ArgumentException>();
        var row = PendingCouponCancellation.Create(Guid.NewGuid(), "coupon");
        var nowAtUtc = DateTime.UtcNow;
        for (var i = 0; i < 30; i++) row.RecordFailure(nowAtUtc.AddHours(i));
        row.Attempts.Should().Be(30);
        row.NextAttemptAtUtc.Should().Be(nowAtUtc.AddHours(30));
    }

    [Fact]
    public void ReminderPush_RequiresAnOwnerAndUtcDeliveryStamp()
    {
        var row = SentReminder.Create(Guid.NewGuid(), new DateOnly(2026, 1, 1), 0);
        FluentActions.Invoking(() => row.PreparePush(Guid.Empty, "Title", "Body")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => row.MarkPushSent(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)))
            .Should().Throw<ArgumentException>();
        row.PreparePush(Guid.NewGuid(), "Title", "Body");
        row.MarkPushSent(DateTime.UtcNow);
        row.PushSentAtUtc.Should().NotBeNull();
    }
}
