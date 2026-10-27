using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Events;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public partial class OAuthPostgresRetryTests
{
    [PostgresFact]
    public Task Refresh_LostCommitAcknowledgement_RotatesOwnSuccessorAndStillRejectsAnotherRequest() => RefreshAfterLostAcknowledgement(false);

    [PostgresFact]
    public Task Refresh_LostCommitAcknowledgementThenRollback_StillFindsItsCommittedSuccessor() => RefreshAfterLostAcknowledgement(true);

    private static Task RefreshAfterLostAcknowledgement(bool rollbackRetry) =>
        WithDatabase(async database =>
        {
            var failure = new LostAcknowledgement { RollbackRetry = rollbackRetry };
            await using var db = RetryContext(database, failure);
            var user = User.Create("Refresh", "refresh@example.com").Value;
            var original = ApiKey.Create(user.Id, "Claude.ai", ["read_habits"], oauthFamilyId: Guid.NewGuid()).Value.Entity;
            var unrelated = ApiKey.Create(user.Id, "Other client", ["read_habits"]).Value.Entity;
            db.AddRange(user, original, unrelated);
            await db.SaveChangesAsync();
            var store = new OAuthAuthorizationStore(new OAuthStateStore(db, TimeProvider.System), TimeProvider.System);
            var refresh = store.CreateRefreshToken(new(user.Id, original.Id, "client", original.OAuthFamilyId!.Value));
            failure.Enabled = true;

            var response = await RefreshController(db).Token("refresh_token", null, null, null, default, refresh, client_id: "client");

            var success = response.Should().BeOfType<OkObjectResult>().Subject;
            var body = JsonSerializer.SerializeToElement(success.Value);
            var keys = await database.ApiKeys.AsNoTracking().ToListAsync();
            var working = keys.Single(key => key.OAuthFamilyId == original.OAuthFamilyId && !key.IsRevoked);
            working.Verify(body.GetProperty("access_token").GetString()!).Should().BeTrue();
            working.HasExpired().Should().BeFalse();
            keys.Count(key => key.OAuthFamilyId == original.OAuthFamilyId).Should().Be(3);
            failure.Failures.Should().Be(1);
            failure.Commits.Should().Be(2);
            failure.RolledBackAttempts.Should().Be(rollbackRetry ? 1 : 0);
            keys.Single(key => key.Id == unrelated.Id).IsRevoked.Should().BeFalse();

            await using var nextRequest = RetryContext(database, new LostAcknowledgement());
            (await RefreshController(nextRequest).Token("refresh_token", null, null, null, default,
                body.GetProperty("refresh_token").GetString(), client_id: "client")).Should().BeOfType<OkObjectResult>();
            (await RefreshController(nextRequest).Token("refresh_token", null, null, null, default, refresh, client_id: "client"))
                .Should().BeOfType<BadRequestObjectResult>();
            (await database.ApiKeys.AsNoTracking().Where(key => key.OAuthFamilyId == original.OAuthFamilyId).ToListAsync())
                .Should().OnlyContain(key => key.IsRevoked);
            (await database.ApiKeys.AsNoTracking().SingleAsync(key => key.Id == unrelated.Id)).IsRevoked.Should().BeFalse();
        });

    [PostgresFact]
    public Task Put_LostCommitAcknowledgement_WithAndWithoutCapacity_ReturnsCommittedState() =>
        WithDatabase(database =>
        {
            foreach (var capacity in new int?[] { null, 1 })
            {
                var failure = new LostAcknowledgement();
                using var db = RetryContext(database, failure);
                var store = new OAuthStateStore(db, TimeProvider.System);
                var kind = capacity.HasValue ? "google" : "code";
                failure.Enabled = true;

                store.Put(kind, kind, "payload", DateTime.UtcNow.AddMinutes(5), capacity).Should().BeTrue();

                failure.Failures.Should().Be(1);
                store.Read(kind, kind, DateTime.UtcNow).Should().Be("payload");
                db.OAuthStates.AsNoTracking().Count(state => state.Kind == kind).Should().Be(1);
            }
            return Task.CompletedTask;
        });

    [PostgresFact]
    public Task Take_LostCommitAcknowledgement_ReturnsOneTimePayloadExactlyOnce() =>
        WithDatabase(database =>
        {
            foreach (var kind in new[] { "google", "code", "consent", "refresh" })
            {
                var failure = new LostAcknowledgement();
                using var db = RetryContext(database, failure, new AutocommitAcknowledgement(failure));
                var store = new OAuthStateStore(db, TimeProvider.System);
                store.Put(kind, kind, "payload", DateTime.UtcNow.AddMinutes(5)).Should().BeTrue();
                failure.Enabled = true;

                store.Take(kind, kind, DateTime.UtcNow).Should().Be("payload");

                failure.Failures.Should().Be(1);
                store.Take(kind, kind, DateTime.UtcNow).Should().BeNull();
            }
            return Task.CompletedTask;
        });

    [PostgresFact]
    public Task Transaction_LostCommitAcknowledgement_DoesNotRepeatBudgetReservationOrLoseResult() => RunTransactionFailure(false);

    [PostgresFact]
    public Task Transaction_RolledBackAttempt_RetriesWithoutDuplicatingReservation() => RunTransactionFailure(true);

    private static Task RunTransactionFailure(bool rollbackFirstCommit) =>
        WithDatabase(async database =>
        {
            var failure = new LostAcknowledgement { RollbackFirstCommit = rollbackFirstCommit };
            await using var db = RetryContext(database, failure);
            var user = User.Create("Budget", "budget@example.com").Value;
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var events = Substitute.For<IAccountEventCollector>();
            var work = new UnitOfWork(db, new DatabaseConnectionSettings(), events);
            var executions = 0;
            failure.Enabled = true;

            var id = await work.ExecuteInTransactionAsync(async token =>
            {
                executions++;
                var call = AiBudgetCall.Reserve(user.Id, new DateOnly(2026, 1, 1), 100);
                db.AiBudgetCalls.Add(call);
                await work.SaveChangesAsync(token);
                return call.Id;
            });

            failure.Failures.Should().Be(1);
            executions.Should().Be(rollbackFirstCommit ? 2 : 1);
            events.Received(rollbackFirstCommit ? 1 : 0).Clear();
            (await database.AiBudgetCalls.AsNoTracking().ToListAsync()).Should().ContainSingle()
                .Which.Id.Should().Be(id);
        });

    [PostgresFact]
    public Task Budget_LostCommitAcknowledgement_ChargesOneReservationAndReturnsItsId() =>
        WithDatabase(async database =>
        {
            var user = User.Create("Budget", "budget@example.com").Value;
            database.Users.Add(user);
            await database.SaveChangesAsync();
            var failure = new LostAcknowledgement { Enabled = true };
            var config = Substitute.For<IAppConfigService>();
            config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
            config.GetAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<long>(1));
            var dates = Substitute.For<IUserDateService>();
            dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 1, 1));
            await using var services = new ServiceCollection()
                .AddScoped(_ => RetryContext(database, failure))
                .AddScoped<IUnitOfWork>(scope => new UnitOfWork(scope.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()))
                .AddSingleton(Substitute.For<IFeatureFlagService>())
                .AddSingleton(config).AddSingleton(dates).BuildServiceProvider();
            var budget = new AiDailyBudget(services.GetRequiredService<IServiceScopeFactory>());

            var id = await budget.ReserveAsync(user.Id, 100, default);

            failure.Failures.Should().Be(1);
            var call = (await database.AiBudgetCalls.AsNoTracking().ToListAsync()).Should().ContainSingle().Subject;
            call.Id.Should().Be(id);
            call.ChargedMicroDollars.Should().Be(100);
            await budget.SettleAsync(id, 50, default);
            (await database.AiBudgetCalls.AsNoTracking().SingleAsync()).ChargedMicroDollars.Should().Be(50);
        });

    [PostgresFact]
    public Task DesktopHandoff_LostCommitAcknowledgement_ReturnsCommittedSession() =>
        WithDatabase(async database =>
        {
            const string redirect = "org.useorbit.desktop://auth/callback";
            const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
            const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
            var failure = new LostAcknowledgement();
            await using var db = RetryContext(database, failure);
            var user = User.Create("Desktop", "desktop@example.com").Value;
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var work = new UnitOfWork(db, new DatabaseConnectionSettings());
            var users = new GenericRepository<User>(db);
            var handoffs = new DesktopHandoffStore(db);
            var create = new CreateDesktopHandoffCommandHandler(handoffs, users, work,
                Options.Create(new DesktopAuthOptions()), TimeProvider.System);
            var handoff = await create.Handle(new(user.Id, "state", challenge, "S256", redirect), default);
            handoff.IsSuccess.Should().BeTrue();
            var tokens = Substitute.For<ITokenService>();
            tokens.GenerateToken(user.Id, user.Email, Arg.Any<Guid>()).Returns("desktop-access");
            var sessions = new AuthSessionService(new GenericRepository<UserSession>(db),
                new GenericRepository<UserSessionRefreshToken>(db), users, tokens, work,
                Options.Create(new JwtSettings { SecretKey = "desktop-test-secret", Issuer = "test", Audience = "test" }));
            var exchange = new ExchangeDesktopHandoffCommandHandler(handoffs, users, sessions, work,
                Options.Create(new DesktopAuthOptions()), TimeProvider.System);
            failure.Enabled = true;

            var response = await exchange.Handle(new(handoff.Value.Code, "state", verifier, redirect), default);

            response.IsSuccess.Should().BeTrue();
            response.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
            (await database.UserSessions.AsNoTracking().ToListAsync()).Should().ContainSingle().Which.UserId.Should().Be(user.Id);
            failure.Failures.Should().Be(1);
            tokens.Received(1).GenerateToken(user.Id, user.Email, Arg.Any<Guid>());
            (await exchange.Handle(new(handoff.Value.Code, "state", verifier, redirect), default)).IsFailure.Should().BeTrue();
        });

    private static OrbitDbContext RetryContext(OrbitDbContext database, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql(database.Database.GetConnectionString(), postgres => postgres.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .AddInterceptors(interceptors).Options);

    private static OAuthController RefreshController(OrbitDbContext db) =>
        new(Substitute.For<IMediator>(), new OAuthAuthorizationStore(new OAuthStateStore(db, TimeProvider.System), TimeProvider.System),
            new GenericRepository<ApiKey>(db), new UnitOfWork(db, new DatabaseConnectionSettings()),
            Options.Create(new GoogleSettings()), new ConfigurationBuilder().Build(), NullLogger<OAuthController>.Instance, Substitute.For<IOAuthClientMetadataService>())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public int Failures { get; private set; }
        public int Commits { get; private set; }
        public bool RollbackRetry { get; init; }
        public bool RollbackFirstCommit { get; init; }
        public int RolledBackAttempts { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled && RollbackFirstCommit && Failures == 0)
            {
                Failures++;
                throw new NpgsqlException("First attempt failed before committing", new TimeoutException());
            }
            if (Enabled && RollbackRetry && Failures == 1 && RolledBackAttempts == 0)
            {
                RolledBackAttempts++;
                throw new NpgsqlException("Retry failed before committing", new TimeoutException());
            }
            return ValueTask.FromResult(result);
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => FailOnce();

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            FailOnce();
            return Task.CompletedTask;
        }

        public void FailOnce()
        {
            if (!Enabled) return;
            Commits++;
            if (Failures != 0) return;
            Failures++;
            throw new NpgsqlException("Commit acknowledgement lost after PostgreSQL committed", new TimeoutException());
        }
    }

    private sealed class AutocommitAcknowledgement(LostAcknowledgement failure) : DbCommandInterceptor
    {
        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            if (command.Transaction is null && command.CommandText.Contains("\"OAuthStates\"", StringComparison.Ordinal)
                && (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)
                    || command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)))
                failure.FailOnce();
            return result;
        }
    }
}
