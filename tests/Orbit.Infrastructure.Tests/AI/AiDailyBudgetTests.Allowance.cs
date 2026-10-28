using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiDailyBudgetTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MeasuredHeavyMessages_CompleteAllowanceWithCapturedPeakReservations(bool pro, bool image)
    {
        using var fixture = new BudgetFixture(pro, useLocalEstimator: true);
        var peak = await CapturedPeakAsync(fixture, image);
        var messages = pro ? 250 : 5;
        const int callsPerMessage = 8;
        const long microDollarsPerMessage = 20_000;
        var spendLimit = pro ? AppConfigKeys.DefaultProAiMicroDollarsPerWeek : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay;
        var callsLimit = pro ? AppConfigKeys.DefaultProAiCallsPerWeek : AppConfigKeys.DefaultFreeAiCallsPerDay;
        output.WriteLine($"Pro={pro}, image={image}: peak captured reservation={peak.Reservation}, calls/message={callsPerMessage}, settled cost/message={microDollarsPerMessage}, allowance cost={messages * microDollarsPerMessage}, spend cap={spendLimit}, call cap={callsLimit}");

        var dispatched = 0;
        async Task CompleteAsync() => await fixture.Client.RunAsync(fixture.User.Id, _ =>
        {
            dispatched++;
            AiCallContext.Current!.ReportCost(microDollarsPerMessage / callsPerMessage);
            return Task.FromResult(true);
        }, default, peak.Request);

        for (var message = 0; message < messages; message++)
            for (var call = 0; call < callsPerMessage; call++)
                await CompleteAsync();

        using (var read = fixture.Database.CreateContext())
        {
            read.AiBudgetCalls.Count().Should().Be(messages * callsPerMessage);
            read.AiBudgetCalls.Should().OnlyContain(call => call.IsSettled && call.LocalDate == Today);
            read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().Be(messages * microDollarsPerMessage);
        }
        dispatched.Should().Be(messages * callsPerMessage);
        (messages * microDollarsPerMessage + 20_000).Should().BeLessThanOrEqualTo(spendLimit,
            "the measured heavy allowance must leave room for another peak reservation");
        peak.Reservation.Should().BeLessThanOrEqualTo(20_000);
        (messages * callsPerMessage).Should().BeLessThan(callsLimit);
    }

    [Theory]
    [InlineData(false, 150_000L)]
    [InlineData(true, 6_000_000L)]
    public void SafetySpendCaps_MatchMeasuredSizing(bool pro, long expectedLimit)
    {
        var spendLimit = pro ? AppConfigKeys.DefaultProAiMicroDollarsPerWeek : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay;
        spendLimit.Should().Be(expectedLimit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedHeavyCalls_RefuseWhenPeakReservationNoLongerFits(bool pro)
    {
        using var fixture = new BudgetFixture(pro, useLocalEstimator: true);
        var peak = await CapturedPeakAsync(fixture, image: true);
        var spendLimit = pro ? AppConfigKeys.DefaultProAiMicroDollarsPerWeek : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay;
        var callsLimit = pro ? AppConfigKeys.DefaultProAiCallsPerWeek : AppConfigKeys.DefaultFreeAiCallsPerDay;
        const long charge = 20_000 / 8;
        var dispatched = 0;
        async Task CompleteAsync() => await fixture.Client.RunAsync(fixture.User.Id, _ =>
        {
            dispatched++;
            AiCallContext.Current!.ReportCost(charge);
            return Task.FromResult(true);
        }, default, peak.Request);

        while (dispatched * charge + peak.Reservation <= spendLimit)
            await CompleteAsync();

        var blocked = () => CompleteAsync();
        await blocked.Should().ThrowAsync<AiBudgetExceededException>();
        dispatched.Should().BeGreaterThan((pro ? 250 : 5) * 8).And.BeLessThan(callsLimit);
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Count().Should().Be(dispatched);
        read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().Be(dispatched * charge);
        (spendLimit - dispatched * charge).Should().BeLessThan(peak.Reservation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedMeasuredSpend_RefusesNextCapturedReservationBeforeDispatch(bool pro)
    {
        using var fixture = new BudgetFixture(pro, useLocalEstimator: true);
        var peak = await CapturedPeakAsync(fixture, image: true);
        var spendLimit = pro ? AppConfigKeys.DefaultProAiMicroDollarsPerWeek : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay;
        var reservation = await fixture.Budget.ReserveAsync(fixture.User.Id, peak.Reservation, default);
        await fixture.Budget.SettleAsync(reservation, spendLimit, default);
        var dispatched = 0;
        var blocked = () => fixture.Client.RunAsync(fixture.User.Id, _ =>
        {
            dispatched++;
            return Task.FromResult(true);
        }, default, peak.Request);

        await blocked.Should().ThrowAsync<AiBudgetExceededException>();

        dispatched.Should().Be(0);
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Should().ContainSingle().Which.ChargedMicroDollars.Should().Be(spendLimit);
    }

    [Theory]
    [InlineData(false, 110)]
    [InlineData(true, 5_500)]
    public async Task CallCap_RefusesNextCallWhileSpendRemains(bool pro, int callsLimit)
    {
        using var fixture = new BudgetFixture(pro, useLocalEstimator: true);
        var peak = await CapturedPeakAsync(fixture, image: true);
        var dispatched = 0;
        async Task CompleteAsync() => await fixture.Client.RunAsync(fixture.User.Id, _ =>
        {
            dispatched++;
            AiCallContext.Current!.ReportCost(1);
            return Task.FromResult(true);
        }, default, peak.Request);

        for (var call = 0; call < callsLimit; call++)
            await CompleteAsync();

        var blocked = () => CompleteAsync();
        await blocked.Should().ThrowAsync<AiBudgetExceededException>();
        dispatched.Should().Be(callsLimit);
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Count().Should().Be(callsLimit);
        read.AiBudgetCalls.Should().OnlyContain(call => call.IsSettled && call.ChargedMicroDollars == 1);
        var spendLimit = pro ? AppConfigKeys.DefaultProAiMicroDollarsPerWeek : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay;
        (callsLimit + peak.Reservation).Should().BeLessThan(spendLimit);
        callsLimit.Should().Be((pro ? 250 : 5) * (1 + 2 * ChatTurnLimits.MaxToolIterations) * 2);
    }

    private static async Task<(string Request, long Reservation)> CapturedPeakAsync(BudgetFixture fixture, bool image)
    {
        var estimates = new List<(string Request, long Reservation)>();
        foreach (var capture in await CapturedRequestsAsync())
        {
            var request = capture.GetProperty("request").GetRawText();
            if (!image && request.Contains("input_image", StringComparison.Ordinal))
                continue;
            estimates.Add((request, await fixture.Estimator.EstimateMicroDollarsAsync(request, default)));
        }
        var peak = estimates.MaxBy(estimate => estimate.Reservation);
        peak.Request.Contains("input_image", StringComparison.Ordinal).Should().Be(image);
        return peak;
    }

    private static async Task<List<JsonElement>> CapturedRequestsAsync()
    {
        var requests = new List<JsonElement>();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AiReservation"), "*.json.gz")
            .Order(StringComparer.Ordinal))
        {
            using var stream = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            using var fixture = await JsonDocument.ParseAsync(stream);
            requests.Add(fixture.RootElement.Clone());
        }
        requests.Should().HaveCount(9);
        return requests;
    }
}
