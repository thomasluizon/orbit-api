using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class MarketingDeliveryTests
{
    [Fact]
    public void RecordDispatch_PreservesTheJobAndDefersRecoveryPastItsSchedule()
    {
        var scheduledAtUtc = DateTime.UtcNow.AddHours(1);
        var delivery = Create(scheduledAtUtc);

        delivery.RecordDispatch("101", DateTime.UtcNow);

        delivery.BackgroundJobId.Should().Be("101");
        delivery.NextRecoveryAtUtc.Should().Be(scheduledAtUtc.AddMinutes(5));
        delivery.SentAtUtc.Should().BeNull();
    }

    [Fact]
    public void DeferRecovery_ReschedulesTheCheckWithoutLosingTheOutstandingJob()
    {
        var nowAtUtc = DateTime.UtcNow;
        var delivery = Create(nowAtUtc.AddMinutes(-1));
        delivery.RecordDispatch("101", nowAtUtc);

        delivery.DeferRecovery(nowAtUtc.AddMinutes(10));

        delivery.BackgroundJobId.Should().Be("101");
        delivery.NextRecoveryAtUtc.Should().Be(nowAtUtc.AddMinutes(15));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RecordDispatch_RejectsAnEmptyJobId(string? jobId)
    {
        var delivery = Create(DateTime.UtcNow);
        Action record = () => delivery.RecordDispatch(jobId!, DateTime.UtcNow);
        record.Should().Throw<ArgumentException>();
        delivery.BackgroundJobId.Should().BeNull();
    }

    [Fact]
    public void RecordDispatch_RejectsANonUtcCheckBeforeChangingState()
    {
        var delivery = Create(DateTime.UtcNow);
        var originalAtUtc = delivery.NextRecoveryAtUtc;
        Action record = () => delivery.RecordDispatch("101", DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
        record.Should().Throw<ArgumentException>();
        delivery.BackgroundJobId.Should().BeNull();
        delivery.NextRecoveryAtUtc.Should().Be(originalAtUtc);
    }

    private static MarketingDelivery Create(DateTime notBeforeAtUtc) => MarketingDelivery.Create(
        Guid.NewGuid(), Guid.NewGuid(), "recipient@example.com", "en", "Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", notBeforeAtUtc);
}
