using Orbit.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class AuthSignupDispatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_DispatchFailure_ReturnsCommittedSession(bool google)
    {
        using var scenario = new Scenario();

        var result = await scenario.SignInAsync(google, "pt-BR", "friend");

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("access");
        result.Value.RefreshToken.Should().Be("refresh");
        scenario.Calls.Should().Equal("upsert", "save", "session", "commit", "dispatch");
        scenario.Cache.TryGetValue("verify:person@example.com", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_AfterFailedDispatch_QueuesOriginalSignup(bool google)
    {
        using var scenario = new Scenario();
        try
        {
            await scenario.SignInAsync(google, "pt-BR", "friend");
        }
        catch (BackgroundJobClientException exception)
        {
            exception.Message.Should().Be("Storage unavailable");
        }

        var result = await scenario.SignInAsync(google, "en", "replacement");

        result.IsSuccess.Should().BeTrue();
        scenario.Queued.Should().ContainSingle();
        var job = scenario.Queued.Single();
        job.Args.Should().Equal(result.Value.UserId, true, "pt-BR", "friend");
        scenario.Calls.Count(call => call == "session").Should().Be(2);
    }

    private sealed class Scenario : IDisposable
    {
        private readonly IAuthUserStore _users = Substitute.For<IAuthUserStore>();
        private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
        private readonly IAuthSessionService _sessions = Substitute.For<IAuthSessionService>();
        private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
        private User? _stored;
        private int _dispatches;

        public MemoryCache Cache { get; } = new(new MemoryCacheOptions());
        public List<string> Calls { get; } = [];
        public List<Job> Queued { get; } = [];

        public Scenario()
        {
            _users.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Calls.Add("upsert");
                return _stored ??= call.Arg<User>();
            });
            _work.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Calls.Add("save");
                return 1;
            });
            _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<LoginResponse>>>>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var result = await call.Arg<Func<CancellationToken, Task<Result<LoginResponse>>>>()(call.Arg<CancellationToken>());
                    Calls.Add("commit");
                    return result;
                });
            _sessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Calls.Add("session");
                return Result.Success(new SessionTokens("access", "refresh"));
            });
            _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
            {
                Calls.Add("dispatch");
                if (++_dispatches == 1)
                    throw new BackgroundJobClientException("Storage unavailable", new InvalidOperationException("Connection failed"));
                Queued.Add(call.Arg<Job>());
                return "signup-job";
            });
        }

        public Task<Result<LoginResponse>> SignInAsync(bool google, string language, string referral)
        {
            if (google)
                return new GoogleSignInFlow(_users, _work, _sessions, new SignupJobDispatcher(_jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), _jobs, new AccountReactivation(Substitute.For<IGenericRepository<Notification>>(), Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>())).CompleteAsync(
                    "person@example.com", "Person", language, referral, null, null, CancellationToken.None);

            Cache.Set("verify:person@example.com", new VerificationEntry("123456", 0, DateTime.UtcNow));
            return new VerifyCodeCommandHandler(Cache, _users, _work, _sessions, new SignupJobDispatcher(_jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), _jobs, new AccountReactivation(Substitute.For<IGenericRepository<Notification>>(), Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>())).Handle(
                new VerifyCodeCommand("person@example.com", "123456", language, referral), CancellationToken.None);
        }

        public void Dispose() => Cache.Dispose();
    }
}
