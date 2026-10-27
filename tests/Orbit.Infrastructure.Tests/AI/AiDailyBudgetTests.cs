#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Common;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.AI;

public class AiDailyBudgetTests
{
    private static readonly DateOnly Today = new(2026, 1, 2);

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
            callsExhausted ? 1 : pro ? AppConfigKeys.DefaultProAiMicroDollarsPerDay : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay));
        await fixture.Database.Context.SaveChangesAsync();
        if (callsExhausted)
            fixture.Config.GetAsync(pro ? AppConfigKeys.ProAiCallsPerDay : AppConfigKeys.FreeAiCallsPerDay,
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
    [InlineData("Staging", true, "calls")]
    [InlineData("Staging", true, "spend")]
    [InlineData("Staging", true, "legacy")]
    [InlineData("Staging", true, "reservation")]
    [InlineData("Staging", false, "calls")]
    [InlineData("Staging", false, "spend")]
    [InlineData("Staging", false, "legacy")]
    [InlineData("Staging", false, "reservation")]
    [InlineData("Production", true, "calls")]
    [InlineData("Production", true, "spend")]
    [InlineData("Production", true, "legacy")]
    [InlineData("Production", true, "reservation")]
    public async Task EvalUnmetered_SpentBudgetsRequireStagingAndTargetedFlag_UsageStillGrows(
        string environmentName, bool flagged, string guard)
    {
        using var fixture = new BudgetFixture(pro: true, evalExempt: flagged, environmentName: environmentName);
        if (guard == "calls")
            fixture.Config.GetAsync(AppConfigKeys.ProAiCallsPerDay, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        if (guard == "reservation")
            fixture.Config.GetAsync(AppConfigKeys.ProAiMicroDollarsPerDay, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(1L);
        if (guard != "reservation")
        {
            var spent = AiBudgetCall.Reserve(fixture.User.Id, Today, guard == "calls" ? 1 : AppConfigKeys.DefaultProAiMicroDollarsPerDay);
            if (guard == "legacy")
            {
                spent.Settle(0);
                fixture.Database.Context.Entry(spent).Property<long>("ChargedTokens").CurrentValue = 20_000;
            }
            fixture.Database.Context.AiBudgetCalls.Add(spent);
            await fixture.Database.Context.SaveChangesAsync();
        }

        if (environmentName != "Staging" || !flagged)
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

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 50)]
    public async Task TypicalWrites_FitUnderSpendCeiling(bool pro, int messages)
    {
        (pro ? AppConfigKeys.DefaultProAiMicroDollarsPerDay : AppConfigKeys.DefaultFreeAiMicroDollarsPerDay)
            .Should().Be(pro ? 120_000 : 20_000);
        using var fixture = new BudgetFixture(pro);
        for (var call = 0; call < messages * 2; call++)
            await fixture.Client.CompleteTextAsync("system", "a typical write");

        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Count().Should().Be(messages * 2);
        read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().Be(2644 + (messages * 2 - 1) * 649);
        read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().BeLessThan(pro ? 120_000 : 20_000);
        read.AiUsageDaily.Single().TotalTokens.Should().Be(messages * 2 * 20_100);
        fixture.Handler.Completions.Should().Be(messages * 2);
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

        reserved.Should().Be(6546);
        using var read = fixture.Database.CreateContext();
        var call = read.AiBudgetCalls.Single();
        call.ChargedMicroDollars.Should().Be(2644);
        call.IsSettled.Should().BeTrue();
        read.AiUsageDaily.Single().CostUsd.Should().Be(0.00264375m);
        JsonElement.DeepEquals(fixture.EstimatedRequests.Single().GetProperty("input"),
            fixture.Handler.CompletionRequests.Single().GetProperty("input")).Should().BeTrue();
        fixture.Handler.CountedRequests.Should().BeEmpty();
        fixture.Handler.CompletionRequests.Single().GetProperty("max_output_tokens").GetInt32().Should().Be(8192);
    }

    [Fact]
    public async Task SixCallToolLoops_StopAtDollarCeilingBeforeNextCompletion()
    {
        using var fixture = new BudgetFixture(pro: true);
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

        loops.Should().Be(3, $"provider calls: {fixture.Handler.Completions}");
        fixture.Handler.Completions.Should().Be(18);
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Count().Should().Be(18);
        read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().Be(117_828);
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
        var row = AiBudgetCall.Reserve(fixture.User.Id, Today, callsExhausted ? 1 : 20_000);
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
        read.AiBudgetCalls.Single().ChargedMicroDollars.Should().Be(6546);
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
        charged.ChargedMicroDollars.Should().Be(6546);
        charged.IsSettled.Should().BeTrue();
    }

    [Fact]
    public async Task RepeatedDisconnectedStreams_HitDailyCeiling()
    {
        using var fixture = new BudgetFixture();
        fixture.Handler.OmitTerminalEvent = true;
        var service = new AiIntentService(fixture.Client, fixture.Recorder, NullLogger<AiIntentService>.Instance);
        var deltas = 0;
        for (var attempt = 0; attempt < 3; attempt++)
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
        fixture.Handler.Completions.Should().Be(3);
        deltas.Should().Be(3);
        using var read = fixture.Database.CreateContext();
        read.AiBudgetCalls.Sum(call => call.ChargedMicroDollars).Should().Be(19_638);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 0)]
    [InlineData(HttpStatusCode.TooManyRequests, 0)]
    [InlineData(HttpStatusCode.InternalServerError, 6546)]
    [InlineData(HttpStatusCode.BadGateway, 6546)]
    [InlineData(HttpStatusCode.GatewayTimeout, 6546)]
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
        internal IAiUsageRecorder Recorder { get; }
        internal List<JsonElement> EstimatedRequests { get; } = [];
        private readonly ServiceProvider _provider;
        private readonly Microsoft.Extensions.Caching.Memory.MemoryCache _estimateCache = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

        internal BudgetFixture(bool pro = false, string model = "gpt-6-luna", int completionTimeoutSeconds = 185,
            bool useLocalEstimator = false, bool evalExempt = false, string environmentName = "Staging")
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
                .AddScoped<IUnitOfWork>(scope => new UnitOfWork(scope.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()))
                .AddSingleton<IHostEnvironment>(Host.CreateApplicationBuilder(
                    new HostApplicationBuilderSettings { EnvironmentName = environmentName }).Environment)
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
            Client = new BudgetedAiCompletionClient(raw, new AiDailyBudget(_provider.GetRequiredService<IServiceScopeFactory>()),
                Http, NullLogger<BudgetedAiCompletionClient>.Instance, useLocalEstimator
                    ? new AiRequestCostEstimator(_estimateCache, options) : estimates);
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
            var response = ResponsesTestData.Response("{\"value\":\"ok\"}", InputTokens, FullToolLoops ? 8192 : 500,
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
