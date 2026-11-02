using System.Collections.Concurrent;
using System.Linq.Expressions;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Commands;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Marketing;

public class SendMarketingBroadcastCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<MarketingContact> _contactRepo = Substitute.For<IGenericRepository<MarketingContact>>();
    private readonly RecordingEmailService _emailService = new();
    private readonly IMarketingUnsubscribeTokenService _tokenService = Substitute.For<IMarketingUnsubscribeTokenService>();
    private readonly IBackgroundJobClient _backgroundJobClient = Substitute.For<IBackgroundJobClient>();
    private readonly SendMarketingBroadcastCommandHandler _handler;
    private Job? _enqueuedJob;
    private readonly List<Job> _pages = [];
    private User[] _audience = [];
    private MarketingContact[] _contacts = [];
    private readonly MarketingSettings _settings = new() { ApiBaseUrl = "https://api.useorbit.org", SendDelayMilliseconds = 0 };

    public SendMarketingBroadcastCommandHandlerTests()
    {
        _userRepo.FindIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = _audience.AsQueryable().Where(call.Arg<Expression<Func<User, bool>>>());
                var shape = call.Arg<Func<IQueryable<User>, IQueryable<User>>?>();
                return (shape is null ? query : shape(query)).ToArray();
            });
        _contactRepo.FindAsync(Arg.Any<Expression<Func<MarketingContact, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => _contacts.Where(call.Arg<Expression<Func<MarketingContact, bool>>>().Compile()).ToArray());
        _contactRepo.FindAsync(Arg.Any<Expression<Func<MarketingContact, bool>>>(), Arg.Any<Func<IQueryable<MarketingContact>, IQueryable<MarketingContact>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<IQueryable<MarketingContact>, IQueryable<MarketingContact>>>()(
                _contacts.AsQueryable().Where(call.Arg<Expression<Func<MarketingContact, bool>>>())).ToArray());
        _tokenService.CreateToken(Arg.Any<Guid>()).Returns(call => $"token-{call.Arg<Guid>():N}");
        _backgroundJobClient.Create(Arg.Do<Job>(job => { _enqueuedJob = job; _pages.Add(job); }), Arg.Any<IState>());

        _handler = new SendMarketingBroadcastCommandHandler(
            _userRepo, _contactRepo, _backgroundJobClient, _tokenService, Options.Create(_settings),
            NullLogger<SendMarketingBroadcastCommandHandler>.Instance);
    }

    private static SendMarketingBroadcastCommand Command(string? testEmail = null) =>
        new("EN Subject", "PT Assunto", "<p>EN body</p>", "<p>PT corpo</p>", testEmail);

    private void AudienceReturns(params User[] users) => _audience = users;
    private void ContactsReturn(params MarketingContact[] contacts) => _contacts = contacts;

    private async Task WaitForSendCountAsync(RecordingEmailService email, int expected)
    {
        email.MarketingSends.Should().BeEmpty();
        foreach (var queued in _pages.Where(job => job.Type == typeof(SendMarketingBroadcastPageJob)))
            await new SendMarketingBroadcastPageJob(email, _tokenService, Options.Create(_settings), TimeProvider.System)
                .ExecuteAsync((Guid)queued.Args[0], (int)queued.Args[1], (SendMarketingBroadcastCommand)queued.Args[2],
                    (MarketingRecipient[])queued.Args[3], default);
        email.MarketingSends.Should().HaveCount(expected);
    }

    [Fact]
    public async Task Handle_FiltersToConsentingUsers_AndRendersPerUserLanguage()
    {
        var enUser = User.Create("En", "en@example.com").Value;
        var ptUser = User.Create("Pt", "pt@example.com").Value;
        enUser.SetMarketingConsent(true);
        ptUser.SetMarketingConsent(true);
        ptUser.SetLanguage("pt-BR");

        Expression<Func<User, bool>>? capturedPredicate = null;
        _userRepo.FindIgnoringFiltersAsync(
                Arg.Do<Expression<Func<User, bool>>>(predicate => capturedPredicate = predicate),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<IQueryable<User>, IQueryable<User>>>()(
                new[] { enUser, ptUser }.AsQueryable().Where(call.Arg<Expression<Func<User, bool>>>())).ToArray());

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
    public async Task Handle_TestEmail_EnqueuesExactlyOne_AndDoesNotEnumerateAudience()
    {
        var result = await _handler.Handle(Command(testEmail: "preview@example.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RecipientCount.Should().Be(1);
        result.Value.WasTest.Should().BeTrue();

        _backgroundJobClient.Received(1).Create(
            Arg.Any<Job>(), Arg.Is<IState>(state => state is EnqueuedState));
        _enqueuedJob.Should().NotBeNull();
        _enqueuedJob!.Type.Should().Be<SendMarketingEmailJob>();
        _enqueuedJob.Method.Name.Should().Be(nameof(SendMarketingEmailJob.ExecuteAsync));
        _enqueuedJob.Args.Should().Equal(
            "preview@example.com",
            "EN Subject",
            "<p>EN body</p>",
            "en",
            "https://api.useorbit.org/api/marketing/unsubscribe?token=token-00000000000000000000000000000000&lang=en");

        _emailService.MarketingSends.Should().BeEmpty();

        await _userRepo.DidNotReceive().FindIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task Handle_TestEmail_PreviewLogDoesNotLeakTestEmail()
    {
        var logger = new CollectingLogger<SendMarketingBroadcastCommandHandler>();
        var handler = new SendMarketingBroadcastCommandHandler(
            _userRepo, _contactRepo, _backgroundJobClient, _tokenService,
            Options.Create(new MarketingSettings { ApiBaseUrl = "https://api.useorbit.org", SendDelayMilliseconds = 0 }),
            logger);

        var command = new SendMarketingBroadcastCommand(
            "EN Subject", "PT Assunto", "<p>EN body</p>", "<p>PT corpo</p>", "preview-pii@example.com");
        await handler.Handle(command, CancellationToken.None);

        var previewLog = logger.Entries.Should()
            .ContainSingle(entry => entry.Contains("preview sent")).Subject;
        previewLog.Should().NotContain("preview-pii@example.com");
        previewLog.Should().Contain("EN Subject");
    }

    [Fact]
    public async Task Handle_OneRecipientSendFails_ContinuesWithTheRest()
    {
        var first = User.Create("First", "first@example.com").Value;
        var second = User.Create("Second", "second@example.com").Value;
        first.SetMarketingConsent(true);
        second.SetMarketingConsent(true);
        _emailService.FailForEmail = "first@example.com";
        AudienceReturns(first, second);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Value.RecipientCount.Should().Be(2);
        var delivery = () => WaitForSendCountAsync(_emailService, 1);
        await delivery.Should().ThrowAsync<AggregateException>();
        _emailService.MarketingSends.Should().ContainSingle(send => send.To == "second@example.com");
    }

    [Fact]
    public async Task Handle_SharedUserAndContactEmail_SendsOnce()
    {
        var user = User.Create("Person", "person@example.com").Value;
        user.SetMarketingConsent(true);
        AudienceReturns(user);
        ContactsReturn(MarketingContact.ConfirmWaitlist("PERSON@example.com", "pt-BR"));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Value.RecipientCount.Should().Be(1);
        await WaitForSendCountAsync(_emailService, 1);
        _emailService.MarketingSends.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_SkipsUnsubscribedAndSuppressedAddresses()
    {
        AudienceReturns();
        var eligible = MarketingContact.ConfirmWaitlist("eligible@example.com", "en");
        var unsubscribed = MarketingContact.ConfirmWaitlist("unsubscribed@example.com", "en");
        var suppressed = MarketingContact.ConfirmWaitlist("suppressed@example.com", "en");
        unsubscribed.Unsubscribe();
        suppressed.Suppress();
        ContactsReturn(eligible, unsubscribed, suppressed);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Value.RecipientCount.Should().Be(1);
        await WaitForSendCountAsync(_emailService, 1);
        _emailService.MarketingSends.Should().ContainSingle(send => send.To == "eligible@example.com");
    }

    [Fact]
    public async Task Handle_TwelveHundredRecipients_EnqueuesThreeDurablePagesBeforeAnyEmail()
    {
        var users = Enumerable.Range(0, 1200).Select(index =>
        {
            var user = User.Create($"User {index}", $"u{index}@example.com").Value;
            user.SetMarketingConsent(true);
            return user;
        }).ToArray();
        AudienceReturns(users);

        var result = await _handler.Handle(Command(), default);

        result.Value.RecipientCount.Should().Be(1200);
        _backgroundJobClient.Received(3).Create(Arg.Any<Job>(), Arg.Any<IState>());
        _emailService.MarketingSends.Should().BeEmpty();
        _pages.Select(job => ((MarketingRecipient[])job.Args[3]).Length).Should().Equal(500, 500, 200);
        _pages.Select(job => (Guid)job.Args[0]).Distinct().Should().ContainSingle();
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

    private sealed class RecordingEmailService : IEmailService
    {
        public ConcurrentBag<(string To, string Subject, string Body, string Language, string UnsubscribeUrl)> MarketingSends { get; } = [];
        public string? FailForEmail { get; set; }

        public Task SendMarketingEmailAsync(string toEmail, string subject, string bodyHtml, string language, string unsubscribeUrl, CancellationToken cancellationToken = default)
        {
            if (toEmail == FailForEmail)
                throw new InvalidOperationException("send failed");

            MarketingSends.Add((toEmail, subject, bodyHtml, language, unsubscribeUrl));
            return Task.CompletedTask;
        }

        public Task SendWelcomeEmailAsync(string toEmail, string userName, string language = "en", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendVerificationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendSupportEmailAsync(string fromName, string fromEmail, string subject, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAccountDeletionCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendApiKeyCreationCodeAsync(string toEmail, string code, string language = "en", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendWaitlistConfirmationAsync(string toEmail, string confirmUrl, string language = "en", CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
