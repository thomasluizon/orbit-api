#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.AI;

public class AiCompletionClientTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    public async Task UsageCancellation_DoesNotReplaceProviderError(string kind)
    {
        var usage = Substitute.For<IAiUsageRecorder>();
        usage.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(),
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>(), Arg.Any<AiCallTelemetry>(), Arg.Any<long>())
            .Returns(_ => Task.FromException(new OperationCanceledException("recording cancelled")));
        var client = new AiCompletionClient(BuildResponsesClient(new UnauthorizedHandler()),
            NullLogger<AiCompletionClient>.Instance, usage);
        Func<Task> call = kind == "json"
            ? async () => await client.CompleteJsonAsync<Probe>("system", "user")
            : async () => await client.CompleteTextAsync("system", "user");

        (await call.Should().ThrowAsync<ClientResultException>()).Which.Status.Should().Be(401);
        usage.ReceivedCalls().Should().ContainSingle();
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("chat")]
    public async Task CompletionDeadline_CancelsUsageWriteAfterSuccessfulResponse(string kind)
    {
        var entered = new TaskCompletionSource<(CancellationToken Token, AiCallTelemetry Telemetry)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usage = Substitute.For<IAiUsageRecorder>();
        usage.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(),
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>(), Arg.Any<AiCallTelemetry>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                entered.TrySetResult((token, call.Arg<AiCallTelemetry>()));
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            });
        var clock = new CompletionTestClock();
        var client = new AiCompletionClient(BuildResponsesClient(new CapturingHandler()), NullLogger<AiCompletionClient>.Instance,
            usage, TimeSpan.FromSeconds(1), timeProvider: clock);
        var intent = new AiIntentService(AiBudgetTestDouble.Wrap(client), usage, NullLogger<AiIntentService>.Instance);
        Func<Task> call = kind switch
        {
            "json" => async () => await client.CompleteJsonAsync<Probe>("system", "user"),
            "text" => async () => await client.CompleteTextAsync("system", "user"),
            _ => async () => await intent.SendWithToolsAsync(new AiToolRequest("user", "system", []))
        };

        var completion = call();
        var recording = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        recording.Telemetry.HasUsage.Should().BeTrue();
        recording.Telemetry.IsError.Should().BeFalse();
        recording.Token.CanBeCanceled.Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        recording.Token.IsCancellationRequested.Should().BeFalse();
        clock.Advance(TimeSpan.FromTicks(1));
        recording.Token.IsCancellationRequested.Should().BeTrue();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        recording.Token.IsCancellationRequested.Should().BeTrue();
        Func<Task> bounded = () => completion.WaitAsync(TimeSpan.FromSeconds(5));
        await bounded.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    public async Task BackgroundCalls_RecordPurposeLatencyAndOneTracePerRun(string kind)
    {
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(new CapturingHandler()), NullLogger<AiCompletionClient>.Instance, usage);
        for (var index = 0; index < 2; index++)
        {
            if (kind == "json")
                await client.CompleteJsonAsync<Probe>("private system", "private user", purpose: "daily_summary");
            else
                await client.CompleteTextAsync("private system", "private user", purpose: "daily_summary");
        }
        var calls = usage.ReceivedCalls().ToList();
        calls.Should().HaveCount(2);
        calls.Should().OnlyContain(call => (string)call.GetArguments()[0]! == "daily_summary");
        var metadata = calls.Select(call => (AiCallTelemetry)call.GetArguments()[8]!).ToList();
        metadata.Should().OnlyContain(call => call.LatencySeconds >= 0 && !call.IsError && call.HasUsage && call.ToolNames.Count == 0);
        metadata.Select(call => call.TraceId).Distinct().Should().HaveCount(2);
        JsonSerializer.Serialize(metadata).Should().NotContain("private");
    }

    [Fact]
    public async Task CompleteJsonAsync_SubTaskTier_OmitsTemperature()
    {
        var handler = new CapturingHandler();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);

        await client.CompleteJsonAsync<Probe>("You are a helpful assistant. Respond only with valid JSON.", "extract facts", purpose: "fact_extraction", tier: AiModelTier.SubTask);

        handler.LastRequestBody.Should().NotBeNull();
        handler.LastRequestUri.Should().EndWith("/responses");
        handler.LastRequestBody.Should().NotContain("temperature");
        await usageRecorder.Received(1).RecordAsync(
            "fact_extraction", "gpt-6-luna", 0, 1, 2, 3, Arg.Any<CancellationToken>(), null, Arg.Any<AiCallTelemetry>());
    }

    [Theory]
    [InlineData("json", AiModelTier.Primary)]
    [InlineData("json", AiModelTier.SubTask)]
    [InlineData("text", AiModelTier.Primary)]
    [InlineData("text", AiModelTier.SubTask)]
    public async Task DefaultModels_BackgroundCalls_UseLunaWithLowReasoning(string kind, AiModelTier tier)
    {
        var handler = new CapturingHandler();
        var settings = new AiSettings { ApiKey = "test-key", BaseUrl = "https://orbit.test/v1" };
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(Options.Create(settings), NullLogger<AiCompletionClient>.Instance,
            usage, new HttpClientPipelineTransport(new HttpClient(handler)));

        if (kind == "json")
            await client.CompleteJsonAsync<Probe>("system", "user", tier: tier);
        else
            await client.CompleteTextAsync("system", "user", tier: tier);

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        document.RootElement.GetProperty("model").GetString().Should().Be("gpt-6-luna");
        document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        document.RootElement.TryGetProperty("temperature", out _).Should().BeFalse();
        await usage.Received(1).RecordAsync(kind, "gpt-6-luna", 0, 1, 2, 3, Arg.Any<CancellationToken>(), null, Arg.Any<AiCallTelemetry>());
    }

    [Fact]
    public async Task CompleteJsonAsync_LegacyPrimaryModel_SendsTemperature()
    {
        var handler = new CapturingHandler();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>(), primaryModel: "gpt-4.1-mini");

        await client.CompleteJsonAsync<Probe>("You are a helpful assistant. Respond only with valid JSON.", "extract facts", temperature: 0.1, purpose: "json", tier: AiModelTier.Primary);

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        document.RootElement.GetProperty("temperature").GetDouble().Should().BeApproximately(0.1, 0.00001);
        document.RootElement.TryGetProperty("reasoning", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("include", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteJsonAsync_WithUserId_AttributesRecordedUsage()
    {
        var handler = new CapturingHandler();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var userId = Guid.NewGuid();

        await client.CompleteJsonAsync<Probe>("system", "user", userId: userId);

        await usageRecorder.Received(1).RecordAsync(
            "json", "gpt-6-luna", 0, 1, 2, 3, Arg.Any<CancellationToken>(), userId, Arg.Any<AiCallTelemetry>());
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        document.RootElement.GetProperty("safety_identifier").GetString().Should().Be(userId.ToString("N"));
        document.RootElement.GetProperty("prompt_cache_key").GetString().Should().Be(userId.ToString("N"));
        document.RootElement.TryGetProperty("user", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("text", false, "gpt-6-luna")]
    [InlineData("json", false, "gpt-6-luna")]
    [InlineData("text", true, "gpt-6-luna")]
    [InlineData("json", true, "gpt-6-luna")]
    [InlineData("text", false, "gpt-4.1-mini")]
    [InlineData("json", false, "gpt-4.1-mini")]
    [InlineData("text", true, "gpt-4.1-mini")]
    [InlineData("json", true, "gpt-4.1-mini")]
    public async Task BudgetedBackgroundCalls_UseStableCacheKeyForResolvedUser(string kind, bool background, string model)
    {
        var handler = new CapturingHandler();
        var usage = Substitute.For<IAiUsageRecorder>();
        var inner = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            usage, primaryModel: model, subTaskModel: model);
        var http = new HttpContextAccessor();
        var budget = Substitute.For<IAiDailyBudget>();
        var estimates = Substitute.For<IAiRequestCostEstimator>();
        estimates.EstimateMicroDollarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1000L);
        var client = new BudgetedAiCompletionClient(inner, budget, http,
            NullLogger<BudgetedAiCompletionClient>.Instance, estimates);
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();
        var keys = new List<string?>();

        foreach (var userId in new[] { firstUser, firstUser, secondUser })
        {
            http.HttpContext = background ? null : new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"))
            };
            using (background ? AiCallContext.Begin(userId) : null)
            {
                foreach (var tier in new[] { AiModelTier.Primary, AiModelTier.SubTask })
                {
                    if (kind == "json")
                        await client.CompleteJsonAsync<Probe>("system", "user", tier: tier);
                    else
                        await client.CompleteTextAsync("system", "user", tier: tier);

                    using var document = JsonDocument.Parse(handler.LastRequestBody!);
                    var root = document.RootElement;
                    keys.Add(root.GetProperty("prompt_cache_key").GetString());
                    keys[^1].Should().Be(userId.ToString("N"));
                    root.GetProperty("safety_identifier").GetString().Should().Be(keys[^1]);
                    root.GetProperty("model").GetString().Should().Be(model);
                    root.TryGetProperty("temperature", out _).Should().Be(model == "gpt-4.1-mini");
                    usage.ReceivedCalls().Last().GetArguments()[7].Should().Be(userId);
                    (AiCallContext.Current?.UserId).Should().Be(background ? userId : (Guid?)null);
                }
            }
            AiCallContext.Current.Should().BeNull();
        }

        keys.Take(4).Should().OnlyContain(key => key == firstUser.ToString("N"));
        keys.Skip(4).Should().OnlyContain(key => key == secondUser.ToString("N"));
        await budget.Received(4).ReserveAsync(firstUser, 1000L, Arg.Any<CancellationToken>());
        await budget.Received(2).ReserveAsync(secondUser, 1000L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteJsonAsync_ExplicitUserTakesPrecedenceOverAmbientUser()
    {
        var handler = new CapturingHandler();
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usage);
        var explicitUser = Guid.NewGuid();
        using var context = AiCallContext.Begin(Guid.NewGuid());

        await client.CompleteJsonAsync<Probe>("system", "user", userId: explicitUser);

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        document.RootElement.GetProperty("prompt_cache_key").GetString().Should().Be(explicitUser.ToString("N"));
        usage.ReceivedCalls().Should().ContainSingle().Which.GetArguments()[7].Should().Be(explicitUser);
    }

    [Fact]
    public void ResolveSubTaskModel_EmptyConfig_FallsBackToPrimary()
    {
        AiCompletionClient.ResolveSubTaskModel("", "gpt-4.1-mini").Should().Be("gpt-4.1-mini");
        AiCompletionClient.ResolveSubTaskModel("   ", "gpt-4.1-mini").Should().Be("gpt-4.1-mini");
    }

    [Fact]
    public void ResolveSubTaskModel_ConfiguredModel_IsUsed()
    {
        AiCompletionClient.ResolveSubTaskModel("gpt-5.4-nano", "gpt-4.1-mini").Should().Be("gpt-5.4-nano");
    }

    [Theory]
    [InlineData(AiModelTier.Primary, "gpt-6-luna", "gpt-6-luna", false)]
    [InlineData(AiModelTier.Primary, "gpt-5.4-nano", "gpt-6-luna", false)]
    [InlineData(AiModelTier.SubTask, "gpt-6-luna", "gpt-4.1-mini", true)]
    [InlineData(AiModelTier.Primary, "gpt-4.1-mini", "gpt-5.4-nano", true)]
    [InlineData(AiModelTier.SubTask, "gpt-4.1-mini", "gpt-5.4-nano", false)]
    [InlineData(AiModelTier.SubTask, "gpt-4.1-mini", "gpt-4.1-mini", true)]
    public void ShouldApplyTemperature_UsesSelectedModelCapabilities(
        AiModelTier tier, string primary, string subTask, bool expected)
        => AiCompletionClient.ShouldApplyTemperature(tier, primary, subTask).Should().Be(expected);

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task CompletionDeadline_CancelsTheWholeCallIncludingRetryWaits(string kind)
    {
        using var handler = new WaitingHandler();
        var clock = new CompletionTestClock();
        var retry = new ObservingRetryPolicy();
        var usage = Substitute.For<IAiUsageRecorder>();
        usage.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(),
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>(), Arg.Any<AiCallTelemetry>(), Arg.Any<long>())
            .Returns(Task.FromException(new OperationCanceledException("usage recording canceled")));
        var client = new AiCompletionClient(BuildResponsesClient(handler, retry), NullLogger<AiCompletionClient>.Instance,
            usage, TimeSpan.FromMilliseconds(50), timeProvider: clock);
        using var caller = new CancellationTokenSource();
        var call = StartCompletion(client, usage, kind, caller.Token);

        await AssertDeadlineDuringRetryAsync(call, retry, clock, client.CompletionTimeout, caller, kind);
        handler.RequestCount.Should().Be(1);
        clock.ActiveTimers.Should().Be(0);
        AssertFailedUsage(usage);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task CompletionDeadline_ConfiguredBound_CancelsBufferedCallsDuringRetryWait(string kind)
    {
        var settings = new AiSettings
        {
            ApiKey = "test-key",
            BaseUrl = "https://orbit.test/v1",
            CompletionTimeoutSeconds = 1
        };
        var usage = Substitute.For<IAiUsageRecorder>();
        using var handler = new WaitingHandler();
        var clock = new CompletionTestClock();
        var retry = new ObservingRetryPolicy();
        var client = new AiCompletionClient(Options.Create(settings), NullLogger<AiCompletionClient>.Instance,
            usage, new HttpClientPipelineTransport(new HttpClient(handler)), clock, retry);
        using var caller = new CancellationTokenSource();
        var call = StartCompletion(client, usage, kind, caller.Token);

        await AssertDeadlineDuringRetryAsync(call, retry, clock,
            kind == "stream" ? client.StreamingCompletionTimeout : client.CompletionTimeout, caller, kind);
        client.CompletionTimeout.Should().Be(TimeSpan.FromSeconds(1));
        client.StreamingCompletionTimeout.Should().Be(TimeSpan.FromSeconds(75));
        handler.RequestCount.Should().Be(1);
        clock.ActiveTimers.Should().Be(0);
        AssertFailedUsage(usage);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("chat")]
    [InlineData("stream")]
    [InlineData("budget-text")]
    [InlineData("budget-json")]
    public async Task CompletionDeadline_CallerCancellation_ReleasesTimersWithoutRetry(string kind)
    {
        using var handler = new WaitingHandler();
        var clock = new CompletionTestClock();
        var retry = new ObservingRetryPolicy();
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler, retry), NullLogger<AiCompletionClient>.Instance,
            usage, timeProvider: clock);
        using var caller = new CancellationTokenSource();
        var call = StartCompletion(client, usage, kind, caller.Token);
        var token = await retry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        caller.Cancel();

        token.IsCancellationRequested.Should().BeTrue();
        Func<Task> bounded = () => call.WaitAsync(TimeSpan.FromSeconds(5));
        await bounded.Should().ThrowAsync<OperationCanceledException>();
        clock.ActiveTimers.Should().Be(0);
        handler.RequestCount.Should().Be(1);
        AssertFailedUsage(usage);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task CompletionDeadline_PreCanceledCaller_DoesNotDispatch(string kind)
    {
        using var handler = new WaitingHandler();
        var clock = new CompletionTestClock();
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            usage, timeProvider: clock);
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        Func<Task> call = () => StartCompletion(client, usage, kind, caller.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
        handler.RequestCount.Should().Be(0);
        clock.ActiveTimers.Should().Be(0);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("chat")]
    [InlineData("budget-text")]
    [InlineData("budget-json")]
    public async Task CompletionDeadline_RepeatedSuccess_ReleasesTimersAndCallerRegistrations(string kind)
    {
        var clock = new CompletionTestClock();
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(new CapturingHandler()), NullLogger<AiCompletionClient>.Instance,
            usage, timeProvider: clock);
        using var caller = new CancellationTokenSource();
        for (var index = 0; index < 2; index++)
        {
            await StartCompletion(client, usage, kind, caller.Token);
            clock.ActiveTimers.Should().Be(0);
        }
        var tokens = usage.ReceivedCalls().Select(call => (CancellationToken)call.GetArguments()[6]!).ToArray();
        tokens.Should().HaveCount(2);

        caller.Cancel();
        clock.Advance(client.CompletionTimeout);

        tokens.Should().OnlyContain(token => !token.IsCancellationRequested,
            "completed scopes must detach their caller registrations and deadline timers");
    }

    [Fact]
    public void CompletionDeadline_ServiceRegistration_UsesRegisteredClock()
    {
        var clock = new CompletionTestClock();
        using var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(Options.Create(new AiSettings { ApiKey = "test-key" }))
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<AiCompletionClient>>(NullLogger<AiCompletionClient>.Instance)
            .AddSingleton(Substitute.For<IAiUsageRecorder>())
            .AddSingleton<AiCompletionClient>()
            .BuildServiceProvider();
        var client = services.GetRequiredService<AiCompletionClient>();
        using var scope = client.CreateCompletionCancellation(CancellationToken.None);

        clock.Advance(client.CompletionTimeout);

        scope.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void CompletionDeadline_ConcurrentScopes_KeepIndependentDeadlines()
    {
        var clock = new CompletionTestClock();
        var client = new AiCompletionClient(BuildResponsesClient(new CapturingHandler()), NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>(), TimeSpan.FromSeconds(1), timeProvider: clock);
        using var caller = new CancellationTokenSource();
        using var first = client.CreateCompletionCancellation(caller.Token);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        using var second = client.CreateCompletionCancellation(caller.Token);

        clock.Advance(TimeSpan.FromMilliseconds(500));

        first.Token.IsCancellationRequested.Should().BeTrue();
        second.Token.IsCancellationRequested.Should().BeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        second.Token.IsCancellationRequested.Should().BeTrue();
    }

    private static Task StartCompletion(AiCompletionClient client, IAiUsageRecorder usage, string kind, CancellationToken token)
    {
        var service = new AiIntentService(AiBudgetTestDouble.Wrap(client), usage, NullLogger<AiIntentService>.Instance);
        return kind switch
        {
            "budget-text" => AiBudgetTestDouble.Wrap(client).CompleteTextAsync("system", "user", cancellationToken: token),
            "budget-json" => AiBudgetTestDouble.Wrap(client).CompleteJsonAsync<Probe>("system", "user", cancellationToken: token),
            "json" => client.CompleteJsonAsync<Probe>("system", "user", cancellationToken: token),
            "text" => client.CompleteTextAsync("system", "user", cancellationToken: token),
            _ => service.SendWithToolsAsync(new AiToolRequest("user", "system", []),
                kind == "stream" ? _ => Task.CompletedTask : null, cancellationToken: token)
        };
    }

    private static async Task AssertDeadlineDuringRetryAsync(Task call, ObservingRetryPolicy retry,
        CompletionTestClock clock, TimeSpan timeout, CancellationTokenSource caller, string kind)
    {
        try
        {
            var token = await retry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            token.IsCancellationRequested.Should().BeFalse($"{kind} must enter the real retry wait before its deadline");
            clock.Advance(timeout - TimeSpan.FromTicks(1));
            token.IsCancellationRequested.Should().BeFalse($"{kind} must remain active just before its deadline");
            call.IsCompleted.Should().BeFalse();
            clock.Advance(TimeSpan.FromTicks(1));
            token.IsCancellationRequested.Should().BeTrue($"{kind} must forward cancellation at its controlled deadline");
            Func<Task> bounded = () => call.WaitAsync(TimeSpan.FromSeconds(5));
            var exception = (await bounded.Should().ThrowAsync<OperationCanceledException>()).Which;
            exception.CancellationToken.Should().Be(token, "failed usage recording must preserve the SDK cancellation");
        }
        finally
        {
            caller.Cancel();
            try { await call.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { caller.IsCancellationRequested.Should().BeTrue(); }
        }
    }

    private static void AssertFailedUsage(IAiUsageRecorder usage)
    {
        var arguments = usage.ReceivedCalls().Single().GetArguments();
        arguments.Skip(2).Take(4).Should().OnlyContain(value => (long)value! == 0);
        var metadata = (AiCallTelemetry)arguments[8]!;
        metadata.IsError.Should().BeTrue();
        metadata.HasUsage.Should().BeFalse();
    }

    private sealed class ObservingRetryPolicy() : AiRetryLoggingPolicy(2, NullLogger.Instance)
    {
        internal TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var wait = base.WaitAsync(delay, cancellationToken);
            Entered.TrySetResult(cancellationToken);
            return wait;
        }
    }

    [Theory]
    [InlineData("gpt-6.1-sol", "gpt-6-luna", AiModelTier.Primary, "gpt-6.1-sol", false)]
    [InlineData("gpt-6-luna", "gpt-6-luna", AiModelTier.Primary, "gpt-6-luna", false)]
    [InlineData("gpt-6-luna", "gpt-6-luna", AiModelTier.SubTask, "gpt-6-luna", false)]
    [InlineData("gpt-4.1-mini", "gpt-6-luna", AiModelTier.Primary, "gpt-4.1-mini", true)]
    [InlineData("gpt-6-luna", "gpt-4.1-mini", AiModelTier.SubTask, "gpt-4.1-mini", true)]
    [InlineData("gpt-4.1-mini", "", AiModelTier.SubTask, "gpt-4.1-mini", true)]
    public async Task CompleteTextAsync_ConfiguredTier_SendsCompatibleResponsesOptions(
        string primary, string subTask, AiModelTier tier, string expectedModel, bool temperatureAccepted)
    {
        var handler = new CapturingHandler();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>(), primaryModel: primary, subTaskModel: subTask);

        await client.CompleteTextAsync("system", "user", tier: tier);

        using var document = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var root = document.RootElement;
        root.GetProperty("model").GetString().Should().Be(expectedModel);
        root.GetProperty("store").GetBoolean().Should().BeFalse();
        root.TryGetProperty("temperature", out _).Should().Be(temperatureAccepted);
        if (temperatureAccepted)
        {
            root.TryGetProperty("reasoning", out _).Should().BeFalse();
            root.TryGetProperty("include", out _).Should().BeFalse();
        }
        else
        {
            root.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
            root.GetProperty("include").EnumerateArray().Select(value => value.GetString())
                .Should().Contain("reasoning.encrypted_content");
        }
    }

    [Fact]
    public async Task CompleteTextAsync_MultipleAssistantMessages_PreservesMessageBoundaries()
    {
        var response = ResponsesTestData.Response();
        response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem(
            [ResponseContentPart.CreateOutputTextPart("Check", []), ResponseContentPart.CreateOutputTextPart("ing.", [])]));
        response.OutputItems.Add(ResponseItem.CreateReasoningItem([]));
        response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Done."));
        var handler = new CapturingHandler(ModelReaderWriter.Write(response).ToString());
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>());

        var result = await client.CompleteTextAsync("system", "user");

        result.Should().Be("Checking.\n\nDone.");
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task NetworkTimeout_BufferedReasoningHasHeadroomWhileStreamingFailsPromptly(string kind)
    {
        var handler = new SlowResponseHandler(waitForCancellation: kind == "stream");
        var settings = new AiSettings
        {
            ApiKey = "test-key",
            BaseUrl = "https://orbit.test/v1",
            NetworkTimeoutSeconds = 1,
            BufferedNetworkTimeoutSeconds = 10,
            MaxRetries = 0
        };
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(Options.Create(settings), NullLogger<AiCompletionClient>.Instance,
            usage, new HttpClientPipelineTransport(new HttpClient(handler)));
        var service = new AiIntentService(Orbit.Infrastructure.Tests.AiBudgetTestDouble.Wrap(client), usage, NullLogger<AiIntentService>.Instance);
        Func<Task> call = async () =>
        {
            if (kind == "text")
                (await client.CompleteTextAsync("system", "user")).Should().NotBeNullOrWhiteSpace();
            else if (kind == "json")
                (await client.CompleteJsonAsync<Probe>("system", "user"))!.Value.Should().Be("ok");
            else
            {
                var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []),
                    kind == "stream" ? _ => Task.CompletedTask : null);
                result.IsSuccess.Should().Be(kind != "stream");
            }
        };

        var elapsed = Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(() => call().WaitAsync(TimeSpan.FromSeconds(20)));
        elapsed.Stop();

        if (kind != "stream")
        {
            error.Should().BeNull();
            elapsed.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        }
        else
        {
            if (error is not null)
                error.Should().BeAssignableTo<OperationCanceledException>();
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
        handler.CancellationObserved.Should().Be(kind == "stream");
        handler.RequestCount.Should().Be(1);
    }

    private sealed class SlowResponseHandler(bool waitForCancellation) : HttpMessageHandler
    {
        internal bool CancellationObserved { get; private set; }
        internal int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            try
            {
                await Task.Delay(waitForCancellation ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(ResponsesTestData.Text("{\"value\":\"ok\"}"), Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task CompletionDeadline_CancellationDuringResponseBuffering_PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new BufferingCancellationHandler(cancellation.Cancel);
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>());

        var call = () => client.CompleteJsonAsync<Probe>("system", "user", cancellationToken: cancellation.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
        handler.Buffers.Should().NotBeEmpty().And.OnlyContain(stream => stream.CanRead,
            "the handler owns backing streams until SDK buffering and cleanup finish");
    }

    private sealed class BufferingCancellationHandler(Action cancel) : HttpMessageHandler
    {
        internal List<Stream> Buffers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
            };
            response.Content = new CancellationContent(() =>
            {
                var buffer = new CancellingMemoryStream(() =>
                {
                    cancel();
                    response.Dispose();
                });
                Buffers.Add(buffer);
                return buffer;
            });
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Buffers.ForEach(stream => stream.Dispose());
            base.Dispose(disposing);
        }
    }

    private sealed class CancellationContent(Func<Stream> createBuffer) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new NetworkResponseStream(createBuffer()));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length)
        {
            length = 2;
            return true;
        }
    }

    private sealed class CancellingMemoryStream(Action cancel) : MemoryStream(Encoding.UTF8.GetBytes("{}"))
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            cancel();
            return base.CopyToAsync(destination, bufferSize, cancellationToken);
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        private readonly List<Stream> _buffers = [];
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var buffer = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
            _buffers.Add(buffer);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StreamContent(new NetworkResponseStream(buffer))
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _buffers.ForEach(stream => stream.Dispose());
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The HTTP handler owns the backing stream until SDK buffering and response cleanup finish.
    /// Disposing this non-seekable response view does not close the handler's backing stream.
    /// </summary>
    private sealed class NetworkResponseStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            => inner.CopyToAsync(destination, bufferSize, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static ResponsesClient BuildResponsesClient(HttpMessageHandler handler, ClientRetryPolicy? retryPolicy = null) =>
        new(
            credential: new ApiKeyCredential("test-key"),
            options: new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                RetryPolicy = retryPolicy,
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            });

    private sealed record Probe(string Value);

    private sealed class CapturingHandler(string? responseBody = null) : HttpMessageHandler
    {
        private static readonly string Body = ResponsesTestData.Text("{\"value\":\"ok\"}");

        public string? LastRequestBody { get; private set; }
        public string? LastRequestUri { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return BuildResponse(request);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return BuildResponse(request);
        }

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(responseBody ?? Body, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }
}
