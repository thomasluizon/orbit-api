using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class SesEmailServiceTests
{
    private readonly IAmazonSimpleEmailServiceV2 _client = Substitute.For<IAmazonSimpleEmailServiceV2>();
    private readonly List<SendEmailRequest> _requests = [];
    private readonly SesEmailService _service;

    public SesEmailServiceTests()
    {
        _client.SendEmailAsync(Arg.Do<SendEmailRequest>(_requests.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SendEmailResponse()));
        _service = new SesEmailService(_client,
            Options.Create(new SesSettings { SupportEmail = "support@useorbit.org", MarketingRetryBaseDelayMs = 1 }),
            Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" }),
            NullLogger<SesEmailService>.Instance);
    }

    [Theory]
    [InlineData("welcome", "Welcome to Orbit")]
    [InlineData("verification", "sign-in code")]
    [InlineData("deletion", "delete your Orbit account")]
    [InlineData("api-key", "API key")]
    [InlineData("waitlist", "Orbit iOS list")]
    public async Task TransactionalEmailsUseExpectedSesRequest(string kind, string subjectFragment)
    {
        const string to = "recipient@example.com";
        switch (kind)
        {
            case "welcome": await _service.SendWelcomeEmailAsync(to, "Taylor"); break;
            case "verification": await _service.SendVerificationCodeAsync(to, "123456"); break;
            case "deletion": await _service.SendAccountDeletionCodeAsync(to, "123456"); break;
            case "api-key": await _service.SendApiKeyCreationCodeAsync(to, "123456"); break;
            case "waitlist": await _service.SendWaitlistConfirmationAsync(to, "https://useorbit.org/confirm"); break;
        }

        _requests.Should().ContainSingle();
        var request = _requests[0];
        request.FromEmailAddress.Should().Be("Orbit <noreply@send.useorbit.org>");
        request.Destination.ToAddresses.Should().ContainSingle().Which.Should().Be(to);
        request.ConfigurationSetName.Should().Be("orbit-transactional");
        request.Content.Simple.Subject.Data.Should().ContainEquivalentOf(subjectFragment);
        request.Content.Simple.Body.Html.Data.Should().NotBeNullOrWhiteSpace();
        request.Content.Simple.Body.Text.Data.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SupportEmailUsesReplyToAndSupportDestination()
    {
        await _service.SendSupportEmailAsync("Taylor", "taylor@example.com", "Help", "Question");
        var request = _requests.Should().ContainSingle().Which;
        request.FromEmailAddress.Should().Be("Orbit <noreply@send.useorbit.org>");
        request.Destination.ToAddresses.Should().ContainSingle().Which.Should().Be("support@useorbit.org");
        request.ConfigurationSetName.Should().Be("orbit-transactional");
        request.Content.Simple.Subject.Data.Should().Be("[Orbit Support] Help");
        request.ReplyToAddresses.Should().ContainSingle().Which.Should().Be("taylor@example.com");
    }

    [Fact]
    public async Task MarketingEmailUsesMarketingIdentityAndUnsubscribeHeaders()
    {
        const string url = "https://api.useorbit.org/api/marketing/unsubscribe?token=abc";
        await _service.SendMarketingEmailAsync("recipient@example.com", "News", "<p>Update</p>", "en", url);
        var request = _requests.Should().ContainSingle().Which;
        request.FromEmailAddress.Should().Be("Orbit <news@updates.useorbit.org>");
        request.Destination.ToAddresses.Should().ContainSingle().Which.Should().Be("recipient@example.com");
        request.ConfigurationSetName.Should().Be("orbit-marketing");
        request.Content.Simple.Subject.Data.Should().Be("News");
        request.Content.Simple.Body.Html.Data.Should().Contain(url);
        request.Content.Simple.Headers.Should().ContainEquivalentOf(new MessageHeader
        {
            Name = "List-Unsubscribe",
            Value = $"<{url}>"
        });
        request.Content.Simple.Headers.Should().ContainEquivalentOf(new MessageHeader
        {
            Name = "List-Unsubscribe-Post",
            Value = "List-Unsubscribe=One-Click"
        });
    }

    [Fact]
    public async Task MarketingThrottlingRetriesThenStops()
    {
        _client.SendEmailAsync(Arg.Any<SendEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SendEmailResponse>(new TooManyRequestsException("throttled")));
        await _service.SendMarketingEmailAsync("recipient@example.com", "News", "<p>Update</p>", "en", "https://api.useorbit.org/u");
        await _client.Received(5).SendEmailAsync(Arg.Any<SendEmailRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendVerificationCodeAsync_PortugueseLanguage_SendsPortugueseSubject()
    {
        await _service.SendVerificationCodeAsync("user@test.com", "123456", "pt-BR");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;

        message.Subject.Data.Should().Contain("Seu código de acesso do Orbit");
    }

    [Fact]
    public async Task SendVerificationCodeAsync_EnglishLanguage_SendsEnglishSubject()
    {
        await _service.SendVerificationCodeAsync("user@test.com", "123456", "en");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;

        message.Subject.Data.Should().Contain("Your Orbit sign-in code");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_SuccessfulResponse_SendsEmail()
    {
        await _service.SendWelcomeEmailAsync("user@test.com", "Alex");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        body.Should().Contain("You are in");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_Portuguese_SendsPortugueseContent()
    {
        await _service.SendWelcomeEmailAsync("user@test.com", "Alex", "pt-BR");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;

        message.Subject.Data.Should().Contain("Boas-vindas");
    }

    [Fact]
    public async Task SendAccountDeletionCodeAsync_English_SendsEnglish()
    {
        await _service.SendAccountDeletionCodeAsync("user@test.com", "654321", "en");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;

        message.Subject.Data.Should().Contain("Confirm that you want to delete your Orbit account");
    }

    [Fact]
    public async Task SendAccountDeletionCodeAsync_Portuguese_SendsPortuguese()
    {
        await _service.SendAccountDeletionCodeAsync("user@test.com", "654321", "pt");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;

        message.Subject.Data.Should().Contain("Confirme a exclus");
    }

    [Fact]
    public async Task SendApiKeyCreationCodeAsync_English_UsesApiKeyWording()
    {
        await _service.SendApiKeyCreationCodeAsync("user@test.com", "654321", "en");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        message.Subject.Data.Should().Contain("Confirm your new Orbit API key");
        body.Should().Contain("No key is created.");
        body.Should().NotContain("account deletion");
    }

    [Fact]
    public async Task SendApiKeyCreationCodeAsync_Portuguese_UsesApiKeyWording()
    {
        await _service.SendApiKeyCreationCodeAsync("user@test.com", "654321", "pt-BR");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        body.Should().Contain("chave de API do Orbit");
        body.Should().NotContain("exclus");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task SendVerificationCodeAsync_IncludesPlainTextPart(string language)
    {
        await _service.SendVerificationCodeAsync("user@test.com", "123456", language);

        var message = _requests.Should().ContainSingle().Which.Content.Simple;

        message.Body.Text.Data.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SendVerificationCodeAsync_HtmlHasNoUnreplacedTokens()
    {
        await _service.SendVerificationCodeAsync("user@test.com", "123456");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        body.Should().NotContain("{{");
    }

    [Fact]
    public async Task SendVerificationCodeAsync_IncludesLogoAndPreheader()
    {
        await _service.SendVerificationCodeAsync("user@test.com", "123456");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        body.Should().Contain("logo-no-bg.png");
        body.Should().Contain("It works for the next 5 minutes.");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_IncludesTextPartWithRawUserName()
    {
        await _service.SendWelcomeEmailAsync("user@test.com", "Ana & Co");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        message.Body.Text.Data.Should().NotBeNullOrWhiteSpace();
        message.Body.Text.Data.Should().Contain("You are in, Ana & Co");
        body.Should().Contain("You are in, Ana &amp; Co");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_CarriesNoBannedGradientOrGlow()
    {
        await _service.SendWelcomeEmailAsync("user@test.com", "Alex");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        body.Should().NotContain("#22094F");
        body.Should().NotContain("linear-gradient");
        body.Should().NotContain("box-shadow");
    }

    [Fact]
    public async Task SendAccountDeletionCodeAsync_IncludesPlainTextPart()
    {
        await _service.SendAccountDeletionCodeAsync("user@test.com", "654321", "en");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        message.Body.Text.Data.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("654321");
    }

    [Fact]
    public async Task SendApiKeyCreationCodeAsync_IncludesPlainTextPart()
    {
        await _service.SendApiKeyCreationCodeAsync("user@test.com", "654321", "en");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        message.Body.Text.Data.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("654321");
    }

    [Fact]
    public async Task SendSupportEmailAsync_AdoptsSharedLayoutWithTextPart()
    {
        await _service.SendSupportEmailAsync("John", "john@test.com", "Bug Report", "Found a bug");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        body.Should().Contain("logo-no-bg.png");
        message.Body.Text.Data.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("Reply to this email to answer them.");
    }

    [Fact]
    public async Task SendSupportEmailAsync_EncodesHtmlInUserContent()
    {
        await _service.SendSupportEmailAsync("<b>John</b>", "john@test.com", "Bug", "line1\nline2");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        body.Should().Contain("&lt;b&gt;John&lt;/b&gt;");
        body.Should().Contain("line1<br>line2");
    }

    [Fact]
    public async Task SendWelcomeEmailAsync_UserNameWithTokenSyntax_IsNotSubstituted()
    {
        await _service.SendWelcomeEmailAsync("user@test.com", "{{footer}}");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        body.Should().Contain("You are in, {{footer}}");
    }

    [Fact]
    public async Task SendWaitlistConfirmationAsync_English_SendsEnglishSubjectAndConfirmUrl()
    {
        await _service.SendWaitlistConfirmationAsync(
            "user@test.com",
            "https://api.useorbit.org/api/waitlist/confirm?token=abc.def",
            "en");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;
        var body = message.Body.Html.Data;

        message.Subject.Data.Should().Contain("Confirm your place on the Orbit iOS list");
        body.Should().Contain("https://api.useorbit.org/api/waitlist/confirm?token=abc.def");
        message.Body.Text.Data.Should().NotBeNullOrWhiteSpace();
        body.Should().NotContain("{{");
    }

    [Fact]
    public async Task SendWaitlistConfirmationAsync_Portuguese_SendsPortugueseSubject()
    {
        await _service.SendWaitlistConfirmationAsync(
            "user@test.com",
            "https://api.useorbit.org/api/waitlist/confirm?token=abc.def",
            "pt-BR");

        var message = _requests.Should().ContainSingle().Which.Content.Simple;

        message.Subject.Data.Should().Contain("Confirme sua vaga na lista do Orbit");
    }

}
