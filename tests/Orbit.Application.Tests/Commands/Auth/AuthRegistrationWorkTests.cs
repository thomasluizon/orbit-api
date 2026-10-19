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
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class AuthRegistrationWorkTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SignIn_RegistrationDoesNotChangeCollaboratorOrder(bool google, bool withExtras)
    {
        var returning = await SignInAsync(google, true, withExtras);
        var signup = await SignInAsync(google, false, withExtras);

        signup.Should().Equal(returning);
    }

    private static async Task<List<string>> SignInAsync(bool google, bool exists, bool withExtras)
    {
        var calls = new List<string>();
        var users = Substitute.For<IAuthUserStore>();
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                calls.Add("begin");
                var result = await call.Arg<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>()(call.Arg<CancellationToken>());
                calls.Add("commit");
                return result;
            });
        var sessions = Substitute.For<IAuthSessionService>();
        var jobs = Substitute.For<IBackgroundJobClient>();
        users.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add("upsert");
                return exists ? User.Create("Existing", "person@example.com").Value : call.Arg<User>();
            });
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>())
            .Returns(_ => { calls.Add("enqueue"); return "job"; });
        work.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { calls.Add("save"); return Task.FromResult(1); });
        sessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("session");
                return Result.Success(new SessionTokens("access", "refresh"));
            });
        if (google)
        {
            var flow = new GoogleSignInFlow(users, work, sessions, new SignupJobDispatcher(jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), jobs);
            (await flow.CompleteAsync("person@example.com", "Person", "en", withExtras ? "friend" : null, withExtras ? "access" : null, withExtras ? "refresh" : null,
                CancellationToken.None)).IsSuccess.Should().BeTrue();
        }
        else
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            cache.Set("verify:person@example.com", new VerificationEntry("123456", 0, DateTime.UtcNow));
            var handler = new VerifyCodeCommandHandler(cache, users, work, sessions, new SignupJobDispatcher(jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), jobs);
            (await handler.Handle(new VerifyCodeCommand("person@example.com", "123456", ReferralCode: withExtras ? "friend" : null),
                CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        calls.Should().Equal("begin", "upsert", "save", "session", "commit", "enqueue");
        return calls;
    }
}
