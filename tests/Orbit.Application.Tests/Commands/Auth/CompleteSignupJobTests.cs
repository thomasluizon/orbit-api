using System.Collections.Concurrent;
using System.Linq.Expressions;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
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
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IProductAnalytics _analytics = Substitute.For<IProductAnalytics>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly ConcurrentQueue<Job> _queued = new();

    public CompleteSignupJobTests()
    {
        _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            _queued.Enqueue(call.Arg<Job>());
            return Guid.NewGuid().ToString();
        });
    }

    [Fact]
    public async Task DuplicateSignupJobs_DoNotRepeatCompletedEffects()
    {
        var user = User.Create("Person", "person@example.com").Value;
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(user);
        _mediator.Send(Arg.Any<ProcessReferralCodeCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

        await RunSignupAsync(user.Id, "pt-BR", "friend");
        await RunSignupAsync(user.Id, "pt-BR", "friend");

        _analytics.Received(1).CaptureUserEvent(user.Id, "signup_completed", "Free");
        await _mediator.Received(1).Send(Arg.Any<ProcessReferralCodeCommand>(), CancellationToken.None);
        await _email.Received(1).SendWelcomeEmailAsync(user.Email, user.Name, "pt-BR", CancellationToken.None);
    }

    [Fact]
    public async Task PartialDispatchFailure_RetryDoesNotRepeatAlreadyQueuedEffects()
    {
        var user = User.Create("Person", "person@example.com").Value;
        user.BeginSignup("pt-BR", "friend");
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(user);
        _mediator.Send(Arg.Any<ProcessReferralCodeCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var dispatches = 0;
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            if (++dispatches == 3)
                throw new BackgroundJobClientException("Storage unavailable", new InvalidOperationException("Connection failed"));
            _queued.Enqueue(call.Arg<Job>());
            return "child-job";
        });

        var act = () => CreateJob().ExecuteAsync(user.Id, true, "en", "replacement");
        await act.Should().ThrowAsync<BackgroundJobClientException>();
        while (_queued.TryDequeue(out var child))
            await InvokeAsync(child);
        user.SignupCompletedAtUtc.Should().BeNull();

        await RunSignupAsync(user.Id, "en", "replacement");

        user.SignupCompletedAtUtc.Should().NotBeNull();
        _analytics.Received(1).CaptureUserEvent(user.Id, "signup_completed", "Free");
        await _mediator.Received(1).Send(Arg.Is<ProcessReferralCodeCommand>(c => c.ReferralCode == "friend"), CancellationToken.None);
        await _email.Received(1).SendWelcomeEmailAsync(user.Email, user.Name, "pt-BR", CancellationToken.None);
    }

    [Fact]
    public async Task ReturningUser_PerformsNoSignupWork()
    {
        await CreateJob().ExecuteAsync(Guid.NewGuid(), false, "en", "friend");

        _users.ReceivedCalls().Should().BeEmpty();
        _email.ReceivedCalls().Should().BeEmpty();
        _mediator.ReceivedCalls().Should().BeEmpty();
        _analytics.ReceivedCalls().Should().BeEmpty();
        _jobs.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signup_ProcessesReferralCapturesAnalyticsAndSendsWelcome(bool referralFails)
    {
        var user = User.Create("Person", "person@example.com").Value;
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(user);
        _mediator.Send(Arg.Any<ProcessReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => referralFails ? throw new InvalidOperationException("Referral unavailable") : Result.Success());

        var signup = () => RunSignupAsync(user.Id, "pt-BR", "friend");
        if (referralFails)
        {
            await signup.Should().ThrowAsync<InvalidOperationException>().WithMessage("Referral unavailable");
            user.SignupReferralCompletedAtUtc.Should().BeNull();
            while (_queued.TryDequeue(out var child))
                await InvokeAsync(child);
        }
        else
        {
            await signup();
        }

        await _mediator.Received(1).Send(Arg.Is<ProcessReferralCodeCommand>(c => c.NewUserId == user.Id && c.ReferralCode == "friend"), CancellationToken.None);
        _analytics.Received(1).CaptureUserEvent(user.Id, "signup_completed", "Free");
        await _email.Received(1).SendWelcomeEmailAsync(user.Email, user.Name, "pt-BR", CancellationToken.None);
    }

    [Fact]
    public async Task Signup_AnalyticsFailureDoesNotPreventWelcome()
    {
        var user = User.Create("Person", "person@example.com").Value;
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(user);
        _analytics.When(a => a.CaptureUserEvent(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>())).Do(_ => throw new InvalidOperationException("Analytics unavailable"));

        await RunSignupAsync(user.Id, "en", null);

        await _email.Received(1).SendWelcomeEmailAsync(user.Email, user.Name, "en", CancellationToken.None);
        _mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task WelcomeFailure_RetriesOnlyEmailWithoutRepeatingSignupAnalyticsOrReferral()
    {
        var user = User.Create("Person", "person@example.com").Value;
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(user);
        _mediator.Send(Arg.Any<ProcessReferralCodeCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var attempts = 0;
        _email.SendWelcomeEmailAsync(user.Email, user.Name, "en", CancellationToken.None)
            .Returns(_ => ++attempts == 1
                ? Task.FromException(new InvalidOperationException("Email unavailable"))
                : Task.CompletedTask);
        Job? current = null;

        async Task AttemptAsync()
        {
            if (current is null)
                await CreateJob().ExecuteAsync(user.Id, true, "en", "friend");
            else
                await InvokeAsync(current);

            while (_queued.TryDequeue(out var next))
            {
                current = next;
                await InvokeAsync(current);
            }
        }

        await ((Func<Task>)AttemptAsync).Should().ThrowAsync<InvalidOperationException>().WithMessage("Email unavailable");
        await AttemptAsync();

        attempts.Should().Be(2);
        _analytics.Received(1).CaptureUserEvent(user.Id, "signup_completed", "Free");
        await _mediator.Received(1).Send(Arg.Any<ProcessReferralCodeCommand>(), CancellationToken.None);
    }

    private async Task InvokeAsync(Job job)
    {
        object instance = job.Type == typeof(CompleteSignupJob)
            ? CreateJob()
            : new SendWelcomeEmailJob(_users, _email, _work);
        var operation = (Task)job.Method.Invoke(instance, job.Args.ToArray())!;
        await operation;
    }

    private async Task RunSignupAsync(Guid userId, string language, string? referralCode)
    {
        await CreateJob().ExecuteAsync(userId, true, language, referralCode);
        while (_queued.TryDequeue(out var job))
            await InvokeAsync(job);
    }

    private CompleteSignupJob CreateJob() => new(_users, _jobs, _mediator, _analytics, NullLogger<CompleteSignupJob>.Instance, _work);
}
