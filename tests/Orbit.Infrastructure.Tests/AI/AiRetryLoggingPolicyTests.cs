using System.ClientModel.Primitives;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Tests.AI;

public class AiRetryLoggingPolicyTests
{
    [Fact]
    public void Send_RetriableFailures_LogsEveryAttemptWithItsNumber()
    {
        var logger = new CollectingLogger();
        var pipeline = BuildPipeline(logger, HttpStatusCode.ServiceUnavailable, maxRetries: 2);
        var message = CreateMessage(pipeline);

        pipeline.Send(message);

        message.Response!.Status.Should().Be(503);
        logger.Entries.Should().SatisfyRespectively(
            first => first.Should().Contain("AI attempt 1 failed").And.Contain("retriable: True"),
            second => second.Should().Contain("AI attempt 2 failed").And.Contain("retriable: True"),
            third => third.Should().Contain("AI attempt 3 failed").And.Contain("retriable: False"));
        logger.Entries.Should().AllSatisfy(entry => entry.Should().Contain("HTTP 503"));
    }

    [Fact]
    public async Task SendAsync_RetriableFailures_LogsEveryAttemptWithItsNumber()
    {
        var logger = new CollectingLogger();
        var pipeline = BuildPipeline(logger, HttpStatusCode.ServiceUnavailable, maxRetries: 1);
        var message = CreateMessage(pipeline);

        await pipeline.SendAsync(message);

        message.Response!.Status.Should().Be(503);
        logger.Entries.Should().SatisfyRespectively(
            first => first.Should().Contain("AI attempt 1 failed").And.Contain("retriable: True"),
            second => second.Should().Contain("AI attempt 2 failed").And.Contain("retriable: False"));
    }

    [Fact]
    public void Send_SuccessfulResponse_LogsNothing()
    {
        var logger = new CollectingLogger();
        var pipeline = BuildPipeline(logger, HttpStatusCode.OK, maxRetries: 2);
        var message = CreateMessage(pipeline);

        pipeline.Send(message);

        message.Response!.Status.Should().Be(200);
        logger.Entries.Should().BeEmpty();
    }

    private static ClientPipeline BuildPipeline(ILogger logger, HttpStatusCode status, int maxRetries)
    {
        return ClientPipeline.Create(new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(new FixedStatusHandler(status))),
            RetryPolicy = new ZeroDelayRetryPolicy(maxRetries, logger),
        });
    }

    private static PipelineMessage CreateMessage(ClientPipeline pipeline)
    {
        var message = pipeline.CreateMessage();
        message.Request.Method = "GET";
        message.Request.Uri = new Uri("https://orbit.test/ai");
        return message;
    }

    private sealed class ZeroDelayRetryPolicy(int maxRetries, ILogger logger)
        : AiRetryLoggingPolicy(maxRetries, logger)
    {
        protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) => TimeSpan.Zero;
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return BuildResponse(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(BuildResponse(request));
        }

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            return new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(string.Empty),
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
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}
