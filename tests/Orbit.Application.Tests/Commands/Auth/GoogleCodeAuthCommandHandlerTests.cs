using Hangfire.Common;
using Hangfire.States;
using Orbit.Application.Auth.Jobs;
using System.Linq.Expressions;
using Orbit.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Hangfire;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Behaviors;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Auth;

public class GoogleCodeAuthCommandHandlerTests
{
    private const string Email = "google@example.com";
    private readonly IGoogleAuthorizationCodeService _exchange = Substitute.For<IGoogleAuthorizationCodeService>();
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly IAuthSessionService _sessions = Substitute.For<IAuthSessionService>();
    private readonly IAuthUserStore _store = Substitute.For<IAuthUserStore>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly IMediator _signInMediator = Substitute.For<IMediator>();
    private readonly GoogleCodeAuthCommandHandler _handler;

    public GoogleCodeAuthCommandHandlerTests()
    {
        _work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<Orbit.Application.Auth.Queries.LoginResponse>>>>()(call.Arg<CancellationToken>()));
        _store.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<User>());
        var flow = new GoogleSignInFlow(_store, _work, _sessions, new SignupJobDispatcher(_jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance), _jobs);
        _handler = new GoogleCodeAuthCommandHandler(_exchange, _signInMediator);
        _signInMediator.Send(Arg.Any<CompleteGoogleSignInCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<CompleteGoogleSignInCommand>();
                return flow.CompleteAsync(request.Email, request.Name, request.Language, request.ReferralCode,
                    request.GoogleAccessToken, request.GoogleRefreshToken, call.Arg<CancellationToken>());
            });
        _exchange.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoogleCodeIdentity(Email, "Google User", "access", "refresh")));
        _sessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("orbit-access", "orbit-refresh")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Reactivation_RestoresOnlyUnexpiredStripeRenewal(bool expired, bool play)
    {
        var user = User.Create("Returning", Email).Value;
        _store.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(user);
        var expiryAtUtc = DateTime.UtcNow.AddDays(expired ? -1 : 3);
        if (play)
            user.SetPlaySubscription("play-token", expiryAtUtc);
        else
            user.SetStripeSubscription("sub_paid", expiryAtUtc);
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        Job? enqueued = null;
        _jobs.Create(Arg.Do<Job>(job => { if (job.Type == typeof(CancelSubscriptionOnDeletionJob)) enqueued = job; }), Arg.Any<IState>());

        var result = await _handler.Handle(new GoogleCodeAuthCommand("code", "verifier", "https://api.test/oauth/google/callback", PersistGoogleTokens: false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WasReactivated.Should().BeTrue();
        user.IsDeactivated.Should().BeFalse();
        user.ScheduledDeletionAt.Should().BeNull();
        user.HasProAccess.Should().Be(!expired);
        user.Plan.Should().Be(expired ? UserPlan.Free : UserPlan.Pro);
        _jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob)
            && (Guid)job.Args[0] == user.Id && !(bool)job.Args[1]), Arg.Any<IState>());
        _jobs.Received(!expired && !play ? 2 : 1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        if (!expired && !play)
        {
            _jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CancelSubscriptionOnDeletionJob)), Arg.Any<IState>());
            enqueued!.Type.Should().Be<CancelSubscriptionOnDeletionJob>();
            enqueued.Args.Should().Equal(user.Id, "sub_paid", null, false, CancellationToken.None);
        }
        else
            _jobs.DidNotReceive().Create(Arg.Is<Job>(job => job.Type == typeof(CancelSubscriptionOnDeletionJob)), Arg.Any<IState>());
    }

    [Fact]
    public async Task NewUser_StoresTokensAndRunsSignupEffects()
    {
        User? created = null;
        _store.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(call => created = call.Arg<User>());

        var result = await _handler.Handle(new GoogleCodeAuthCommand("code", "verifier", "https://app.test/callback", ReferralCode: "friend"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().Be("orbit-access");
        result.Value.RefreshToken.Should().Be("orbit-refresh");
        created.Should().NotBeNull();
        created!.GoogleAccessToken.Should().Be("access");
        created.GoogleRefreshToken.Should().Be("refresh");
        _jobs.Received(1).Create(Arg.Is<Job>(job => job.Type == typeof(CompleteSignupJob)
            && (Guid)job.Args[0] == created.Id && (bool)job.Args[1]
            && (string)job.Args[3] == "friend"), Arg.Any<IState>());
    }

    [Theory]
    [InlineData(null, "old-refresh", false)]
    [InlineData("new-refresh", "new-refresh", false)]
    [InlineData(null, null, true)]
    [InlineData("new-refresh", "new-refresh", true)]
    public async Task ExistingUser_PreservesOrReplacesRefreshToken(string? returnedRefresh, string? expectedRefresh, bool reconnectRequired)
    {
        var user = User.Create("Existing", Email).Value;
        user.SetGoogleTokens("old-access", "old-refresh");
        if (reconnectRequired)
        {
            user.EnableCalendarAutoSync().IsSuccess.Should().BeTrue();
            user.MarkCalendarSyncReconnectRequired("invalid_grant");
        }
        _store.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(user);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanManageCalendar(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var stateHandler = new GetCalendarAutoSyncStateQueryHandler(_users, payGate);
        _exchange.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoogleCodeIdentity(Email, "New Name", "new-access", returnedRefresh)));

        var result = await _handler.Handle(new GoogleCodeAuthCommand("code", "verifier", "https://app.test/callback"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(user.Id);
        user.GoogleAccessToken.Should().Be("new-access");
        user.GoogleRefreshToken.Should().Be(expectedRefresh);
        await _work.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        var state = await stateHandler.Handle(new GetCalendarAutoSyncStateQuery(user.Id), CancellationToken.None);

        state.IsSuccess.Should().BeTrue();
        state.Value.Status.Should().Be(GoogleCalendarAutoSyncStatus.Idle);
        state.Value.HasGoogleConnection.Should().BeTrue();
        state.Value.Enabled.Should().BeFalse();
        user.GoogleCalendarLastSyncError.Should().BeNull();
        await _users.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task McpIdentityOnlyExchange_DoesNotReplaceCalendarTokens()
    {
        var user = User.Create("Existing", Email).Value;
        user.SetGoogleTokens("calendar-access", "calendar-refresh");
        _store.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(
            new GoogleCodeAuthCommand("code", "verifier", "https://api.useorbit.org/oauth/google/callback",
                PersistGoogleTokens: false),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.GoogleAccessToken.Should().Be("calendar-access");
        user.GoogleRefreshToken.Should().Be("calendar-refresh");
    }

    [Fact]
    public async Task DeactivatedUser_IsReactivated()
    {
        var user = User.Create("Existing", Email).Value;
        user.Deactivate(DateTime.UtcNow.AddDays(1));
        _store.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new GoogleCodeAuthCommand("code", "verifier", "https://app.test/callback"), CancellationToken.None);

        result.Value.WasReactivated.Should().BeTrue();
        user.IsDeactivated.Should().BeFalse();
    }

    [Theory]
    [InlineData(ErrorCodes.GoogleRedirectUriNotAllowed)]
    [InlineData(ErrorCodes.GoogleCodeExchangeFailed)]
    [InlineData(ErrorCodes.InvalidGoogleToken)]
    public async Task ExchangeFailure_NeverCreatesUser(string code)
    {
        _exchange.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GoogleCodeIdentity>("Rejected", code));

        var result = await _handler.Handle(new GoogleCodeAuthCommand("code", "verifier", "https://app.test/callback"), CancellationToken.None);

        result.ErrorCode.Should().Be(code);
        await _users.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _sessions.DidNotReceive().CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConcurrencyConflict_RetriesSignInWithoutRedeemingCodeAgain()
    {
        var staleUser = User.Create("Stale", Email).Value;
        var currentUser = User.Create("Current", Email).Value;
        var trackerWasReset = false;
        _work.When(work => work.ResetTracking()).Do(_ => trackerWasReset = true);
        _store.UpsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns(_ => trackerWasReset ? currentUser : staleUser);
        var saves = 0;
        _work.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ++saves == 1
                ? throw new DbUpdateConcurrencyException("stale user")
                : Task.FromResult(1));
        var exchanges = 0;
        _exchange.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++exchanges == 1
                ? Result.Success(new GoogleCodeIdentity(Email, "Google User", "access", "refresh"))
                : Result.Failure<GoogleCodeIdentity>("Code already redeemed", ErrorCodes.GoogleCodeExchangeFailed));

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<GoogleCodeAuthCommand>();
                cfg.AddOpenBehavior(typeof(ConcurrencyRetryBehavior<,>));
            })
            .AddScoped<GoogleSignInFlow>()
            .AddSingleton<ISignupJobDispatcher>(new SignupJobDispatcher(_jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance))
            .AddSingleton(_store)
            .AddSingleton(_jobs)
            .AddSingleton(_exchange)
            .AddSingleton(_users)
            .AddSingleton(_work)
            .AddSingleton(_sessions)
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new GoogleCodeAuthCommand("code", "verifier", "https://app.test/callback"), CancellationToken.None);

        exchanges.Should().Be(1);
        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(currentUser.Id);
        currentUser.GoogleAccessToken.Should().Be("access");
        currentUser.GoogleRefreshToken.Should().Be("refresh");
        saves.Should().Be(2);
        _work.Received(1).ResetTracking();
    }
}
