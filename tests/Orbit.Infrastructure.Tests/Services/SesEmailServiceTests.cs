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
            Name = "List-Unsubscribe", Value = $"<{url}>"
        });
        request.Content.Simple.Headers.Should().ContainEquivalentOf(new MessageHeader
        {
            Name = "List-Unsubscribe-Post", Value = "List-Unsubscribe=One-Click"
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
}
