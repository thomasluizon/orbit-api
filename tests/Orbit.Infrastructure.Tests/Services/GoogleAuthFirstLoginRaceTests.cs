using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Jobs;
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
    public async Task FirstAndConflictingCandidates_SaveSessionsForSameUserAndQueueOneSignup()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var jobs = Substitute.For<IBackgroundJobClient>();
        var tokenService = Substitute.For<ITokenService>();
        tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>()).Returns("session-token");

        async Task<Guid> SignInAsync()
        {
            await using var context = database.CreateContext();
            var work = new UnitOfWork(context, new DatabaseConnectionSettings());
            var sessions = new AuthSessionService(
                new GenericRepository<UserSession>(context), new GenericRepository<UserSessionRefreshToken>(context),
                new GenericRepository<User>(context), tokenService, work,
                Options.Create(new JwtSettings { SecretKey = "unused-test-secret", Issuer = "test", Audience = "test", RefreshExpiryDays = 90 }));
            var flow = new GoogleSignInFlow(new AuthUserStore(context), work, sessions, jobs);
            var result = await flow.CompleteAsync("Google@Example.com", "Google User", "pt-BR", "friend",
                "google-access", "google-refresh", CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value.UserId;
        }

        var winner = await SignInAsync();
        var conflicting = await SignInAsync();

        conflicting.Should().Be(winner);
        database.Context.Users.Count().Should().Be(1);
        database.Context.UserSessions.Count().Should().Be(2);
        var user = database.Context.Users.Single();
        user.GoogleAccessToken.Should().Be("google-access");
        user.Language.Should().Be("pt-BR");
        user.Handle.Should().NotBeNullOrEmpty();
        jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob) && (bool)job.Args[1]), Arg.Any<IState>());
        jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob) && !(bool)job.Args[1]), Arg.Any<IState>());
    }
}
