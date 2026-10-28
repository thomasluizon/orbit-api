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
    public async Task CapturedCompoundMessages_CompleteAllowanceAndRefuseAbuse(bool pro, bool image)
    {
        using var fixture = new BudgetFixture(pro, useLocalEstimator: true);
        var requests = await CapturedRequestsAsync();
        var estimates = new List<(string Request, long Cost, long Charge)>();
        foreach (var request in requests)
        {
            var capturedJson = request.GetProperty("request").GetRawText();
            if (!image && capturedJson.Contains("input_image", StringComparison.Ordinal))
                continue;
            var cost = await fixture.Estimator.EstimateMicroDollarsAsync(capturedJson, default);
            var providerTokens = request.GetProperty("completion_usage").GetProperty("input_tokens").GetInt64();
            var outputTokens = request.GetProperty("request").GetProperty("max_output_tokens").GetInt32();
            var coldMaximumCost = checked((long)decimal.Ceiling(providerTokens * 0.125m + outputTokens * 0.5m));
            estimates.Add((capturedJson, cost, coldMaximumCost));
        }
        var worst = estimates.MaxBy(estimate => estimate.Cost);
        var requestJson = worst.Request;
        requestJson.Contains("input_image", StringComparison.Ordinal).Should().Be(image);
        var reservation = await fixture.Estimator.EstimateMicroDollarsAsync(requestJson, default);
        var messages = pro ? 250 : 5;
        var callsPerMessage = 1 + 2 * ChatTurnLimits.MaxToolIterations;
        var spendLimit = pro ? AppConfigKeys.DefaultProAiMicroDollarsPerWeek : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay;
        var callsLimit = pro ? AppConfigKeys.DefaultProAiCallsPerWeek : AppConfigKeys.DefaultFreeAiCallsPerDay;
        output.WriteLine($"Pro={pro}, image={image}: peak captured reservation={worst.Cost}, workload reservation={reservation}, calls/message={callsPerMessage}, cold maximum charge={worst.Charge}, allowance upper cost={messages * callsPerMessage * worst.Charge}, spend cap={spendLimit}, call cap={callsLimit}");

        var dispatched = 0;
        async Task CompleteAsync() => await fixture.Client.RunAsync(fixture.User.Id, _ =>
        {
            dispatched++;
            AiCallContext.Current!.ReportCost(worst.Charge);
            return Task.FromResult(true);
        }, default, requestJson);

        for (var message = 0; message < messages; message++)
            for (var call = 0; call < callsPerMessage; call++)
                await CompleteAsync();

        using (var read = fixture.Database.CreateContext())
        {
            read.AiBudgetCalls.Count().Should().Be(messages * callsPerMessage);
            read.AiBudgetCalls.Should().OnlyContain(call => call.IsSettled && call.LocalDate == Today);
            read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().Be(messages * callsPerMessage * worst.Charge);
        }
        (messages * callsPerMessage * worst.Charge + reservation).Should().BeLessThanOrEqualTo(spendLimit / 2,
            "a full allowance must retain at least an equal amount of spend headroom even at maximum output with no cache discount");
        callsLimit.Should().BeGreaterThanOrEqualTo(messages * callsPerMessage * 2);

        for (var call = messages * callsPerMessage; call < callsLimit; call++)
            await CompleteAsync();
        var blocked = () => CompleteAsync();
        await blocked.Should().ThrowAsync<AiBudgetExceededException>();
        dispatched.Should().Be(callsLimit);
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
