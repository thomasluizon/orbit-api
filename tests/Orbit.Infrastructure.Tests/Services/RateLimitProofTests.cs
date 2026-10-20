using FluentAssertions;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;
using Orbit.RateLimitProof;

namespace Orbit.Infrastructure.Tests.Services;

public class RateLimitProofTests
{
    [Fact]
    public async Task Observer_CountsActualServiceTransactionsAndSaves()
    {
        var observer = new AcquisitionObserver();
        using var factory = new SqliteOrbitDbContextFactory(observer);
        var service = new DistributedRateLimitService(factory.Context, TimeProvider.System);

        await service.TryAcquireAsync("auth", "proof:observer");

        observer.Starts.Should().Be(1);
        observer.Saves.Should().Be(1);
    }

    [Theory]
    [InlineData(null, "disposable", "yes")]
    [InlineData("", "disposable", "yes")]
    [InlineData("secret", null, "yes")]
    [InlineData("secret", " ", "yes")]
    [InlineData("secret", "disposable", null)]
    [InlineData("secret", "disposable", "true")]
    public void Settings_RequireAllDisposableAcknowledgementsWithoutLeakingCredentials(
        string? connection, string? database, string? acknowledgement)
    {
        var parse = () => ProofSettings.Parse(connection, database, acknowledgement);

        parse.Should().Throw<InvalidOperationException>().WithMessage("*disposable*")
            .Which.Message.Should().NotContain("secret");
    }

    [Fact]
    public void Settings_AcceptExplicitAcknowledgementAndRedactPrintedCredentials()
    {
        var settings = ProofSettings.Parse("Host=unused;Password=private-value", "disposable", "yes");

        settings.ConnectionString.Should().Be("Host=unused;Password=private-value");
        settings.ExpectedDatabase.Should().Be("disposable");
        settings.ToString().Should().NotContain("private-value").And.NotContain("Password");
    }

    [Fact]
    public void Timing_ReportsNearestRankTailsAndThroughputForUnorderedSamples()
    {
        var timing = ProofTiming.Measure(TimeSpan.FromSeconds(2),
            Enumerable.Range(1, 20).Reverse().Select(i => TimeSpan.FromMilliseconds(i * 10)));

        timing.Should().Be(new ProofTiming(2000, 100, 190, 200, 10));
    }

    [Fact]
    public void Timing_SingleSampleHasTheSameMedianTailAndMaximum()
    {
        ProofTiming.Measure(TimeSpan.FromSeconds(1), [TimeSpan.FromMilliseconds(250)])
            .Should().Be(new ProofTiming(1000, 250, 250, 250, 1));
    }

    [Fact]
    public void Timing_RejectsMissingMeasurements()
    {
        var measure = () => ProofTiming.Measure(TimeSpan.FromSeconds(1), []);

        measure.Should().Throw<ArgumentOutOfRangeException>();
    }
}
