using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class CompleteSignupJobTests
{
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IProductAnalytics _analytics = Substitute.For<IProductAnalytics>();

    [Fact]
    public async Task ReturningUser_PerformsNoSignupWork()
    {
        await CreateJob().ExecuteAsync(Guid.NewGuid(), false, "en", "friend");

        _users.ReceivedCalls().Should().BeEmpty();
        _email.ReceivedCalls().Should().BeEmpty();
        _mediator.ReceivedCalls().Should().BeEmpty();
        _analytics.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signup_ProcessesReferralCapturesAnalyticsAndSendsWelcome(bool referralFails)
    {
        var user = User.Create("Person", "person@example.com").Value;
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _mediator.Send(Arg.Any<ProcessReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => referralFails ? throw new InvalidOperationException("Referral unavailable") : Result.Success());

        await CreateJob().ExecuteAsync(user.Id, true, "pt-BR", "friend");

        await _mediator.Received(1).Send(Arg.Is<ProcessReferralCodeCommand>(c => c.NewUserId == user.Id && c.ReferralCode == "friend"), CancellationToken.None);
        _analytics.Received(1).CaptureUserEvent(user.Id, "signup_completed", "Free");
        await _email.Received(1).SendWelcomeEmailAsync(user.Email, user.Name, "pt-BR", CancellationToken.None);
    }

    [Fact]
    public async Task Signup_AnalyticsFailureDoesNotPreventWelcome()
    {
        var user = User.Create("Person", "person@example.com").Value;
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _analytics.When(a => a.CaptureUserEvent(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>())).Do(_ => throw new InvalidOperationException("Analytics unavailable"));

        await CreateJob().ExecuteAsync(user.Id, true, "en", null);

        await _email.Received(1).SendWelcomeEmailAsync(user.Email, user.Name, "en", CancellationToken.None);
        _mediator.ReceivedCalls().Should().BeEmpty();
    }

    private CompleteSignupJob CreateJob() => new(_users, _email, _mediator, _analytics, NullLogger<CompleteSignupJob>.Instance);
}
