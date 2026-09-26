using System.Linq.Expressions;
using System.Net;
using System.Text;
using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Orbit.Application.Auth.Commands;
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
    public async Task ConcurrentFirstLogins_SaveWinnerSessionWithoutRetryingLosingUser()
    {
        using var database = new SqliteOrbitDbContextFactory();
        await using var winnerContext = database.CreateContext();
        var loserContext = database.Context;
        var emailService = Substitute.For<IEmailService>();
        var analytics = Substitute.For<IProductAnalytics>();
        var welcomeSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        emailService.SendWelcomeEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { welcomeSent.TrySetResult(); return Task.CompletedTask; });

        var mediator = Substitute.For<IMediator>();
        var referralProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Send(Arg.Any<ProcessReferralCodeCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => { referralProcessed.TrySetResult(); return Task.FromResult(Result.Success()); });
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IMediator)).Returns(mediator);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var winnerWork = new UnitOfWork(winnerContext, new DatabaseConnectionSettings());
        var winnerRepository = new GenericRepository<User>(winnerContext);
        var winnerHandler = CreateHandler(winnerContext, winnerRepository, winnerWork, emailService, scopeFactory, analytics);

        var loserRepository = Substitute.For<IGenericRepository<User>>();
        var realLoserRepository = new GenericRepository<User>(loserContext);
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupCount = 0;
        loserRepository.FindOneTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var found = await realLoserRepository.FindOneTrackedIgnoringFiltersAsync(
                    call.Arg<Expression<Func<User, bool>>>(), call.Arg<CancellationToken>());
                if (Interlocked.Increment(ref lookupCount) != 1)
                    return found;
                lookupStarted.SetResult();
                await releaseLookup.Task;
                return found;
            });
        loserRepository.AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(call => realLoserRepository.AddAsync(call.Arg<User>(), call.Arg<CancellationToken>()));

        var loserWork = Substitute.For<IUnitOfWork>();
        var saveAttempts = 0;
        loserWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            saveAttempts++;
            try
            {
                return await loserContext.SaveChangesAsync(call.Arg<CancellationToken>());
            }
            catch (DbUpdateException exception) when (saveAttempts == 1 && exception.InnerException is SqliteException)
            {
                throw new DbUpdateException("duplicate user email", new PostgresException(
                    "duplicate user email", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));
            }
        });
        loserWork.When(work => work.ResetTracking()).Do(_ => loserContext.ChangeTracker.Clear());
        var loserHandler = CreateHandler(loserContext, loserRepository, loserWork, emailService, scopeFactory, analytics);

        var loserRequest = new GoogleAuthCommand("valid-token", GoogleAccessToken: "google-access", ReferralCode: "referral");
        var loserTask = loserHandler.Handle(loserRequest, CancellationToken.None);
        await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var winner = await winnerHandler.Handle(
            new GoogleAuthCommand("valid-token", ReferralCode: "referral"), CancellationToken.None);
        releaseLookup.SetResult();
        var loser = await loserTask;
        await Task.WhenAll(welcomeSent.Task, referralProcessed.Task).WaitAsync(TimeSpan.FromSeconds(5));

        winner.IsSuccess.Should().BeTrue();
        loser.IsSuccess.Should().BeTrue();
        loser.Value.UserId.Should().Be(winner.Value.UserId);
        loser.Value.Token.Should().NotBeNullOrWhiteSpace();
        saveAttempts.Should().Be(3);
        loserContext.ChangeTracker.Entries<User>()
            .Should().NotContain(entry => entry.State == EntityState.Added);
        await using var verify = database.CreateContext();
        (await verify.Users.CountAsync()).Should().Be(1);
        (await verify.UserSessions.CountAsync()).Should().Be(2);
        (await verify.Users.SingleAsync()).GoogleAccessToken.Should().Be("google-access");
        analytics.Received(1).CaptureUserEvent(winner.Value.UserId, "signup_completed", "Free", Arg.Any<IReadOnlyDictionary<string, object>?>());
        await emailService.Received(1).SendWelcomeEmailAsync("google@example.com", "Google User", "en", Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(Arg.Is<ProcessReferralCodeCommand>(command =>
            command.NewUserId == winner.Value.UserId && command.ReferralCode == "referral"), Arg.Any<CancellationToken>());
    }

    private static GoogleAuthCommandHandler CreateHandler(
        OrbitDbContext context,
        IGenericRepository<User> userRepository,
        IUnitOfWork unitOfWork,
        IEmailService emailService,
        IServiceScopeFactory scopeFactory,
        IProductAnalytics analytics)
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>()).Returns("session-access-token");
        var sessionService = new AuthSessionService(
            new GenericRepository<UserSession>(context),
            new GenericRepository<UserSessionRefreshToken>(context),
            userRepository, tokenService, unitOfWork,
            Options.Create(new JwtSettings
            {
                SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
                Issuer = "test-issuer", Audience = "test-audience", RefreshExpiryDays = 90
            }));
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("Supabase").Returns(new HttpClient(new GoogleTokenHandler())
        {
            BaseAddress = new Uri("https://supabase.example.com")
        });
        return new GoogleAuthCommandHandler(userRepository, unitOfWork, sessionService, httpFactory,
            emailService, scopeFactory, analytics, NullLogger<GoogleAuthCommandHandler>.Instance);
    }

    private sealed class GoogleTokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"email":"Google@Example.com","user_metadata":{"full_name":"Google User"}}""",
                    Encoding.UTF8, "application/json")
            });
    }
}
