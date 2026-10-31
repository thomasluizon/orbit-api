using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Persistence;
using System.Data.Common;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;

namespace Orbit.Infrastructure.Tests.AI;

public class AiUsageRecorderTests
{
    [Fact]
    public async Task RecordAsync_EmitsExactlyOneGenerationAlongsideDailyUpsert()
    {
        var analytics = Substitute.For<IProductAnalytics>();
        var commandCapture = new UpsertCapture();
        using var services = new ServiceCollection()
            .AddSingleton(analytics)
            .AddDbContext<OrbitDbContext>(options => options.UseSqlite("Data Source=:memory:").AddInterceptors(commandCapture))
            .BuildServiceProvider();
        var recorder = ActivatorUtilities.CreateInstance<AiUsageRecorder>(services,
            Options.Create(new AiSettings { Pricing = new Dictionary<string, AiModelPrice> { ["priced"] = SamplePrice } }),
            NullLogger<AiUsageRecorder>.Instance);

        await recorder.RecordAsync("summary", "priced", 200_000, 1_000_000, 500_000, 1_500_000, reasoningTokens: 123);

        commandCapture.Costs.Should().ContainSingle().Which.Should().Be(1.14m);
        commandCapture.ReasoningTokens.Should().ContainSingle().Which.Should().Be(123);
        analytics.Received(1).CaptureAggregateEvent("$ai_generation", Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    private sealed class UpsertCapture : DbCommandInterceptor
    {
        public List<decimal> Costs { get; } = [];
        public List<long> ReasoningTokens { get; } = [];
        public List<string> Purposes { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Costs.Add(Convert.ToDecimal(command.Parameters.Cast<DbParameter>().Last().Value, System.Globalization.CultureInfo.InvariantCulture));
            command.CommandText.Should().Contain("\"ReasoningTokens\" = \"AiUsageDaily\".\"ReasoningTokens\" + EXCLUDED.\"ReasoningTokens\"");
            ReasoningTokens.Add(Convert.ToInt64(command.Parameters.Cast<DbParameter>().Reverse().Skip(1).First().Value));
            Purposes.Add((string)command.Parameters[3].Value!);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BudgetAwareRecorder_LabelsEvalUsageEvenWithoutAnEvalKey(bool eval)
    {
        var userId = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StagingEval:AccountIds"] = (eval ? userId : Guid.NewGuid()).ToString()
        }).Build();
        var capture = new UpsertCapture();
        using var services = new ServiceCollection()
            .AddDbContext<OrbitDbContext>(options => options.UseSqlite("Data Source=:memory:").AddInterceptors(capture))
            .BuildServiceProvider();
        var settings = Options.Create(new AiSettings { Pricing = new() { ["priced"] = SamplePrice } });
        var inner = new AiUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), settings,
            Substitute.For<IProductAnalytics>(), NullLogger<AiUsageRecorder>.Instance);
        var recorder = new BudgetAwareAiUsageRecorder(inner, new HttpContextAccessor(), settings, config);
        using var context = AiCallContext.Begin(userId);

        await recorder.RecordAsync("chat", "priced", 40, 100, 30, 130, reasoningTokens: 20);
        await recorder.RecordAsync("habit_suggest", "priced", 0, 100, 30, 130, userId: userId, reasoningTokens: 20);

        capture.Purposes.Should().Equal(eval ? ["eval_chat", "eval_habit_suggest"] : ["chat", "habit_suggest"]);
        capture.ReasoningTokens.Should().Equal(20, 20);
        context.ChargedMicroDollars.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordAsync_GenerationHasTypedPropertiesAndMatchesDailyCosts(bool unpriced)
    {
        var analytics = Substitute.For<IProductAnalytics>();
        var commandCapture = new UpsertCapture();
        using var services = new ServiceCollection().AddSingleton(analytics)
            .AddDbContext<OrbitDbContext>(options => options.UseSqlite("Data Source=:memory:").AddInterceptors(commandCapture))
            .BuildServiceProvider();
        var recorder = ActivatorUtilities.CreateInstance<AiUsageRecorder>(services,
            Options.Create(new AiSettings { Pricing = unpriced ? [] : new() { ["priced"] = SamplePrice } }),
            NullLogger<AiUsageRecorder>.Instance);
        var userId = Guid.NewGuid();
        for (var round = 0; round < 2; round++)
            await recorder.RecordAsync("chat", "priced", 200_000, 1_000_000, 500_000, 1_500_000,
                userId: userId, telemetry: new AiCallTelemetry("chat-correlation-id", 1.25, round == 1, ["log_habit"], "trial"));

        var events = analytics.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IProductAnalytics.CaptureUserEvent)).ToList();
        events.Should().HaveCount(2);
        var spans = new HashSet<string>();
        foreach (var captured in events)
        {
            captured.GetArguments()[0].Should().Be(userId);
            captured.GetArguments()[1].Should().Be("$ai_generation");
            captured.GetArguments()[2].Should().Be("trial");
            var properties = (IReadOnlyDictionary<string, object>)captured.GetArguments()[3]!;
            properties.Keys.Should().BeEquivalentTo(new[] { "$ai_trace_id", "$ai_span_id", "$ai_model", "$ai_provider",
                "$ai_input_tokens", "$ai_output_tokens", "$ai_cache_read_input_tokens", "$ai_latency", "$ai_is_error",
                "$ai_input_cost_usd", "$ai_output_cost_usd", "$ai_total_cost_usd", "purpose", "tool_names" });
            properties["$ai_trace_id"].Should().Be("chat-correlation-id");
            spans.Add((string)properties["$ai_span_id"]);
            properties["$ai_model"].Should().Be("priced");
            properties["$ai_provider"].Should().Be("openai");
            properties["$ai_input_tokens"].Should().BeOfType<long>().Which.Should().Be(1_000_000);
            properties["$ai_output_tokens"].Should().BeOfType<long>().Which.Should().Be(500_000);
            properties["$ai_cache_read_input_tokens"].Should().BeOfType<long>().Which.Should().Be(200_000);
            properties["$ai_latency"].Should().BeOfType<double>().Which.Should().Be(1.25);
            properties["$ai_is_error"].Should().BeOfType<bool>();
            properties["$ai_input_cost_usd"].Should().Be(unpriced ? 0m : 0.34m);
            properties["$ai_output_cost_usd"].Should().Be(unpriced ? 0m : 0.8m);
            properties["$ai_total_cost_usd"].Should().Be(commandCapture.Costs[0]);
            properties["tool_names"].Should().BeEquivalentTo(new[] { "log_habit" });
            properties["purpose"].Should().Be("chat");
        }
        spans.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordAsync_AnalyticsFailureOrNoOp_DoesNotChangeDailyUpsert(bool noOp)
    {
        var throwing = Substitute.For<IProductAnalytics>();
        throwing.When(analytics => analytics.CaptureUserEvent(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, object>>())).Do(_ => throw new InvalidOperationException("offline"));
        IProductAnalytics analytics = noOp ? new NoOpProductAnalytics() : throwing;
        var commandCapture = new UpsertCapture();
        using var services = new ServiceCollection().AddSingleton(analytics)
            .AddDbContext<OrbitDbContext>(options => options.UseSqlite("Data Source=:memory:").AddInterceptors(commandCapture))
            .BuildServiceProvider();
        var recorder = ActivatorUtilities.CreateInstance<AiUsageRecorder>(services,
            Options.Create(new AiSettings { Pricing = new() { ["priced"] = SamplePrice } }), NullLogger<AiUsageRecorder>.Instance);

        await recorder.RecordAsync("chat", "priced", 200_000, 1_000_000, 500_000, 1_500_000, userId: Guid.NewGuid());

        commandCapture.Costs.Should().ContainSingle().Which.Should().Be(1.14m);
    }

    [Fact]
    public async Task RecordAsync_DailyWriteFailure_StillCapturesOneGeneration()
    {
        var analytics = Substitute.For<IProductAnalytics>();
        using var services = new ServiceCollection().AddSingleton(analytics).BuildServiceProvider();
        var recorder = ActivatorUtilities.CreateInstance<AiUsageRecorder>(services,
            Options.Create(new AiSettings()), NullLogger<AiUsageRecorder>.Instance);

        await recorder.RecordAsync("summary", "unknown", 0, 5, 3, 8);

        analytics.Received(1).CaptureAggregateEvent("$ai_generation", Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Fact]
    public async Task RecordAsync_ErrorWithoutUsage_EmitsEventWithoutCreatingDailyRow()
    {
        var analytics = Substitute.For<IProductAnalytics>();
        var commandCapture = new UpsertCapture();
        using var services = new ServiceCollection().AddSingleton(analytics)
            .AddDbContext<OrbitDbContext>(options => options.UseSqlite("Data Source=:memory:").AddInterceptors(commandCapture))
            .BuildServiceProvider();
        var recorder = ActivatorUtilities.CreateInstance<AiUsageRecorder>(services,
            Options.Create(new AiSettings()), NullLogger<AiUsageRecorder>.Instance);

        await recorder.RecordAsync("summary", "unpriced", 0, 0, 0, 0,
            telemetry: new AiCallTelemetry("job-run", 0.1, true, [], HasUsage: false));

        commandCapture.Costs.Should().BeEmpty();
        analytics.Received(1).CaptureAggregateEvent("$ai_generation", Arg.Is<IReadOnlyDictionary<string, object>>(properties =>
            (bool)properties["$ai_is_error"] && (decimal)properties["$ai_total_cost_usd"] == 0));
    }

    private static readonly AiModelPrice SamplePrice = new()
    {
        InputPerMillionUsd = 0.40m,
        CachedInputPerMillionUsd = 0.10m,
        OutputPerMillionUsd = 1.60m
    };

    [Fact]
    public void ComputeCostUsd_UncachedInputPlusOutput_SumsBothLegs()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            SamplePrice, cachedTokens: 0, promptTokens: 1_000_000, completionTokens: 500_000);

        cost.Should().Be(1.20m);
    }

    [Fact]
    public void ComputeCostUsd_SplitsCachedFromUncachedInput()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            SamplePrice, cachedTokens: 200_000, promptTokens: 1_000_000, completionTokens: 0);

        cost.Should().Be(0.34m);
    }

    [Fact]
    public void ComputeCostUsd_AllInputCached_ChargesOnlyCachedRate()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            SamplePrice, cachedTokens: 500_000, promptTokens: 500_000, completionTokens: 0);

        cost.Should().Be(0.05m);
    }

    [Fact]
    public void ComputeCostUsd_OutputOnly_ChargesOutputRate()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            SamplePrice, cachedTokens: 0, promptTokens: 0, completionTokens: 2_000_000);

        cost.Should().Be(3.20m);
    }

    [Fact]
    public void ComputeCostUsd_UnpricedModel_ReturnsZero()
    {
        var cost = AiUsageRecorder.ComputeCostUsd(
            null, cachedTokens: 1_000, promptTokens: 5_000, completionTokens: 3_000);

        cost.Should().Be(0m);
    }
}
