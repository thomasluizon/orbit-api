using System.Collections.Concurrent;
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Marketing;

public class SendMarketingBroadcastCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly RecordingEmailService _emailService = new();
    private readonly IMarketingUnsubscribeTokenService _tokenService = Substitute.For<IMarketingUnsubscribeTokenService>();
    private readonly SendMarketingBroadcastCommandHandler _handler;

    public SendMarketingBroadcastCommandHandlerTests()
    {
        _tokenService.CreateToken(Arg.Any<Guid>()).Returns(call => $"token-{call.Arg<Guid>():N}");

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IEmailService)).Returns(_emailService);
        provider.GetService(typeof(IMarketingUnsubscribeTokenService)).Returns(_tokenService);
        provider.GetService(typeof(ILogger<SendMarketingBroadcastCommandHandler>))
            .Returns(NullLogger<SendMarketingBroadcastCommandHandler>.Instance);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var settings = Options.Create(new MarketingSettings
        {
            ApiBaseUrl = "https://api.useorbit.org",
            SendDelayMilliseconds = 0,
        });

        _handler = new SendMarketingBroadcastCommandHandler(
            _userRepo, _emailService, _tokenService, scopeFactory, settings,
            NullLogger<SendMarketingBroadcastCommandHandler>.Instance);
    }

    private static SendMarketingBroadcastCommand Command(string? testEmail = null) =>
        new("EN Subject", "PT Assunto", "<p>EN body</p>", "<p>PT corpo</p>", testEmail);

    private void AudienceReturns(params User[] users) =>
        _userRepo.FindAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(users);

    private static async Task WaitForSendCountAsync(RecordingEmailService email, int expected)
    {
        for (var i = 0; i < 100 && email.MarketingSends.Count < expected; i++)
            await Task.Delay(20);
    }

    [Fact]
    public async Task Handle_FiltersToConsentingUsers_AndRendersPerUserLanguage()
    {
        var enUser = User.Create("En", "en@example.com").Value;
        var ptUser = User.Create("Pt", "pt@example.com").Value;
        ptUser.SetLanguage("pt-BR");

        Expression<Func<User, bool>>? capturedPredicate = null;
        _userRepo.FindAsync(
                Arg.Do<Expression<Func<User, bool>>>(predicate => capturedPredicate = predicate),
                Arg.Any<CancellationToken>())
            .Returns([enUser, ptUser]);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RecipientCount.Should().Be(2);
        result.Value.WasTest.Should().BeFalse();

        capturedPredicate.Should().NotBeNull();
        var predicate = capturedPredicate!.Compile();
        var consenting = User.Create("C", "c@example.com").Value;
        consenting.SetMarketingConsent(true);
        var optedOut = User.Create("O", "o@example.com").Value;
        optedOut.SetMarketingConsent(false);
        var neverAsked = User.Create("N", "n@example.com").Value;
        predicate(consenting).Should().BeTrue();
        predicate(optedOut).Should().BeFalse();
        predicate(neverAsked).Should().BeFalse();

        await WaitForSendCountAsync(_emailService, 2);
        _emailService.MarketingSends.Should().HaveCount(2);

        var enSend = _emailService.MarketingSends.Single(send => send.To == "en@example.com");
        enSend.Subject.Should().Be("EN Subject");
        enSend.Body.Should().Be("<p>EN body</p>");
        enSend.UnsubscribeUrl.Should().Contain("/api/marketing/unsubscribe?token=");

        var ptSend = _emailService.MarketingSends.Single(send => send.To == "pt@example.com");
        ptSend.Subject.Should().Be("PT Assunto");
        ptSend.Body.Should().Be("<p>PT corpo</p>");
    }

    [Fact]
    public async Task Handle_TestEmail_SendsExactlyOne_AndDoesNotEnumerateAudience()
    {
        var result = await _handler.Handle(Command(testEmail: "preview@example.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RecipientCount.Should().Be(1);
        result.Value.WasTest.Should().BeTrue();

        _emailService.MarketingSends.Should().ContainSingle();
        _emailService.MarketingSends.Single().To.Should().Be("preview@example.com");
        _emailService.MarketingSends.Single().Subject.Should().Be("EN Subject");

        await _userRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoConsentingUsers_QueuesZeroAndSendsNothing()
    {
        AudienceReturns();

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Value.RecipientCount.Should().Be(0);
        await Task.Delay(60);
        _emailService.MarketingSends.Should().BeEmpty();
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public ConcurrentBag<(string To, string Subject, string Body, string Language, string UnsubscribeUrl)> MarketingSends { get; } = [];

        public Task SendMarketingEmailAsync(string toEmail, string subject, string bodyHtml, string language, string unsubscribeUrl, CancellationToken cancellationToken = default)
        {
            MarketingSends.Add((toEmail, subject, bodyHtml, language, unsubscribeUrl));
            return Task.CompletedTask;
        }

        public Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendVerificationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendSupportEmailAsync(string fromName, string fromEmail, string subject, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAccountDeletionCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendWaitlistConfirmationAsync(string toEmail, string confirmUrl, string language = "en", CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
