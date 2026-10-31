#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Common;
using Orbit.Application.Chat;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiDailyBudgetTests(ITestOutputHelper output)
{
    private static readonly DateOnly Today = new(2026, 1, 2);

    [Fact]
    public async Task EvalCeiling_IsSharedAcrossAccountsAndCannotBeBypassedByUnmetered()
    {
        using var fixture = new BudgetFixture(pro: true, evalExempt: true, listed: true);
        var other = User.Create("Other", "other-eval@example.com").Value;
        other.GrantLifetimePro();
        fixture.Database.Context.Users.Add(other);
        fixture.Database.Context.AppFeatureFlagAccounts.Add(AppFeatureFlagAccount.Create(FeatureFlagKeys.AstraEvalUnmetered, other.Id));
        await fixture.Database.Context.SaveChangesAsync();
        fixture.AccountConfiguration[EvalAccountAccess.AccountIdsKey] = $"{fixture.User.Id},{other.Id}";
        await fixture.Budget.ReserveAsync(fixture.User.Id, 500_000, default);

        var blocked = () => fixture.Budget.ReserveAsync(other.Id, 1, default);

        await blocked.Should().ThrowAsync<AiBudgetExceededException>();
        fixture.Locks.Where(key => key.StartsWith("ai-eval-budget:", StringComparison.Ordinal))
            .Should().Equal("ai-eval-budget:2026-01-02", "ai-eval-budget:2026-01-02");
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.01)]
    public async Task EvalCeiling_UsesConfiguredDollarsAndResetsAtUtcMidnight(double dollars)
    {
        using var fixture = new BudgetFixture(pro: true, evalExempt: true, listed: true, evalDailyBudgetUsd: (decimal)dollars);
        fixture.Clock.UtcNow = new DateTimeOffset(2026, 1, 2, 23, 59, 59, TimeSpan.Zero);
        fixture.Dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today.AddDays(1));
        var ceiling = (long)((decimal)dollars * 1_000_000m);
        if (ceiling > 0) await fixture.Budget.ReserveAsync(fixture.User.Id, ceiling, default);
        var blocked = () => fixture.Budget.ReserveAsync(fixture.User.Id, 1, default);
        await blocked.Should().ThrowAsync<AiBudgetExceededException>();
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        if (ceiling == 0)
            await blocked.Should().ThrowAsync<AiBudgetExceededException>();
        else
            (await blocked()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task EvalCeiling_CountsAlreadyReportedUtcUsageDuringRollout()
    {
        using var fixture = new BudgetFixture(pro: true, evalExempt: true, listed: true);
        var row = AiUsageDaily.Create(Today, "gpt-6-luna", "eval_chat", new(1, 0, 0, 0, 0, 0.50m), fixture.User.Id);
        fixture.Database.Context.AiUsageDaily.Add(row);
        await fixture.Database.Context.SaveChangesAsync();

        var blocked = () => fixture.Budget.ReserveAsync(fixture.User.Id, 1, default);
        await blocked.Should().ThrowAsync<AiBudgetExceededException>();
    }

    [Theory]
    [InlineData(1, 0, "2026-08-03", "2026-08-16", true)]
    [InlineData(1, 0, "2026-08-03", "2026-08-16", false)]
    [InlineData(0, 1, "2026-08-02", "2026-08-10", true)]
    [InlineData(0, 1, "2026-08-02", "2026-08-10", false)]
    public async Task WeekStartPreferenceChange_SafetyBudgetRetainsEarlierCallsUntilAlignedBoundary(
        int originalStart, int newStart, string start, string end, bool callsExhausted)
    {
        using var fixture = new BudgetFixture(pro: true);
        var weekStart = DateOnly.Parse(start);
        var periodEnd = DateOnly.Parse(end);
        fixture.User.SetWeekStartDay(originalStart).IsSuccess.Should().BeTrue();
        fixture.User.IncrementAiMessageCount(weekStart);
        fixture.Database.Context.AiBudgetCalls.Add(AiBudgetCall.Reserve(fixture.User.Id, weekStart,
            callsExhausted ? 1 : AppConfigKeys.DefaultProAiMicroDollarsPerWeek));
        fixture.User.SetWeekStartDay(newStart).IsSuccess.Should().BeTrue();
        await fixture.Database.Context.SaveChangesAsync();
        if (callsExhausted)
            fixture.Config.GetAsync(AppConfigKeys.ProAiCallsPerWeek,
                Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);

        var reserve = () => fixture.Budget.ReserveAsync(fixture.User.Id, 6546, default);
        for (var day = 6; day < periodEnd.DayNumber - weekStart.DayNumber; day++)
        {
            fixture.Dates.GetUserTodayAsync(fixture.User.Id, Arg.Any<CancellationToken>()).Returns(weekStart.AddDays(day));
            await reserve.Should().ThrowAsync<AiBudgetExceededException>();
        }

        fixture.Dates.GetUserTodayAsync(fixture.User.Id, Arg.Any<CancellationToken>()).Returns(periodEnd);
        var reservation = await reserve();
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Single(call => call.Id == reservation).LocalDate.Should().Be(periodEnd);
        if (!callsExhausted)
            await fixture.Budget.SettleAsync(reservation, AppConfigKeys.DefaultProAiMicroDollarsPerWeek, default);
        await reserve.Should().ThrowAsync<AiBudgetExceededException>();

        fixture.User.IncrementAiMessageCount(periodEnd);
        await fixture.Database.Context.SaveChangesAsync();
        fixture.Dates.GetUserTodayAsync(fixture.User.Id, Arg.Any<CancellationToken>()).Returns(periodEnd.AddDays(7));
        (await reserve()).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 1, false)]
    public async Task SafetyBudget_ProPersistsUntilWeekStartWhileFreeResetsDaily(bool pro, int weekStartDay, bool callsExhausted)
    {
        using var fixture = new BudgetFixture(pro);
        fixture.User.SetWeekStartDay(weekStartDay).IsSuccess.Should().BeTrue();
        fixture.Database.Context.AiBudgetCalls.Add(AiBudgetCall.Reserve(fixture.User.Id, Today,
            callsExhausted ? 1 : pro ? AppConfigKeys.DefaultProAiMicroDollarsPerWeek : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay));
        await fixture.Database.Context.SaveChangesAsync();
        if (callsExhausted)
            fixture.Config.GetAsync(pro ? AppConfigKeys.ProAiCallsPerWeek : AppConfigKeys.FreeAiCallsPerDay,
                Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        fixture.Dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today.AddDays(1));

        if (pro)
        {
            var blocked = () => fixture.Client.CompleteTextAsync("system", "write");
            await blocked.Should().ThrowAsync<AiBudgetExceededException>();
            fixture.Handler.Completions.Should().Be(0);
            fixture.Dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(new DateOnly(2026, 1, weekStartDay == 0 ? 4 : 5));
        }

        await fixture.Client.CompleteTextAsync("system", "write");

        fixture.Handler.Completions.Should().Be(1);
    }

    [Theory]
    [InlineData("Staging", true, "calls", false)]
    [InlineData("Staging", true, "spend", false)]
    [InlineData("Staging", true, "legacy", false)]
    [InlineData("Staging", true, "reservation", false)]
    [InlineData("Staging", false, "calls", false)]
    [InlineData("Staging", false, "spend", false)]
    [InlineData("Staging", false, "legacy", false)]
    [InlineData("Staging", false, "reservation", false)]
    [InlineData("Production", true, "calls", false)]
    [InlineData("Production", true, "spend", false)]
    [InlineData("Production", true, "legacy", false)]
    [InlineData("Production", true, "reservation", false)]
    [InlineData("Production", true, "calls", true)]
    [InlineData("Production", true, "spend", true)]
    [InlineData("Production", true, "legacy", true)]
    [InlineData("Production", true, "reservation", true)]
    [InlineData("Production", false, "calls", true)]
    [InlineData("Production", false, "spend", true)]
    [InlineData("Production", false, "legacy", true)]
    [InlineData("Production", false, "reservation", true)]
    public async Task EvalUnmetered_SpentBudgetsRequireAnEligibleEnvironmentAndTargetedFlag_UsageStillGrows(
        string environmentName, bool flagged, string guard, bool listed)
    {
        using var fixture = new BudgetFixture(pro: true, evalExempt: flagged, environmentName: environmentName, listed: listed);
        if (guard == "calls")
            fixture.Config.GetAsync(AppConfigKeys.ProAiCallsPerWeek, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        if (guard == "reservation")
            fixture.Config.GetAsync(AppConfigKeys.ProAiMicroDollarsPerWeek, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(1L);
        if (guard != "reservation")
        {
            var spent = AiBudgetCall.Reserve(fixture.User.Id, Today, guard == "calls" ? 1 : AppConfigKeys.DefaultProAiMicroDollarsPerWeek);
            if (guard == "legacy")
            {
                spent.Settle(0);
                fixture.Database.Context.Entry(spent).Property<long>("ChargedTokens").CurrentValue = AppConfigKeys.DefaultProAiMicroDollarsPerWeek / 10;
            }
            fixture.Database.Context.AiBudgetCalls.Add(spent);
            await fixture.Database.Context.SaveChangesAsync();
        }

        if (!flagged || (environmentName != "Staging" && !(environmentName == "Production" && listed)))
        {
            var blocked = () => fixture.Client.CompleteTextAsync("system", "write");
            await blocked.Should().ThrowAsync<AiBudgetExceededException>();
            fixture.Handler.Completions.Should().Be(0);
            using var read = fixture.Database.CreateContext();
            read.AiUsageDaily.Should().BeEmpty();
            return;
        }

        await fixture.Client.CompleteTextAsync("system", "write");
        await fixture.Client.CompleteTextAsync("system", "write again");

        fixture.Handler.Completions.Should().Be(2);
        using var recorded = fixture.Database.CreateContext();
        var usage = recorded.AiUsageDaily.Single();
        usage.UserId.Should().Be(fixture.User.Id);
        usage.Calls.Should().Be(2);
        usage.PromptTokens.Should().Be(39_200);
        usage.CompletionTokens.Should().Be(1_000);
        usage.TotalTokens.Should().Be(40_200);
        usage.CostUsd.Should().Be(0.00329225m);
        recorded.AiBudgetCalls.Count(call => call.IsSettled && call.ChargedMicroDollars > 0).Should().Be(2);
    }

    [Fact]
    public async Task Completion_DoesNotDispatchInputTokenCountRequests()
    {
        using var fixture = new BudgetFixture(useLocalEstimator: true);
        await fixture.Client.CompleteTextAsync("system", "write");
        fixture.Handler.CountedRequests.Should().BeEmpty();
        fixture.Handler.Completions.Should().Be(1);
    }

    [Fact]
    public async Task Timing_RecordsLocalBudgetAndCompletionWithoutTokenCountPhase()
    {
        using var fixture = new BudgetFixture();
        var clock = new CompletionTestClock();
        fixture.Handler.OnCompletion = () => clock.Advance(TimeSpan.FromMilliseconds(13));
        using var timer = ChatTurnTimer.Begin(clock);

        await fixture.Client.CompleteTextAsync("system", "write");

        var timing = timer.Complete();
        timing.Phases.Select(phase => phase.Name).Should().Equal("budget", "budget", "model_call", "budget", "other");
        timing.TotalMilliseconds.Should().Be(13);
        timing.Phases.Sum(phase => phase.Milliseconds).Should().Be(timing.TotalMilliseconds);
        fixture.Handler.CountedRequests.Should().BeEmpty();
        fixture.Handler.Completions.Should().Be(1);
    }

    [Fact]
    public async Task TypicalFreeWrites_FitUnderSpendCeiling()
    {
        const int messages = 5;
        AppConfigKeys.DefaultFreeAiMicroDollarsPerDay.Should().Be(150_000);
        using var fixture = new BudgetFixture();
        for (var call = 0; call < messages * 2; call++)
            await fixture.Client.CompleteTextAsync("system", "a typical write");

        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Count().Should().Be(messages * 2);
        read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().Be(2644 + (messages * 2 - 1) * 649);
        read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().BeLessThan(20_000);
        read.AiUsageDaily.Single().TotalTokens.Should().Be(messages * 2 * 20_100);
        fixture.Handler.Completions.Should().Be(messages * 2);
    }

    [Theory]
    [InlineData(0, "America/Sao_Paulo", "2026-01-04T03:00:00Z")]
    [InlineData(1, "Pacific/Auckland", "2026-01-04T11:00:00Z")]
    [InlineData(1, "America/New_York", "2026-03-09T04:00:00Z")]
    public async Task ProSpendCeiling_ResetsAtNextLocalWeekStart(int weekStartDay, string timeZone, string reset)
    {
        using var fixture = new BudgetFixture(pro: true);
        fixture.User.SetWeekStartDay(weekStartDay).IsSuccess.Should().BeTrue();
        fixture.User.SetTimeZone(timeZone).IsSuccess.Should().BeTrue();
        var resetAtUtc = DateTimeOffset.Parse(reset);
        var now = resetAtUtc.AddSeconds(-1);
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(_ => now);
        var localDates = new UserDateService(Substitute.For<IGenericRepository<User>>(),
            Substitute.For<IDistributedCache>(), clock, Substitute.For<IUnitOfWork>());
        fixture.Dates.GetUserTodayAsync(fixture.User.Id, Arg.Any<CancellationToken>())
            .Returns(_ => localDates.GetUserTodayAsync(fixture.User.TimeZone, fixture.User.Id));
        var localDate = await fixture.Dates.GetUserTodayAsync(fixture.User.Id);
        fixture.Database.Context.AiBudgetCalls.Add(AiBudgetCall.Reserve(fixture.User.Id, localDate, AppConfigKeys.DefaultProAiMicroDollarsPerWeek));
        await fixture.Database.Context.SaveChangesAsync();

        var nextCall = () => fixture.Budget.ReserveAsync(fixture.User.Id, 6546, default);
        await nextCall.Should().ThrowAsync<AiBudgetExceededException>();

        now = resetAtUtc;
        var reservation = await nextCall();

        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Single(call => call.Id == reservation).LocalDate.Should().Be(localDate.AddDays(1));
        (await nextCall()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reservation_SettlesToActualUsageIncludingCacheWritesAndReasoning()
    {
        using var fixture = new BudgetFixture();
        long reserved = 0;
        fixture.Handler.OnCompletion = () =>
        {
            using var read = fixture.Database.CreateContext();
            reserved = read.AiBudgetCalls.Single().ChargedMicroDollars;
        };

        await fixture.Client.CompleteTextAsync("system", "write");

        reserved.Should().Be(3474);
        using var read = fixture.Database.CreateContext();
        var call = read.AiBudgetCalls.Single();
        call.ChargedMicroDollars.Should().Be(2644);
        call.IsSettled.Should().BeTrue();
        read.AiUsageDaily.Single().CostUsd.Should().Be(0.00264375m);
        JsonElement.DeepEquals(fixture.EstimatedRequests.Single().GetProperty("input"),
            fixture.Handler.CompletionRequests.Single().GetProperty("input")).Should().BeTrue();
        fixture.Handler.CountedRequests.Should().BeEmpty();
        fixture.Handler.CompletionRequests.Single().GetProperty("max_output_tokens").GetInt32().Should().Be(2048);
    }

    [Fact]
    public async Task SixCallToolLoops_StopAtConfiguredDollarCeilingBeforeNextCompletion()
    {
        using var fixture = new BudgetFixture(pro: true);
        fixture.Config.GetAsync(AppConfigKeys.ProAiMicroDollarsPerWeek, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(120_000L);
        fixture.Handler.FullToolLoops = true;
        var service = new AiIntentService(fixture.Client, fixture.Recorder, NullLogger<AiIntentService>.Instance);
        var loops = 0;
        while (true)
        {
            var result = await service.SendWithToolsAsync(new AiToolRequest("write", "system", [], fixture.User.Id));
            while (result.IsSuccess && result.Value.HasToolCalls)
                result = await service.ContinueWithToolResultsAsync(result.Value.ConversationContext!,
                    result.Value.ToolCalls!.Select(call => new AiToolCallResult(call.Name, call.Id, true, null, null, null)).ToList());
            if (result.IsFailure)
            {
                result.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
                break;
            }
            loops++;
            loops.Should().BeLessThan(10);
        }

        loops.Should().Be(4, $"provider calls: {fixture.Handler.Completions}");
        fixture.Handler.Completions.Should().Be(26);
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Count().Should().Be(26);
        read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().Be(116_948);
    }

    [Fact]
    public async Task FreeImageMessage_ReservesAndSettlesWithinCeiling()
    {
        using var fixture = new BudgetFixture();
        fixture.Handler.InputTokens = 20_829;
        var service = new AiIntentService(fixture.Client, fixture.Recorder, NullLogger<AiIntentService>.Instance);
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0ckAAAAASUVORK5CYII=");

        var result = await service.SendWithToolsAsync(new AiToolRequest("describe", "system", [], fixture.User.Id,
            image, "image/png"));

        result.IsSuccess.Should().BeTrue();
        fixture.Handler.Completions.Should().Be(1);
        fixture.EstimatedRequests.Single().GetProperty("input").GetRawText().Should().Contain("input_image");
        JsonElement.DeepEquals(fixture.EstimatedRequests.Single().GetProperty("input"),
            fixture.Handler.CompletionRequests.Single().GetProperty("input")).Should().BeTrue();
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Single().ChargedMicroDollars.Should().BeLessThan(20_000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedUser_IsRefusedBeforeModel_WhileOtherUserProceeds(bool callsExhausted)
    {
        using var fixture = new BudgetFixture();
        var row = AiBudgetCall.Reserve(fixture.User.Id, Today, callsExhausted ? 1 : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay);
        fixture.Database.Context.AiBudgetCalls.Add(row);
        await fixture.Database.Context.SaveChangesAsync();
        if (callsExhausted)
            fixture.Config.GetAsync(AppConfigKeys.FreeAiCallsPerDay, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        var blocked = () => fixture.Client.CompleteTextAsync("system", "write");
        await blocked.Should().ThrowAsync<AiBudgetExceededException>();
        fixture.Handler.Completions.Should().Be(0);
        var other = User.Create("Other", "other@example.com").Value;
        other.StartTrial(DateTime.UtcNow.AddDays(-1));
        fixture.Database.Context.Users.Add(other);
        await fixture.Database.Context.SaveChangesAsync();
        fixture.SetUser(other.Id);

        await fixture.Client.CompleteTextAsync("system", "write");

        fixture.Handler.Completions.Should().Be(1);
        using var read = fixture.Database.CreateContext();
        read.AiUsageDaily.Single().UserId.Should().Be(other.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedProvider_ReleasesUnreportedSpendOrSettlesReportedUsage(bool reportedUsage)
    {
        using var fixture = new BudgetFixture();
        fixture.Handler.Fail = true;
        fixture.Handler.ReportFailedUsage = reportedUsage;
        var call = () => fixture.Client.CompleteTextAsync("system", "write");

        await call.Should().ThrowAsync<Exception>();

        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Single().ChargedMicroDollars.Should().Be(reportedUsage ? 2644 : 0);
        read.AiBudgetCalls.Single().IsSettled.Should().BeTrue();
    }

    [Fact]
    public async Task SuccessfulProviderWithoutUsage_RetainsReservation()
    {
        using var fixture = new BudgetFixture();
        fixture.Handler.OmitUsage = true;
        await fixture.Client.CompleteTextAsync("system", "write");
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Single().ChargedMicroDollars.Should().Be(3474);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("sink")]
    [InlineData("truncated")]
    public async Task InterruptedStreamAfterOutput_RetainsConservativeSpend(string interruption)
    {
        using var fixture = new BudgetFixture();
        fixture.Handler.OmitTerminalEvent = true;
        var service = new AiIntentService(fixture.Client, fixture.Recorder, NullLogger<AiIntentService>.Instance);
        using var cancellation = new CancellationTokenSource();
        var deltas = 0;
        var call = () => service.SendWithToolsAsync(new AiToolRequest("write", "system", [], fixture.User.Id),
            update =>
            {
                if (update.Kind == AiStreamEventKind.Delta)
                {
                    deltas++;
                    if (interruption == "cancel")
                        cancellation.Cancel();
                    if (interruption == "sink")
                        throw new IOException("The response sink disconnected.");
                }
                return Task.CompletedTask;
            }, cancellation.Token);

        if (interruption == "cancel")
            await call.Should().ThrowAsync<OperationCanceledException>();
        else
            (await call()).IsFailure.Should().BeTrue();

        deltas.Should().Be(1);
        fixture.Handler.Completions.Should().Be(1);
        using var read = fixture.Database.CreateContext();
        read.AiUsageDaily.Should().BeEmpty();
        var charged = read.AiBudgetCalls.Single();
        charged.ChargedMicroDollars.Should().Be(4498);
        charged.IsSettled.Should().BeTrue();
    }

    [Fact]
    public async Task RepeatedDisconnectedStreams_HitDailyCeiling()
    {
        using var fixture = new BudgetFixture();
        fixture.Config.GetAsync(AppConfigKeys.FreeAiMicroDollarsPerDay, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(20_000L);
        fixture.Handler.OmitTerminalEvent = true;
        var service = new AiIntentService(fixture.Client, fixture.Recorder, NullLogger<AiIntentService>.Instance);
        var deltas = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await service.SendWithToolsAsync(new AiToolRequest("write", "system", [], fixture.User.Id),
                _ =>
                {
                    deltas++;
                    throw new IOException("The response sink disconnected.");
                });
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(ErrorMessages.AiUnavailable.Code);
        }

        var blocked = await service.SendWithToolsAsync(new AiToolRequest("write", "system", [], fixture.User.Id),
            _ => Task.CompletedTask);

        blocked.ErrorCode.Should().Be(AiBudgetExceededException.Error.Code);
        fixture.Handler.Completions.Should().Be(4);
        deltas.Should().Be(4);
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().Be(17_992);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 0)]
    [InlineData(HttpStatusCode.TooManyRequests, 0)]
    [InlineData(HttpStatusCode.InternalServerError, 3474)]
    [InlineData(HttpStatusCode.BadGateway, 3474)]
    [InlineData(HttpStatusCode.GatewayTimeout, 3474)]
    public async Task UnreportedProviderFailure_ChargesUnlessExplicitlyRejected(HttpStatusCode status, long expectedSpend)
    {
        using var fixture = new BudgetFixture();
        fixture.Handler.Fail = true;
        fixture.Handler.FailureStatus = status;
        var call = () => fixture.Client.CompleteTextAsync("system", "write");

        var failure = await call.Should().ThrowAsync<ClientResultException>();

        failure.Which.Status.Should().Be((int)status);
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Single().ChargedMicroDollars.Should().Be(expectedSpend);
    }

    [Fact]
    public async Task FailureBeforeProviderDispatch_ReleasesReservation()
    {
        using var fixture = new BudgetFixture();
        var request = fixture.Client.Inner.SerializeTextRequest("system", "write", 0.7, 8192, AiModelTier.Primary);
        var call = () => fixture.Client.RunAsync<string>(fixture.User.Id,
            _ => Task.FromException<string>(new IOException("The completion was never dispatched.")), default, request);

        await call.Should().ThrowAsync<IOException>();

        fixture.Handler.Completions.Should().Be(0);
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Single().ChargedMicroDollars.Should().Be(0);
    }

    [Fact]
    public async Task DollarConfig_CanRaiseTheCeiling_AndPricesTheRequestedModel()
    {
        using var fixture = new BudgetFixture(model: "gpt-6.1-sol");
        fixture.Config.GetAsync(AppConfigKeys.FreeAiMicroDollarsPerDay, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(20_000L);
        var blocked = () => fixture.Client.CompleteTextAsync("system", "write");
        await blocked.Should().ThrowAsync<AiBudgetExceededException>();
        fixture.Handler.Completions.Should().Be(0);
        fixture.Config.GetAsync(AppConfigKeys.FreeAiMicroDollarsPerDay, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(200_000L);

        await fixture.Client.CompleteTextAsync("system", "write");

        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Single().ChargedMicroDollars.Should().Be(52_875);
    }

    [Fact]
    public async Task LegacyTokenRows_StillCountDuringRollingDeployments()
    {
        using var fixture = new BudgetFixture();
        fixture.Config.GetAsync(AppConfigKeys.FreeAiMicroDollarsPerDay, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(20_000L);
        var legacy = AiBudgetCall.Reserve(fixture.User.Id, Today, 1);
        legacy.Settle(0);
        fixture.Database.Context.AiBudgetCalls.Add(legacy);
        fixture.Database.Context.Entry(legacy).Property<long>("ChargedTokens").CurrentValue = 20_100;
        await fixture.Database.Context.SaveChangesAsync();
        var blocked = () => fixture.Client.CompleteTextAsync("system", "write");

        await blocked.Should().ThrowAsync<AiBudgetExceededException>();

        fixture.Handler.Completions.Should().Be(0);
        using var read = fixture.Database.CreateContext();
        read.Entry(read.AiBudgetCalls.Single()).Property<long>("ChargedTokens").CurrentValue.Should().Be(20_100);
    }

    [Fact]
    public async Task UnknownModelPricing_RefusesBeforeCountingOrCompleting()
    {
        using var fixture = new BudgetFixture(model: "unpriced-model");
        var call = () => fixture.Client.CompleteTextAsync("system", "write");
        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("*pricing*");
        fixture.Handler.CountedRequests.Should().BeEmpty();
        fixture.Handler.Completions.Should().Be(0);
    }

    [Fact]
    public async Task AnonymousUser_IsRefusedBeforeReservationOrCompletion()
    {
        using var fixture = new BudgetFixture();
        fixture.Http.HttpContext!.User = new ClaimsPrincipal();
        var anonymous = () => fixture.Client.CompleteTextAsync("system", "write");
        await anonymous.Should().ThrowAsync<InvalidOperationException>().WithMessage("*authenticated user*");
        fixture.EstimatedRequests.Should().BeEmpty();
        fixture.Handler.Completions.Should().Be(0);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task CompletionShapes_ReserveTheSameSerializedInputAndTools(string kind)
    {
        using var fixture = new BudgetFixture();
        if (kind == "text")
            await fixture.Client.CompleteTextAsync("system", "write", maxOutputTokens: 2048);
        else if (kind == "json")
            await fixture.Client.CompleteJsonAsync<Probe>("system JSON", "write", maxOutputTokens: 2048);
        else
        {
            var declaration = new { name = "noop", description = "noop", parameters = new { type = "object", properties = new { } } };
            var service = new AiIntentService(fixture.Client, fixture.Recorder, NullLogger<AiIntentService>.Instance);
            var result = await service.SendWithToolsAsync(new AiToolRequest("write", "system", [declaration], fixture.User.Id),
                kind == "stream" ? _ => Task.CompletedTask : null);
            result.IsSuccess.Should().BeTrue();
        }
        var counted = fixture.EstimatedRequests.Single();
        var completed = fixture.Handler.CompletionRequests.Single();
        counted.GetProperty("input").GetRawText().Should().Be(completed.GetProperty("input").GetRawText());
        if (kind is "chat" or "stream")
            counted.GetProperty("tools").GetRawText().Should().Be(completed.GetProperty("tools").GetRawText());
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Single().IsSettled.Should().BeTrue();
    }

    private sealed record Probe(string Value);

    private sealed class BudgetFixture : IDisposable
    {
        internal SqliteOrbitDbContextFactory Database { get; } = new();
        internal User User { get; }
        internal IAppConfigService Config { get; } = Substitute.For<IAppConfigService>();
        internal IUserDateService Dates { get; } = Substitute.For<IUserDateService>();
        internal HttpContextAccessor Http { get; } = new() { HttpContext = new DefaultHttpContext() };
        internal ModelHandler Handler { get; }
        internal BudgetedAiCompletionClient Client { get; }
        internal IAiDailyBudget Budget { get; }
        internal IAiUsageRecorder Recorder { get; }
        internal IAiRequestCostEstimator Estimator { get; }
        internal List<JsonElement> EstimatedRequests { get; } = [];
        internal IConfiguration AccountConfiguration => _provider.GetRequiredService<IConfiguration>();
        internal BudgetClock Clock { get; } = new();
        internal List<string> Locks { get; } = [];
        private readonly ServiceProvider _provider;
        private readonly Microsoft.Extensions.Caching.Memory.MemoryCache _estimateCache = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

        internal BudgetFixture(bool pro = false, string model = "gpt-6-luna", int completionTimeoutSeconds = 185,
            bool useLocalEstimator = false, bool evalExempt = false, string environmentName = "Staging", bool listed = false,
            decimal evalDailyBudgetUsd = 0.50m)
        {
            User = User.Create("Writer", "writer@example.com").Value;
            User.StartTrial(DateTime.UtcNow.AddDays(pro ? 1 : -1));
            Database.Context.Users.Add(User);
            if (evalExempt)
                Database.Context.AppFeatureFlagAccounts.Add(AppFeatureFlagAccount.Create(FeatureFlagKeys.AstraEvalUnmetered, User.Id));
            Database.Context.SaveChanges();
            SetUser(User.Id);
            Config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
            Config.GetAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<long>(1));
            var dates = Dates;
            dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
            _provider = new ServiceCollection()
                .AddScoped(_ => Database.CreateContext())
                .AddScoped<IUnitOfWork>(scope =>
                {
                    var real = new UnitOfWork(scope.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings());
                    var work = Substitute.For<IUnitOfWork>();
                    work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Guid>>>(), Arg.Any<CancellationToken>())
                        .Returns(call => real.ExecuteInTransactionAsync(call.Arg<Func<CancellationToken, Task<Guid>>>(), call.Arg<CancellationToken>()));
                    work.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => real.SaveChangesAsync(call.Arg<CancellationToken>()));
                    work.AcquireAdvisoryLockAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
                    {
                        Locks.Add(call.Arg<string>());
                        return real.AcquireAdvisoryLockAsync(call.Arg<string>(), call.Arg<CancellationToken>());
                    });
                    return work;
                })
                .AddSingleton<TimeProvider>(Clock)
                .AddSingleton<IOptions<AiSettings>>(Options.Create(new AiSettings { EvalDailyBudgetUsd = evalDailyBudgetUsd }))
                .AddSingleton<IHostEnvironment>(Host.CreateApplicationBuilder(
                    new HostApplicationBuilderSettings { EnvironmentName = environmentName }).Environment)
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { [EvalAccountAccess.AccountIdsKey] = listed ? User.Id.ToString() : null }).Build())
                .AddMemoryCache().AddScoped<IFeatureFlagService, FeatureFlagService>()
                .AddSingleton(Config).AddSingleton(dates).BuildServiceProvider();
            var options = Options.Create(new AiSettings
            {
                ApiKey = "test-key",
                BaseUrl = "https://orbit.test/v1",
                Model = model,
                MaxRetries = 0,
                CompletionTimeoutSeconds = completionTimeoutSeconds,
                Pricing = new Dictionary<string, AiModelPrice>
                {
                    ["gpt-6-luna"] = new() { InputPerMillionUsd = 0.10m, CachedInputPerMillionUsd = 0.01m, CacheWritePerMillionUsd = 0.125m, OutputPerMillionUsd = 0.50m },
                    ["gpt-6.1-sol"] = new() { InputPerMillionUsd = 2m, CachedInputPerMillionUsd = 0.10m, CacheWritePerMillionUsd = 2.50m, OutputPerMillionUsd = 10m }
                }
            });
            Handler = new ModelHandler(model);
            Recorder = new BudgetAwareAiUsageRecorder(new AiUsageRecorder(_provider.GetRequiredService<IServiceScopeFactory>(),
                options, Substitute.For<IProductAnalytics>(), NullLogger<AiUsageRecorder>.Instance), Http, options);
            var raw = new AiCompletionClient(options, NullLogger<AiCompletionClient>.Instance, Recorder,
                new HttpClientPipelineTransport(new HttpClient(Handler)));
            var estimates = Substitute.For<IAiRequestCostEstimator>();
            estimates.EstimateMicroDollarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                using var request = JsonDocument.Parse(call.Arg<string>());
                EstimatedRequests.Add(request.RootElement.Clone());
                var price = AiRequestCostEstimator.GetPrice(options.Value, request.RootElement.GetProperty("model").GetString()!);
                var inputRate = Math.Max(price.InputPerMillionUsd, price.CacheWritePerMillionUsd ?? price.InputPerMillionUsd);
                return checked((long)decimal.Ceiling(Handler.InputTokens * inputRate
                    + request.RootElement.GetProperty("max_output_tokens").GetInt32() * price.OutputPerMillionUsd));
            });
            Budget = new AiDailyBudget(_provider.GetRequiredService<IServiceScopeFactory>());
            Estimator = useLocalEstimator ? new AiRequestCostEstimator(_estimateCache, options) : estimates;
            Client = new BudgetedAiCompletionClient(raw, Budget,
                Http, NullLogger<BudgetedAiCompletionClient>.Instance, Estimator);
        }

        internal void SetUser(Guid userId) => Http.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));

        public void Dispose()
        {
            _estimateCache.Dispose();
            _provider.Dispose();
            Database.Dispose();
        }
    }

    private sealed class BudgetClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    /// <summary>
    /// Count payload follows TokenCountsResource in the provider schema:
    /// https://github.com/openai/openai-openapi/blob/main/openapi.json
    /// Completion payloads are serialized by the installed Responses SDK.
    /// </summary>
    private sealed class ModelHandler(string model) : HttpMessageHandler
    {
        internal int Completions { get; private set; }
        internal int InputTokens { get; set; } = 19_600;
        internal bool FullToolLoops { get; set; }
        internal bool Fail { get; set; }
        internal HttpStatusCode FailureStatus { get; set; } = HttpStatusCode.ServiceUnavailable;
        internal bool ReportFailedUsage { get; set; }
        internal bool OmitUsage { get; set; }
        internal bool OmitTerminalEvent { get; set; }
        internal Action? OnCompletion { get; set; }
        internal List<JsonElement> CountedRequests { get; } = [];
        internal List<JsonElement> CompletionRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri!.AbsolutePath.EndsWith("/input_tokens", StringComparison.Ordinal))
            {
                CountedRequests.Add(body.RootElement.Clone());
                throw new InvalidOperationException("Completions must not dispatch token-count requests.");
            }
            CompletionRequests.Add(body.RootElement.Clone());
            OnCompletion?.Invoke();
            Completions++;
            if (Fail && !ReportFailedUsage)
                return Response(request, FailureStatus, "{}");
            var response = ResponsesTestData.Response("{\"value\":\"ok\"}", InputTokens, FullToolLoops ? body.RootElement.GetProperty("max_output_tokens").GetInt32() : 500,
                FullToolLoops || Completions == 1 ? 0 : 17_350);
            response.Model = model;
            response.Usage.InputTokenDetails.CacheWriteTokenCount = FullToolLoops ? InputTokens : Completions == 1 ? 17_350 : 0;
            response.Usage.OutputTokenDetails = new ResponseOutputTokenUsageDetails { ReasoningTokenCount = 200 };
            if (Fail)
                response.Status = ResponseStatus.Failed;
            if (OmitUsage)
                response.Usage = null;
            if (FullToolLoops && Completions % 6 != 0)
            {
                response.OutputItems.Clear();
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem($"call_{Completions}", "noop", BinaryData.FromString("{}")));
            }
            var streaming = body.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            if (streaming && OmitTerminalEvent)
                return Response(request, HttpStatusCode.OK, ResponsesTestData.Event(new StreamingResponseOutputTextDeltaUpdate
                {
                    Delta = "done",
                    ItemId = "msg_test",
                    OutputIndex = 0,
                    ContentIndex = 0
                }), "text/event-stream");
            return Response(request, HttpStatusCode.OK, streaming ? ResponsesTestData.Stream(response, "done") : ModelReaderWriter.Write(response).ToString(),
                streaming ? "text/event-stream" : "application/json");
        }

        private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, string content, string mediaType = "application/json")
            => new(status) { RequestMessage = request, Content = new StringContent(content, Encoding.UTF8, mediaType) };
    }
}
