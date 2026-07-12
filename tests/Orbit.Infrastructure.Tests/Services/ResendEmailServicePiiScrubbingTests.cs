using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ResendEmailServicePiiScrubbingTests
{
    private const string RecipientEmail = "secret.user@example.com";

    private readonly CollectingLogger<ResendEmailService> _logger = new();
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly ResendEmailService _sut;

    public ResendEmailServicePiiScrubbingTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.resend.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Resend").Returns(httpClient);

        var settings = Options.Create(new ResendSettings
        {
            ApiKey = "re_test_key",
            FromEmail = "noreply@useorbit.org",
            SupportEmail = "contact@useorbit.org",
            MarketingFromEmail = "news@useorbit.org",
            MarketingRetryBaseDelayMs = 1,
        });
        var frontend = Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" });

        _sut = new ResendEmailService(factory, settings, frontend, _logger);
    }

    [Fact]
    public async Task SuccessfulSend_InfoLog_OmitsRecipientEmail()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendVerificationCodeAsync(RecipientEmail, "123456");

        _logger.Entries.Should().NotBeEmpty();
        _logger.Entries.Should().OnlyContain(entry => !entry.Contains(RecipientEmail));
    }

    [Fact]
    public async Task FailedSend_ErrorLog_OmitsEmailAndResponseBody_ButKeepsStatus()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($"{{\"message\":\"invalid recipient {RecipientEmail}\"}}"),
        };

        await _sut.SendVerificationCodeAsync(RecipientEmail, "123456");

        var errorLog = _logger.Entries.Should()
            .ContainSingle(entry => entry.Contains("Email failed")).Subject;
        errorLog.Should().NotContain(RecipientEmail);
        errorLog.Should().NotContain("invalid recipient");
        errorLog.Should().Contain("BadRequest");
    }

    [Fact]
    public async Task SendException_ErrorLog_OmitsRecipientEmail()
    {
        _handler.ExceptionToThrow = new HttpRequestException("Connection reset");

        await _sut.SendVerificationCodeAsync(RecipientEmail, "123456");

        _logger.Entries.Should().Contain(entry => entry.Contains("Email send exception"));
        _logger.Entries.Should().OnlyContain(entry => !entry.Contains(RecipientEmail));
    }

    [Fact]
    public async Task MarketingFailure_ErrorLog_OmitsEmailAndResponseBody_ButKeepsStatus()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($"{{\"message\":\"blocked {RecipientEmail}\"}}"),
        };

        await _sut.SendMarketingEmailAsync(
            RecipientEmail, "Product news", "<p>hi</p>", "en",
            "https://api.useorbit.org/api/marketing/unsubscribe?token=t");

        var errorLog = _logger.Entries.Should()
            .ContainSingle(entry => entry.Contains("Email failed")).Subject;
        errorLog.Should().NotContain(RecipientEmail);
        errorLog.Should().NotContain("blocked");
        errorLog.Should().Contain("BadRequest");
    }

    [Fact]
    public async Task MarketingSuccess_InfoLog_OmitsRecipientEmail()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await SendMarketing();

        _logger.Entries.Should().Contain(entry => entry.Contains("Email sent"));
        _logger.Entries.Should().OnlyContain(entry => !entry.Contains(RecipientEmail));
    }

    [Fact]
    public async Task MarketingRetry_WarningLog_OmitsRecipientEmail()
    {
        _handler.StatusSequence = new Queue<HttpStatusCode>([HttpStatusCode.TooManyRequests, HttpStatusCode.OK]);

        await SendMarketing();

        _logger.Entries.Should().Contain(entry => entry.Contains("rate-limited"));
        _logger.Entries.Should().OnlyContain(entry => !entry.Contains(RecipientEmail));
    }

    [Fact]
    public async Task MarketingSendException_ErrorLog_OmitsRecipientEmail()
    {
        _handler.ExceptionToThrow = new HttpRequestException("Connection reset");

        await SendMarketing();

        _logger.Entries.Should().Contain(entry => entry.Contains("Email send exception"));
        _logger.Entries.Should().OnlyContain(entry => !entry.Contains(RecipientEmail));
    }

    [Fact]
    public async Task MarketingTestAccount_SkipLog_OmitsRecipientEmail() =>
        await WithTestAccountAsync(async () =>
        {
            await SendMarketing();

            _logger.Entries.Should().Contain(entry => entry.Contains("Skipping email"));
            _logger.Entries.Should().OnlyContain(entry => !entry.Contains(RecipientEmail));
        });

    [Fact]
    public async Task TransactionalTestAccount_SkipLog_OmitsRecipientEmail() =>
        await WithTestAccountAsync(async () =>
        {
            await _sut.SendVerificationCodeAsync(RecipientEmail, "123456");

            _logger.Entries.Should().Contain(entry => entry.Contains("Skipping email"));
            _logger.Entries.Should().OnlyContain(entry => !entry.Contains(RecipientEmail));
        });

    private Task SendMarketing() =>
        _sut.SendMarketingEmailAsync(
            RecipientEmail, "Product news", "<p>hi</p>", "en",
            "https://api.useorbit.org/api/marketing/unsubscribe?token=t");

    private static async Task WithTestAccountAsync(Func<Task> action)
    {
        Environment.SetEnvironmentVariable("TEST_ACCOUNTS", $"{RecipientEmail}:123456");
        try
        {
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_ACCOUNTS", null);
        }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK);
        public Queue<HttpStatusCode>? StatusSequence { get; set; }
        public Exception? ExceptionToThrow { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            if (StatusSequence is { Count: > 0 })
            {
                var status = StatusSequence.Count > 1 ? StatusSequence.Dequeue() : StatusSequence.Peek();
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
            }

            return Task.FromResult(ResponseToReturn);
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
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
