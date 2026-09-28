using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ResendEmailServiceTests
{
    private readonly ResendEmailService _sut;
    private readonly FakeHttpMessageHandler _handler;

    public ResendEmailServiceTests()
    {
        var resendSettings = Options.Create(new ResendSettings
        {
            ApiKey = "re_test_key",
            FromEmail = "noreply@useorbit.org",
            SupportEmail = "contact@useorbit.org"
        });

        var frontendSettings = Options.Create(new FrontendSettings
        {
            BaseUrl = "https://app.useorbit.org"
        });

        _handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.resend.com") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Resend").Returns(httpClient);

        var logger = new NullLoggerFactory().CreateLogger<ResendEmailService>();
        _sut = new ResendEmailService(factory, resendSettings, frontendSettings, logger);
    }

    [Fact]
    public async Task SendVerificationCodeAsync_SuccessfulResponse_DoesNotThrow()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        var act = () => _sut.SendVerificationCodeAsync("user@test.com", "123456");
        await act.Should().NotThrowAsync();

        _handler.LastRequest.Should().NotBeNull();
        _handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/emails");
    }

    [Fact]
    public async Task SendVerificationCodeAsync_ApiFailure_DoesNotThrow()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server error")
        };

        var act = () => _sut.SendVerificationCodeAsync("user@test.com", "123456");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendVerificationCodeAsync_AuthError401_HandledWithoutRetry()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"message\":\"Invalid API key\"}")
        };

        var act = () => _sut.SendVerificationCodeAsync("real.user@example.com", "123456");

        await act.Should().NotThrowAsync();
        _handler.LastRequest.Should().NotBeNull();
        _handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendVerificationCodeAsync_InvalidEmailAddress422_HandledWithoutRetry()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"name\":\"validation_error\",\"message\":\"Invalid `to` field\"}")
        };

        var act = () => _sut.SendVerificationCodeAsync("not-an-email", "123456");

        await act.Should().NotThrowAsync();
        _handler.LastRequest.Should().NotBeNull();
        _handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendVerificationCodeAsync_TransientServerError_RetriesUpToPolicyLimit()
    {
        _handler.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var act = () => _sut.SendVerificationCodeAsync("real.user@example.com", "123456");

        await act.Should().NotThrowAsync();
        _handler.CallCount.Should().Be(1 + Orbit.Infrastructure.Common.HttpRetryPolicy.MaxRetries);
    }

    [Fact]
    public async Task SendVerificationCodeAsync_HttpException_DoesNotThrow()
    {
        _handler.ExceptionToThrow = new HttpRequestException("Connection failed");

        var act = () => _sut.SendVerificationCodeAsync("user@test.com", "123456");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendVerificationCodeAsync_ClientCancellation_RethrowsInsteadOfSwallowing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _handler.ExceptionToThrow = new OperationCanceledException(cts.Token);

        var act = () => _sut.SendVerificationCodeAsync("user@test.com", "123456", "en", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SendVerificationCodeAsync_TimeoutNotClientCancellation_DoesNotThrow()
    {
        _handler.ExceptionToThrow = new TaskCanceledException("Resend timed out");

        var act = () => _sut.SendVerificationCodeAsync("user@test.com", "123456");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_SuccessfulResponse_SendsEmail()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendWelcomeEmailAsync("user@test.com", "Alex");

        _handler.LastRequest.Should().NotBeNull();
        _handler.LastRequestBody.Should().Contain("Welcome aboard");
    }

    [Fact]
    public async Task SendSupportEmailAsync_SendsToSupportEmail()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendSupportEmailAsync("John", "john@test.com", "Bug Report", "Found a bug");

        _handler.LastRequestBody.Should().Contain("[Orbit Support]");
        _handler.LastRequestBody.Should().Contain("reply_to");
    }

    [Fact]
    public async Task SendVerificationCodeAsync_TestAccount_SkipsSend()
    {
        Environment.SetEnvironmentVariable("TEST_ACCOUNTS", "testaccount@test.com:123456");
        try
        {
            _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

            await _sut.SendVerificationCodeAsync("testaccount@test.com", "123456");

            _handler.LastRequest.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_ACCOUNTS", null);
        }
    }

    [Fact]
    public async Task SendVerificationCodeAsync_NonTestAccount_SendsEmail()
    {
        Environment.SetEnvironmentVariable("TEST_ACCOUNTS", "other@test.com:111111");
        try
        {
            _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

            await _sut.SendVerificationCodeAsync("real@user.com", "123456");

            _handler.LastRequest.Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_ACCOUNTS", null);
        }
    }

    [Fact]
    public async Task SendVerificationCodeAsync_EmptyTestAccountsEnv_SendsEmail()
    {
        Environment.SetEnvironmentVariable("TEST_ACCOUNTS", null);
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendVerificationCodeAsync("user@test.com", "123456");

        _handler.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task SendWaitlistConfirmationAsync_English_SendsEnglishSubjectAndConfirmUrl()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendWaitlistConfirmationAsync(
            "user@test.com",
            "https://api.useorbit.org/api/waitlist/confirm?token=abc.def",
            "en");

        _handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/emails");
        _handler.LastRequestBody.Should().Contain("Confirm your spot on the Orbit iOS waitlist");
        _handler.LastRequestBody.Should().Contain("https://api.useorbit.org/api/waitlist/confirm?token=abc.def");
        _handler.LastRequestBody.Should().Contain("\"text\":");
        _handler.LastRequestBody.Should().NotContain("{{");
    }

    /// <summary>
    /// Fake HttpMessageHandler for intercepting HTTP calls.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK);
        public Func<HttpResponseMessage>? ResponseFactory { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestBody { get; private set; } = "";
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;

            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return ResponseFactory?.Invoke() ?? ResponseToReturn;
        }
    }
}
