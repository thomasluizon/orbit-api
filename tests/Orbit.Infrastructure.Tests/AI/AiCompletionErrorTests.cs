using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI;
using OpenAI.Chat;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Tests.AI;

/// <summary>
/// Failure-mode coverage for <see cref="AiCompletionClient"/> against OpenAI HTTP error responses.
/// The documented contract is to propagate the API failure to the caller (the trust boundary) while
/// the retry-logging policy makes each attempt observable and no phantom token usage is recorded for
/// a call that never produced a completion.
/// </summary>
public class AiCompletionErrorTests
{
    [Fact]
    public async Task CompleteTextAsync_QuotaExhausted429_RetriesThenThrowsWithoutRecordingUsage()
    {
        const string quotaBody =
            "{\"error\":{\"message\":\"You exceeded your current quota\",\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\"}}";
        var handler = new FixedStatusHandler(HttpStatusCode.TooManyRequests, quotaBody);
        var retryLogger = new CollectingLogger();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = BuildClient(handler, retryLogger, usageRecorder, maxRetries: 2);

        var act = () => client.CompleteTextAsync("system", "user");

        var exception = (await act.Should().ThrowAsync<ClientResultException>()).Which;
        exception.Status.Should().Be(429);
        handler.CallCount.Should().Be(3);
        retryLogger.Entries.Should().HaveCount(3);
        retryLogger.Entries[^1].Should().Contain("retriable: False");
        await usageRecorder.DidNotReceive().RecordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(),
            Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteJsonAsync_ContextLengthExceeded400_ThrowsImmediatelyWithoutRetryOrUsage()
    {
        const string contextBody =
            "{\"error\":{\"message\":\"maximum context length exceeded\",\"type\":\"invalid_request_error\",\"code\":\"context_length_exceeded\"}}";
        var handler = new FixedStatusHandler(HttpStatusCode.BadRequest, contextBody);
        var retryLogger = new CollectingLogger();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = BuildClient(handler, retryLogger, usageRecorder, maxRetries: 2);

        var act = () => client.CompleteJsonAsync<Probe>("system", "user");

        var exception = (await act.Should().ThrowAsync<ClientResultException>()).Which;
        exception.Status.Should().Be(400);
        handler.CallCount.Should().Be(1);
        retryLogger.Entries.Should().ContainSingle().Which.Should().Contain("retriable: False");
        await usageRecorder.DidNotReceive().RecordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(),
            Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    private static AiCompletionClient BuildClient(
        HttpMessageHandler handler, ILogger retryLogger, IAiUsageRecorder usageRecorder, int maxRetries)
    {
        var chatClient = new ChatClient(
            model: "gpt-test",
            credential: new ApiKeyCredential("test-key"),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
                RetryPolicy = new ZeroDelayRetryPolicy(maxRetries, retryLogger),
            });
        return new AiCompletionClient(chatClient, NullLogger<AiCompletionClient>.Instance, usageRecorder);
    }

    private sealed record Probe(string Value);

    private sealed class ZeroDelayRetryPolicy(int maxRetries, ILogger logger)
        : AiRetryLoggingPolicy(maxRetries, logger)
    {
        protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) => TimeSpan.Zero;
    }

    private sealed class FixedStatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => BuildResponse(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(BuildResponse(request));

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            CallCount++;
            return new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, exception));
    }
}
