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

    [Fact]
    public async Task SendSupportEmailAsync_SendsToSupportEmail()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendSupportEmailAsync("John", "john@test.com", "Bug Report", "Found a bug");

        _handler.LastRequestBody.Should().Contain("[Orbit Support]");
        _handler.LastRequestBody.Should().Contain("reply_to");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task SendVerificationCodeAsync_IncludesPlainTextPart(string language)
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendVerificationCodeAsync("user@test.com", "123456", language);

        _handler.LastRequestBody.Should().Contain("\"text\":");
    }

    [Fact]
    public async Task SendVerificationCodeAsync_HtmlHasNoUnreplacedTokens()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendVerificationCodeAsync("user@test.com", "123456");

        _handler.LastRequestBody.Should().NotContain("{{");
    }

    [Fact]
    public async Task SendVerificationCodeAsync_IncludesLogoAndPreheader()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendVerificationCodeAsync("user@test.com", "123456");

        _handler.LastRequestBody.Should().Contain("logo-no-bg.png");
        _handler.LastRequestBody.Should().Contain("It expires in 5 minutes.");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_IncludesTextPartWithRawUserName()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendWelcomeEmailAsync("user@test.com", "Ana & Co");

        _handler.LastRequestBody.Should().Contain("\"text\":");
        _handler.LastRequestBody.Should().Contain("Welcome aboard, Ana \\u0026 Co!");
        _handler.LastRequestBody.Should().Contain("Welcome aboard, Ana \\u0026amp; Co!");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_UsesGradientHeaderWithSolidFallback()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendWelcomeEmailAsync("user@test.com", "Thomas");

        _handler.LastRequestBody.Should().Contain("#22094F");
    }

    [Fact]
    public async Task SendAccountDeletionCodeAsync_IncludesPlainTextPart()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendAccountDeletionCodeAsync("user@test.com", "654321", "en");

        _handler.LastRequestBody.Should().Contain("\"text\":");
        _handler.LastRequestBody.Should().Contain("654321");
    }

    [Fact]
    public async Task SendSupportEmailAsync_AdoptsSharedLayoutWithTextPart()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendSupportEmailAsync("John", "john@test.com", "Bug Report", "Found a bug");

        _handler.LastRequestBody.Should().Contain("logo-no-bg.png");
        _handler.LastRequestBody.Should().Contain("\"text\":");
        _handler.LastRequestBody.Should().Contain("Reply directly to respond to the user.");
    }

    [Fact]
    public async Task SendSupportEmailAsync_EncodesHtmlInUserContent()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendSupportEmailAsync("<b>John</b>", "john@test.com", "Bug", "line1\nline2");

        _handler.LastRequestBody.Should().Contain("\\u0026lt;b\\u0026gt;John\\u0026lt;/b\\u0026gt;");
        _handler.LastRequestBody.Should().Contain("line1\\u003Cbr\\u003Eline2");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_UserNameWithTokenSyntax_IsNotSubstituted()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);

        await _sut.SendWelcomeEmailAsync("user@test.com", "{{footer}}");

        _handler.LastRequestBody.Should().Contain("Welcome aboard, {{footer}}!");
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
