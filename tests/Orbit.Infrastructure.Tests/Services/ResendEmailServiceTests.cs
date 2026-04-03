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
            SupportEmail = "support@useorbit.org"
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

    // --- SendVerificationCodeAsync ---

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
    public async Task SendVerificationCodeAsync_PortugueseLanguage_SendsPortugueseSubject()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendVerificationCodeAsync("user@test.com", "123456", "pt-BR");

        _handler.LastRequestBody.Should().Contain("Seu c\\u00F3digo de verifica\\u00E7\\u00E3o do Orbit");
    }

    [Fact]
    public async Task SendVerificationCodeAsync_EnglishLanguage_SendsEnglishSubject()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendVerificationCodeAsync("user@test.com", "123456", "en");

        _handler.LastRequestBody.Should().Contain("Your Orbit verification code");
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
    public async Task SendVerificationCodeAsync_HttpException_DoesNotThrow()
    {
        _handler.ExceptionToThrow = new HttpRequestException("Connection failed");

        var act = () => _sut.SendVerificationCodeAsync("user@test.com", "123456");
        await act.Should().NotThrowAsync();
    }

    // --- SendWelcomeEmailAsync ---

    [Fact]
    public async Task SendWelcomeEmailAsync_SuccessfulResponse_SendsEmail()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendWelcomeEmailAsync("user@test.com", "Thomas");

        _handler.LastRequest.Should().NotBeNull();
        _handler.LastRequestBody.Should().Contain("Welcome aboard");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_Portuguese_SendsPortugueseContent()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendWelcomeEmailAsync("user@test.com", "Thomas", "pt-BR");

        _handler.LastRequestBody.Should().Contain("Boas-vindas");
    }

    // --- SendAccountDeletionCodeAsync ---

    [Fact]
    public async Task SendAccountDeletionCodeAsync_English_SendsEnglish()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendAccountDeletionCodeAsync("user@test.com", "654321", "en");

        _handler.LastRequestBody.Should().Contain("Confirm your Orbit account deletion");
    }

    [Fact]
    public async Task SendAccountDeletionCodeAsync_Portuguese_SendsPortuguese()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendAccountDeletionCodeAsync("user@test.com", "654321", "pt");

        _handler.LastRequestBody.Should().Contain("Confirme a exclus");
    }

    // --- SendSupportEmailAsync ---

    [Fact]
    public async Task SendSupportEmailAsync_SendsToSupportEmail()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendSupportEmailAsync("John", "john@test.com", "Bug Report", "Found a bug");

        _handler.LastRequestBody.Should().Contain("[Orbit Support]");
        _handler.LastRequestBody.Should().Contain("reply_to");
    }

    // --- Test account detection ---

    [Fact]
    public async Task SendVerificationCodeAsync_TestAccount_SkipsSend()
    {
        // Set up a test account environment variable
        Environment.SetEnvironmentVariable("TEST_ACCOUNTS", "testaccount@test.com:123456");
        try
        {
            _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

            await _sut.SendVerificationCodeAsync("testaccount@test.com", "123456");

            // Should not have made any HTTP request since it's a test account
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

    /// <summary>
    /// Fake HttpMessageHandler for intercepting HTTP calls.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK);
        public Exception? ExceptionToThrow { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return ResponseToReturn;
        }
    }
}
