using System.Linq.Expressions;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
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
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<MarketingContact> _contacts = Substitute.For<IGenericRepository<MarketingContact>>();
    private readonly IGenericRepository<MarketingDelivery> _deliveries = Substitute.For<IGenericRepository<MarketingDelivery>>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<MarketingDelivery> _rows = [];
    private readonly SendMarketingBroadcastCommandHandler _handler;

    public SendMarketingBroadcastCommandHandlerTests()
    {
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("101");
        _unitOfWork.ExecuteOnceInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _contacts.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<MarketingContact>());
        _deliveries.AddAsync(Arg.Any<MarketingDelivery>(), Arg.Any<CancellationToken>())
            .Returns(call => { _rows.Add(call.Arg<MarketingDelivery>()); return Task.CompletedTask; });
        _handler = new(_users, _contacts, _jobs, Substitute.For<IMarketingUnsubscribeTokenService>(),
            _deliveries, _unitOfWork, Options.Create(new MarketingSettings { ApiBaseUrl = "https://api.useorbit.org", SendDelayMilliseconds = 100 }),
            NullLogger<SendMarketingBroadcastCommandHandler>.Instance);
    }

    private static SendMarketingBroadcastCommand Command(string? testEmail = null) =>
        new("EN Subject", "PT Assunto", "<p>EN body</p>", "<p>PT corpo</p>", testEmail);

    private void AudienceReturns(params User[] users) =>
        _users.FindIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(users);

    [Fact]
    public async Task LiveBroadcast_QueuesEachRecipientDurably()
    {
        var users = Enumerable.Range(0, 3).Select(i => User.Create("Recipient", $"recipient{i}@example.com").Value).ToArray();
        foreach (var user in users) user.SetMarketingConsent(true);
        AudienceReturns(users);
        var result = await _handler.Handle(Command(), CancellationToken.None);
        result.Value.RecipientCount.Should().Be(3);
        _rows.Should().HaveCount(3);
        _rows.Should().OnlyContain(row => row.BackgroundJobId == "101");
        _rows.Select(row => row.BroadcastId).Distinct().Should().ContainSingle();
        (_rows[1].NotBeforeAtUtc - _rows[0].NotBeforeAtUtc).Should().Be(TimeSpan.FromMilliseconds(100));
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            for (var i = 0; i < 3; i++)
                _jobs.Create(Arg.Any<Job>(), Arg.Is<IState>(state => state is ScheduledState));
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
        });
        _jobs.Received(3).Create(Arg.Is<Job>(job => job.Method.Name == nameof(SendMarketingEmailJob.ExecuteRecipientAsync)), Arg.Any<IState>());
    }

    [Fact]
    public async Task Audience_DeduplicatesUsersAndWaitlistAndExcludesBlockedOrInactive()
    {
        var allowed = User.Create("Allowed", "person@example.com").Value;
        allowed.SetMarketingConsent(true);
        var inactive = User.Create("Inactive", "inactive@example.com").Value;
        inactive.SetMarketingConsent(true);
        inactive.Deactivate(DateTime.UtcNow.AddDays(7));
        var blocked = User.Create("Blocked", "blocked@example.com").Value;
        blocked.SetMarketingConsent(true);
        var suppression = MarketingContact.ConfirmWaitlist(blocked.Email, "en");
        suppression.Suppress();
        AudienceReturns(allowed, inactive, blocked, User.Create("No consent", "no@example.com").Value);
        _contacts.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] {
            MarketingContact.ConfirmWaitlist("PERSON@example.com", "en"), suppression,
            MarketingContact.ConfirmWaitlist("waitlist@example.com", "pt-BR") });
        var result = await _handler.Handle(Command(), default);
        result.Value.RecipientCount.Should().Be(2);
        _rows.Select(row => row.Email).Should().BeEquivalentTo("person@example.com", "waitlist@example.com");
    }

    [Fact]
    public async Task Preview_EnqueuesWithoutEnumeratingAudienceOrPersistingBroadcast()
    {
        var result = await _handler.Handle(Command("preview@example.com"), default);
        result.Value.WasTest.Should().BeTrue();
        _rows.Should().BeEmpty();
        _jobs.Received(1).Create(Arg.Is<Job>(job => job.Method.Name == nameof(SendMarketingEmailJob.ExecuteAsync)),
            Arg.Is<IState>(state => state is EnqueuedState));
        await _users.DidNotReceive().FindIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>());
    }
}
