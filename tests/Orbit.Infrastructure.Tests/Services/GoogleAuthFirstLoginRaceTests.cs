using System.Linq.Expressions;
using System.Net;
using System.Text;
using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class GoogleAuthFirstLoginRaceTests
{
    [Fact]
    public Task ConcurrentFirstSignIns_SaveSessionsForSameUserAndQueueOneSignup() => RunSignInsAsync(true);

    [Fact]
    public Task FirstAndConflictingCandidates_SaveSessionsForSameUserAndQueueOneSignup() => RunSignInsAsync(false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedDispatch_LaterSignInInFreshContext_CompletesOriginalSignupOnce(bool firstGoogle, bool laterGoogle)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var jobs = Substitute.For<IBackgroundJobClient>();
        var queued = new ConcurrentQueue<Job>();
        var dispatches = 0;
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            if (++dispatches == 1)
            {
                database.Context.Users.Single().SignupLanguage.Should().Be("pt-BR");
                database.Context.UserSessions.Count().Should().Be(1);
                throw new BackgroundJobClientException("Storage unavailable", new InvalidOperationException("Connection failed"));
            }
            queued.Enqueue(call.Arg<Job>());
            return "signup-job";
        });

        var userId = await SignInAsync(database, jobs, firstGoogle, "pt-BR", "friend");
        var returningId = await SignInAsync(database, jobs, laterGoogle, "en", "replacement");

        returningId.Should().Be(userId);
        queued.Should().ContainSingle();
        var original = queued.Single();
        original.Args.Should().Equal(userId, true, "pt-BR", "friend");
        queued.Enqueue(original);
        await CompleteQueuedSignupAsync(database, jobs, queued);
        await using var verification = database.CreateContext();
        var user = await verification.Users.SingleAsync();
        user.SignupCompletedAtUtc.Should().NotBeNull();
        user.SignupReferralCode.Should().Be("friend");
        user.SignupLanguage.Should().Be("pt-BR");
        verification.UserSessions.Count().Should().Be(2);

        await SignInAsync(database, jobs, laterGoogle, "en", "replacement");
        queued.Should().ContainSingle().Which.Args[1].Should().Be(false);
    }

    private async Task RunSignInsAsync(bool concurrently)
    {
        using var database = concurrently
            ? new SqliteOrbitDbContextFactory(Path.Combine(Path.GetTempPath(), $"orbit-auth-{Guid.NewGuid():N}.db"))
            : new SqliteOrbitDbContextFactory();
        var jobs = Substitute.For<IBackgroundJobClient>();
        var queued = new ConcurrentQueue<Job>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            queued.Enqueue(call.Arg<Job>());
            return "signup-job";
        });
        var tokenService = Substitute.For<ITokenService>();
        tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>()).Returns("session-token");

        using var ready = new Barrier(2);
        async Task<Guid> SignInAsync()
        {
            await using var context = database.CreateContext();
            var work = new UnitOfWork(context, new DatabaseConnectionSettings());
            var sessions = new AuthSessionService(
                new GenericRepository<UserSession>(context), new GenericRepository<UserSessionRefreshToken>(context),
                new GenericRepository<User>(context), tokenService, work,
                Options.Create(new JwtSettings { SecretKey = "unused-test-secret", Issuer = "test", Audience = "test", RefreshExpiryDays = 90 }));
            var flow = new GoogleSignInFlow(new AuthUserStore(context), work, sessions, new SignupJobDispatcher(jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), jobs, new AccountReactivation(Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>()));
            if (concurrently)
                ready.SignalAndWait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var result = await flow.CompleteAsync("Google@Example.com", "Google User", "pt-BR", "friend",
                "google-access", "google-refresh", CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value.UserId;
        }

        var results = concurrently
            ? await Task.WhenAll(Task.Run(SignInAsync), Task.Run(SignInAsync))
            : [await SignInAsync(), await SignInAsync()];
        var winner = results[0];
        var conflicting = results[1];

        conflicting.Should().Be(winner);
        database.Context.Users.Count().Should().Be(1);
        database.Context.UserSessions.Count().Should().Be(2);
        var user = database.Context.Users.Single();
        user.GoogleAccessToken.Should().Be("google-access");
        user.Language.Should().Be("pt-BR");
        user.Handle.Should().NotBeNullOrEmpty();
        jobs.Received(2).Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob) && (bool)job.Args[1]), Arg.Any<IState>());
        jobs.DidNotReceive().Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob) && !(bool)job.Args[1]), Arg.Any<IState>());
        await CompleteQueuedSignupAsync(database, jobs, queued);
    }

    private static async Task<Guid> SignInAsync(SqliteOrbitDbContextFactory database, IBackgroundJobClient jobs,
        bool google, string language, string referralCode)
    {
        await using var context = database.CreateContext();
        var work = new UnitOfWork(context, new DatabaseConnectionSettings());
        var tokenService = Substitute.For<ITokenService>();
        tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>()).Returns("session-token");
        var sessions = new AuthSessionService(
            new GenericRepository<UserSession>(context), new GenericRepository<UserSessionRefreshToken>(context),
            new GenericRepository<User>(context), tokenService, work,
            Options.Create(new JwtSettings { SecretKey = "unused-test-secret", Issuer = "test", Audience = "test", RefreshExpiryDays = 90 }));
        var dispatcher = new SignupJobDispatcher(jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance);
        var users = new AuthUserStore(context);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("verify:google@example.com", new VerificationEntry("123456", 0, DateTime.UtcNow));
        var result = google
            ? await new GoogleSignInFlow(users, work, sessions, dispatcher, jobs, new AccountReactivation(Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>())).CompleteAsync(
                "google@example.com", "Google User", language, referralCode, null, null, CancellationToken.None)
            : await new VerifyCodeCommandHandler(cache, users, work, sessions, dispatcher, jobs, new AccountReactivation(Substitute.For<IGenericRepository<OwedSubscriptionCancellation>>())).Handle(
                new VerifyCodeCommand("google@example.com", "123456", language, referralCode), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("session-token");
        result.Value.RefreshToken.Should().NotBeNullOrEmpty();
        return result.Value.UserId;
    }

    private static async Task CompleteQueuedSignupAsync(SqliteOrbitDbContextFactory database,
        IBackgroundJobClient jobs, ConcurrentQueue<Job> queued)
    {
        var analytics = Substitute.For<IProductAnalytics>();
        var email = Substitute.For<IEmailService>();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ProcessReferralCodeCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        while (queued.TryDequeue(out var job))
        {
            await using var context = database.CreateContext();
            var work = new UnitOfWork(context, new DatabaseConnectionSettings());
            var users = new GenericRepository<User>(context);
            object instance = job.Type == typeof(CompleteSignupJob)
                ? new CompleteSignupJob(users, jobs, mediator, analytics, NullLogger<CompleteSignupJob>.Instance, work)
                : new SendWelcomeEmailJob(users, email, work);
            await (Task)job.Method.Invoke(instance, job.Args.ToArray())!;
        }

        analytics.Received(1).CaptureUserEvent(Arg.Any<Guid>(), "signup_completed", "Free");
        await mediator.Received(1).Send(Arg.Is<ProcessReferralCodeCommand>(c => c.ReferralCode == "friend"), CancellationToken.None);
        await email.Received(1).SendWelcomeEmailAsync("google@example.com", Arg.Any<string>(), "pt-BR", CancellationToken.None);
    }
}
