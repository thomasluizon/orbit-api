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

    [Fact]
    public void Send_TransportThrows_LogsSafeClassificationWithoutRawExceptionMessage()
    {
        const string secretDetail = "connection to 10.0.0.5:5432 refused; internal token abc123";
        var logger = new CollectingLogger();
        var pipeline = BuildThrowingPipeline(logger, secretDetail, maxRetries: 1);
        var message = CreateMessage(pipeline);

        var send = () => pipeline.Send(message);

        send.Should().Throw<Exception>();
        logger.Entries.Should().NotBeEmpty();
        logger.Entries.Should().AllSatisfy(entry =>
        {
            entry.Should().MatchRegex(@"^AI attempt \d+ failed \([A-Za-z]+\); retriable: (True|False)$");
            entry.Should().NotContain(secretDetail);
        });
    }

    [Theory]
    [InlineData("insufficient_quota", "insufficient_quota", false, 1)]
    [InlineData("credit_balance_exhausted", "insufficient_quota", false, 1)]
    [InlineData(null, "insufficient_quota", false, 1)]
    [InlineData(null, "credit_balance_exhausted", false, 1)]
    [InlineData("rate_limit_exceeded", "requests", false, 3)]
    [InlineData(null, null, false, 3)]
    [InlineData("insufficient_quota", "insufficient_quota", true, 3)]
    public async Task Send_Provider429_ClassifiesBodyAndPreservesRetryAfter(
        string? code, string? type, bool retryAfter, int expectedAttempts)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            error = new { message = "Provider failure", code, type, param = (string?)null }
        });
        foreach (var asynchronous in new[] { false, true })
        {
            var logger = new CollectingLogger();
            var handler = new FixedStatusHandler(HttpStatusCode.TooManyRequests, body, retryAfter);
            var pipeline = ClientPipeline.Create(new ClientPipelineOptions
            {
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
                RetryPolicy = new ZeroDelayRetryPolicy(2, logger)
            });
            using var message = CreateMessage(pipeline);
            message.BufferResponse = false;

            if (asynchronous)
                await pipeline.SendAsync(message);
            else
                pipeline.Send(message);

            handler.CallCount.Should().Be(expectedAttempts);
            logger.Entries.Should().HaveCount(expectedAttempts);
            logger.Entries[^1].Should().Contain("retriable: False");
            if (expectedAttempts == 1)
            {
                message.Response!.Content.ToString().Should().Be(body);
                AiProviderErrors.IsQuotaExhausted(new System.ClientModel.ClientResultException(message.Response))
                    .Should().BeTrue();
            }
        }
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{\"error\":null}")]
    [InlineData("{\"error\":{\"code\":42}}")]
    [InlineData("{\"error\":{\"message\":\"insufficient_quota\"}}")]
    public async Task SendAsync_Unrecognized429_KeepsRetries(string body)
    {
        var logger = new CollectingLogger();
        var handler = new FixedStatusHandler(HttpStatusCode.TooManyRequests, body);
        var pipeline = ClientPipeline.Create(new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ZeroDelayRetryPolicy(2, logger)
        });
        using var message = CreateMessage(pipeline);

        await pipeline.SendAsync(message);

        handler.CallCount.Should().Be(3);
        logger.Entries.Should().HaveCount(3);
    }

    private static ClientPipeline BuildThrowingPipeline(ILogger logger, string exceptionMessage, int maxRetries)
    {
        return ClientPipeline.Create(new ClientPipelineOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(new ThrowingHandler(exceptionMessage))),
            RetryPolicy = new ZeroDelayRetryPolicy(maxRetries, logger),
        });
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

    private sealed class FixedStatusHandler(HttpStatusCode status, string body = "", bool retryAfter = false) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
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
            CallCount++;
            var response = new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body),
            };
            if (retryAfter)
                response.Headers.Add("Retry-After", "0");
            return response;
        }
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException(message);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException(message);
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
