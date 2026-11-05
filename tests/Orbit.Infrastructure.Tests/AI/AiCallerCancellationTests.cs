#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

/// <summary>
/// A caller that cancels its own token (a superseded daily summary refresh, a closed chat stream) did not
/// see an AI failure, so the usage recorder and the logs must not report one. A completion deadline that
/// expires while the provider is still working is a real failure and keeps its failed record and warnings.
/// </summary>
public class AiCallerCancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("budget-text")]
    [InlineData("budget-json")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task CallerCancellation_DuringProviderCall_RecordsCancelledWithoutWarning(string kind)
    {
        var harness = new Harness();
        using var caller = new CancellationTokenSource();
        var call = harness.Start(kind, caller.Token);
        await harness.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await caller.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        thrown.Should().BeAssignableTo<OperationCanceledException>("the caller still observes its own cancellation");
        var telemetry = harness.SingleTelemetry();
        telemetry.IsError.Should().BeFalse("a caller cancellation is not an AI failure");
        telemetry.IsCancelled.Should().BeTrue();
        telemetry.HasUsage.Should().BeFalse();
        harness.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Information,
            "a superseded call must not reach the production logs as a failure");
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("budget-text")]
    [InlineData("budget-json")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task CompletionDeadline_DuringProviderCall_RecordsFailedWithWarning(string kind)
    {
        var harness = new Harness();
        using var caller = new CancellationTokenSource();
        var call = harness.Start(kind, caller.Token);
        await harness.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Clock.Advance(Timeout);

        var thrown = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        thrown.Should().BeAssignableTo<OperationCanceledException>();
        caller.IsCancellationRequested.Should().BeFalse();
        var telemetry = harness.SingleTelemetry();
        telemetry.IsError.Should().BeTrue("a deadline that expires during the provider call is a failure");
        telemetry.IsCancelled.Should().BeFalse();
        telemetry.HasUsage.Should().BeFalse();
        harness.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message.StartsWith("AI attempt 1 failed (", StringComparison.Ordinal));
        if (kind is not ("text" or "json"))
            harness.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
                && entry.Message == "AI completion failed at completion with cancelled");
    }

    private sealed class Harness
    {
        private readonly AiCompletionClient _client;
        private readonly BudgetedAiCompletionClient _budgeted;
        private readonly AiIntentService _intent;

        internal Harness()
        {
            var responses = new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                RetryPolicy = new AiRetryLoggingPolicy(2, new CollectingLogger<AiRetryLoggingPolicy>(Entries)),
                Transport = new HttpClientPipelineTransport(new HttpClient(Handler))
            });
            _client = new AiCompletionClient(responses, new CollectingLogger<AiCompletionClient>(Entries), Usage,
                Timeout, timeProvider: Clock);
            _budgeted = AiBudgetTestDouble.Wrap(_client, new CollectingLogger<BudgetedAiCompletionClient>(Entries));
            _intent = new AiIntentService(_budgeted, Usage, new CollectingLogger<AiIntentService>(Entries));
        }

        internal BlockingHandler Handler { get; } = new();
        internal CompletionTestClock Clock { get; } = new();
        internal IAiUsageRecorder Usage { get; } = Substitute.For<IAiUsageRecorder>();
        internal List<(LogLevel Level, string Message)> Entries { get; } = [];

        internal Task Start(string kind, CancellationToken token) => kind switch
        {
            "text" => _client.CompleteTextAsync("system", "user", purpose: "daily_summary", cancellationToken: token),
            "json" => _client.CompleteJsonAsync<Probe>("system", "user", cancellationToken: token),
            "budget-text" => _budgeted.CompleteTextAsync("system", "user", purpose: "daily_summary", cancellationToken: token),
            "budget-json" => _budgeted.CompleteJsonAsync<Probe>("system", "user", cancellationToken: token),
            _ => _intent.SendWithToolsAsync(new AiToolRequest("user", "system", []),
                kind == "stream" ? _ => Task.CompletedTask : null, cancellationToken: token)
        };

        internal AiCallTelemetry SingleTelemetry()
        {
            var arguments = Usage.ReceivedCalls().Should().ContainSingle().Which.GetArguments();
            arguments.Skip(2).Take(4).Should().OnlyContain(value => (long)value! == 0);
            return (AiCallTelemetry)arguments[8]!;
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class CollectingLogger<T>(List<(LogLevel Level, string Message)> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries)
                entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed record Probe(string Value);
}
