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

[Collection("ProcessEnvironment")]
public sealed class SesEmailServiceReviewAccountTests
{
    private const string ReviewEmail = "store-review@example.com";
    private const string ReviewCode = "135791";

    private readonly IAmazonSimpleEmailServiceV2 _client = Substitute.For<IAmazonSimpleEmailServiceV2>();
    private readonly SesEmailService _service;

    public SesEmailServiceReviewAccountTests()
    {
        _client.SendEmailAsync(Arg.Any<SendEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SendEmailResponse()));
        _service = new SesEmailService(_client,
            Options.Create(new SesSettings { SupportEmail = "support@useorbit.org", MarketingRetryBaseDelayMs = 1 }),
            Options.Create(new FrontendSettings { BaseUrl = "https://app.useorbit.org" }),
            NullLogger<SesEmailService>.Instance);
    }

    [Theory]
    [InlineData("verification")]
    [InlineData("welcome")]
    [InlineData("marketing")]
    public async Task Production_ReviewAccount_SendsNothing(string kind)
    {
        await WithEnv("Production", ReviewEmail, ReviewCode, () => Send(kind, " Store-Review@Example.com "));

        await _client.DidNotReceive().SendEmailAsync(Arg.Any<SendEmailRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Production_ReviewAccountConfigured_OtherAddress_IsSent()
    {
        await WithEnv("Production", ReviewEmail, ReviewCode, () => Send("verification", "someone-else@example.com"));

        await _client.Received(1).SendEmailAsync(
            Arg.Is<SendEmailRequest>(request => request.Destination.ToAddresses.Single() == "someone-else@example.com"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Production", ReviewEmail, null)]
    [InlineData("Production", ReviewEmail, "13579a")]
    [InlineData("Production", null, ReviewCode)]
    [InlineData("Staging", ReviewEmail, ReviewCode)]
    [InlineData("Development", ReviewEmail, ReviewCode)]
    public async Task ReviewAccountWithoutSeededCode_IsSent(string environmentName, string? reviewEmail, string? reviewCode)
    {
        await WithEnv(environmentName, reviewEmail, reviewCode, () => Send("verification", ReviewEmail));

        await _client.Received(1).SendEmailAsync(Arg.Any<SendEmailRequest>(), Arg.Any<CancellationToken>());
    }

    private Task Send(string kind, string to) => kind switch
    {
        "verification" => _service.SendVerificationCodeAsync(to, "123456"),
        "welcome" => _service.SendWelcomeEmailAsync(to, "Reviewer"),
        _ => _service.SendMarketingEmailAsync(to, "News", "<p>Update</p>", "en", "https://api.useorbit.org/u"),
    };

    private static async Task WithEnv(string environmentName, string? reviewEmail, string? reviewCode, Func<Task> body)
    {
        var names = new[] { "ASPNETCORE_ENVIRONMENT", "REVIEW_ACCOUNT_EMAIL", "REVIEW_ACCOUNT_CODE" };
        var prior = names.Select(Environment.GetEnvironmentVariable).ToArray();
        Environment.SetEnvironmentVariable(names[0], environmentName);
        Environment.SetEnvironmentVariable(names[1], reviewEmail);
        Environment.SetEnvironmentVariable(names[2], reviewCode);
        try
        {
            await body();
        }
        finally
        {
            for (var index = 0; index < names.Length; index++)
                Environment.SetEnvironmentVariable(names[index], prior[index]);
        }
    }
}
