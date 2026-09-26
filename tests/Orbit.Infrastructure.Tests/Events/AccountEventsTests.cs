using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Domain.Entities;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Events;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Events;

public class AccountEventsTests
{
    [Fact]
    public void Bus_ReplaysKnownId_AndResyncsUnknownId()
    {
        var bus = new InMemoryAccountEventBus();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        bus.Publish(userId, new AccountEventPayload(1, [new AccountChange("habit", "create", [Guid.NewGuid()])]));
        bus.TrySubscribe(userId, sessionId, null, out var first).Should().BeTrue();
        using (first!.Lease)
        {
            bus.Publish(userId, new AccountEventPayload(1, [new AccountChange("tag", "update", [Guid.NewGuid()])]));
            first.Reader.TryRead(out var seen).Should().BeTrue();
            bus.Publish(userId, new AccountEventPayload(1, [new AccountChange("goal", "create", [Guid.NewGuid()])]));
            bus.TrySubscribe(userId, sessionId, seen!.Id, out var resumed).Should().BeTrue();
            using (resumed!.Lease)
            {
                resumed.Resync.Should().BeFalse();
                resumed.Replay.Should().ContainSingle()
                    .Which.Data.Changes.Should().ContainSingle()
                    .Which.Kind.Should().Be("goal");
            }
        }

        bus.TrySubscribe(userId, sessionId, "missing.1", out var unknown).Should().BeTrue();
        using (unknown!.Lease)
            unknown.Resync.Should().BeTrue();
    }

    [Fact]
    public void Bus_IsolatesAccounts_CapsStreams_AndClosesRevokedSession()
    {
        var bus = new InMemoryAccountEventBus();
        var userId = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        bus.TrySubscribe(userId, sessionId, null, out var first).Should().BeTrue();
        bus.TrySubscribe(userId, Guid.NewGuid(), null, out var second).Should().BeTrue();
        bus.TrySubscribe(userId, Guid.NewGuid(), null, out var third).Should().BeTrue();
        using (first!.Lease)
        using (second!.Lease)
        using (third!.Lease)
        {
            bus.TrySubscribe(userId, Guid.NewGuid(), null, out _).Should().BeFalse();
            bus.TrySubscribe(otherUser, Guid.NewGuid(), null, out var other).Should().BeTrue();
            using (other!.Lease)
            {
                bus.Publish(userId, new AccountEventPayload(1, [new AccountChange("habit", "update", [Guid.NewGuid()])]));
                first.Reader.TryRead(out _).Should().BeTrue();
                other.Reader.TryRead(out _).Should().BeFalse();
                bus.CloseSession(sessionId);
                first.SessionClosed.IsCancellationRequested.Should().BeTrue();
                second.SessionClosed.IsCancellationRequested.Should().BeFalse();
            }
        }
    }

    [Fact]
    public void Bus_EvictsOldEvents_ReleasesLeases_AndClosesOnlyRequestedAccount()
    {
        var bus = new InMemoryAccountEventBus();
        var userId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        bus.TrySubscribe(userId, sessionId, null, out var first).Should().BeTrue();
        bus.TrySubscribe(otherId, Guid.NewGuid(), null, out var other).Should().BeTrue();
        using (other!.Lease)
        {
            bus.Publish(userId, new AccountEventPayload(1, []));
            first!.Reader.TryRead(out var oldest).Should().BeTrue();
            for (var i = 0; i < 256; i++)
                bus.Publish(userId, new AccountEventPayload(1, []));

            bus.TrySubscribe(userId, sessionId, oldest!.Id, out var expired).Should().BeTrue();
            using (expired!.Lease)
                expired.Resync.Should().BeTrue();

            bus.CloseAccount(userId);
            first.SessionClosed.IsCancellationRequested.Should().BeTrue();
            other.SessionClosed.IsCancellationRequested.Should().BeFalse();
            first.Lease.Dispose();
            bus.TrySubscribe(userId, Guid.NewGuid(), null, out var replacement).Should().BeTrue();
            using (replacement!.Lease)
                replacement.SessionClosed.IsCancellationRequested.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Collector_PublishesOneEventAfterCommit_AndNoneAfterRollback()
    {
        var bus = new InMemoryAccountEventBus();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext.Request.Headers["X-Orbit-Event-Origin"] = "origin-1";
        var collector = new AccountEventCollector(bus, accessor);
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var context = factory.Context;
        var user = User.Create("Owner", "owner@example.com").Value;
        var other = User.Create("Other", "other@example.com").Value;
        context.Users.AddRange(user, other);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        bus.TrySubscribe(user.Id, Guid.NewGuid(), null, out var ownerStream).Should().BeTrue();
        bus.TrySubscribe(other.Id, Guid.NewGuid(), null, out var otherStream).Should().BeTrue();
        using (ownerStream!.Lease)
        using (otherStream!.Lease)
        {
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                var first = Habit.Create(new HabitCreateParams(user.Id, "First", null, null, new DateOnly(2026, 1, 1))).Value;
                var second = Habit.Create(new HabitCreateParams(user.Id, "Second", null, null, new DateOnly(2026, 1, 1))).Value;
                context.Habits.Add(first);
                await context.SaveChangesAsync();
                context.Habits.Add(second);
                await context.SaveChangesAsync();
                ownerStream.Reader.TryRead(out _).Should().BeFalse();
                await transaction.CommitAsync();
                ownerStream.Reader.TryRead(out var committed).Should().BeTrue();
                committed!.Data.Origin.Should().Be("origin-1");
                committed.Data.Changes.Should().ContainSingle();
                committed.Data.Changes.Single().Ids.Should().BeEquivalentTo([first.Id, second.Id]);
                otherStream.Reader.TryRead(out _).Should().BeFalse();
            }

            context.ChangeTracker.Clear();
            var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings(), collector);
            Func<Task> rolledBack = () => unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                context.Habits.Add(Habit.Create(new HabitCreateParams(user.Id, "Rolled back", null, null, new DateOnly(2026, 1, 1))).Value);
                await context.SaveChangesAsync(ct);
                throw new InvalidOperationException("rollback");
            });
            await rolledBack.Should().ThrowAsync<InvalidOperationException>();
            ownerStream.Reader.TryRead(out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Collector_ReportsRelationshipOnlyChanges()
    {
        var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor());
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var context = factory.Context;
        var user = User.Create("Owner", "links@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Habit", null, null, new DateOnly(2026, 1, 1))).Value;
        var tag = Tag.Create(user.Id, "Focus", "#ffffff").Value;
        context.Users.Add(user);
        context.Habits.Add(habit);
        context.Tags.Add(tag);
        await context.SaveChangesAsync();
        bus.TrySubscribe(user.Id, Guid.NewGuid(), null, out var stream).Should().BeTrue();

        using (stream!.Lease)
        {
            habit.AddTag(tag);
            await context.SaveChangesAsync();
            stream.Reader.TryRead(out var item).Should().BeTrue();
            item!.Data.Changes.Should().Contain(change => change.Kind == "habit" && change.Ids.Contains(habit.Id));
            item.Data.Changes.Should().Contain(change => change.Kind == "tag" && change.Ids.Contains(tag.Id));
        }
    }

    [Fact]
    public async Task Collector_PublishesNotificationOnlyCreatesReadsAndDeletesAfterCommit()
    {
        var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor());
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var context = factory.Context;
        var user = User.Create("Owner", "notification-owner@example.com").Value;
        var other = User.Create("Other", "notification-other@example.com").Value;
        context.Users.AddRange(user, other);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        bus.TrySubscribe(user.Id, Guid.NewGuid(), null, out var ownerStream).Should().BeTrue();
        bus.TrySubscribe(other.Id, Guid.NewGuid(), null, out var otherStream).Should().BeTrue();
        using (ownerStream!.Lease)
        using (otherStream!.Lease)
        {
            var notification = Notification.Create(user.Id, "Reminder", "Body");
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                context.Notifications.Add(notification);
                await context.SaveChangesAsync();
                ownerStream.Reader.TryRead(out _).Should().BeFalse();
                await transaction.CommitAsync();
            }
            AssertNotificationChange(ownerStream, notification.Id, "create");
            otherStream.Reader.TryRead(out _).Should().BeFalse();

            notification.MarkAsRead();
            await context.SaveChangesAsync();
            AssertNotificationChange(ownerStream, notification.Id, "update");

            notification.SoftDelete();
            await context.SaveChangesAsync();
            AssertNotificationChange(ownerStream, notification.Id, "delete");

            var rolledBack = Notification.Create(user.Id, "Rolled back", "Body");
            Func<Task> rollback = () => new UnitOfWork(context, new DatabaseConnectionSettings(), collector)
                .ExecuteInTransactionAsync(async ct =>
                {
                    context.Notifications.Add(rolledBack);
                    await context.SaveChangesAsync(ct);
                    throw new InvalidOperationException("rollback");
                });
            await rollback.Should().ThrowAsync<InvalidOperationException>();
            ownerStream.Reader.TryRead(out _).Should().BeFalse();
            otherStream.Reader.TryRead(out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task SyncBatch_NotificationRead_PublishesCommittedChange()
    {
        var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor());
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var context = factory.Context;
        var user = User.Create("Owner", "batch-notification@example.com").Value;
        var notification = Notification.Create(user.Id, "Reminder", "Body");
        context.Users.Add(user);
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        bus.TrySubscribe(user.Id, Guid.NewGuid(), null, out var stream).Should().BeTrue();
        using (stream!.Lease)
        {
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
                ], "test"))
            };
            var controller = new SyncController(context, Substitute.For<ILogger<SyncController>>(), collector)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };
            var request = new SyncController.SyncBatchRequest([
                new SyncController.SyncMutation("notification", "read", notification.Id, null)
            ]);

            var result = await controller.ProcessBatch(request, CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>();
            AssertNotificationChange(stream, notification.Id, "update");
            context.Notifications.Single().IsRead.Should().BeTrue();
        }
    }

    private static void AssertNotificationChange(AccountEventSubscription stream, Guid notificationId, string op)
    {
        stream.Reader.TryRead(out var item).Should().BeTrue();
        item!.Type.Should().Be("changes");
        item.Data.Changes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new AccountChange("notification", op, [notificationId]));
        stream.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Collector_ClosesRevokedSessionOnlyAfterCommit()
    {
        var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor());
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var context = factory.Context;
        var user = User.Create("Owner", "session@example.com").Value;
        var session = UserSession.Create(user.Id, "token-hash", null).Value;
        context.Users.Add(user);
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();

        bus.TrySubscribe(user.Id, session.Id, null, out var stream).Should().BeTrue();
        using (stream!.Lease)
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            session.Revoke(DateTime.UtcNow);
            await context.SaveChangesAsync();
            stream.SessionClosed.IsCancellationRequested.Should().BeFalse();
            await transaction.CommitAsync();
            stream.SessionClosed.IsCancellationRequested.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Collector_PublishesResetOnlyAfterCommit()
    {
        var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor());
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var context = factory.Context;
        var userId = Guid.NewGuid();
        bus.TrySubscribe(userId, Guid.NewGuid(), null, out var stream).Should().BeTrue();

        using (stream!.Lease)
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            collector.MarkResync(userId);
            await context.SaveChangesAsync();
            stream.Reader.TryRead(out _).Should().BeFalse();
            await transaction.CommitAsync();
            stream.Reader.TryRead(out var item).Should().BeTrue();
            item!.Type.Should().Be("resync");
            item.Data.V.Should().Be(1);
        }
    }

    [Fact]
    public async Task Ticket_HasDedicatedAudience_AndExpiresAfterSixtySeconds()
    {
        var settings = new JwtSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience",
            ExpiryMinutes = 15
        };
        var service = new EventTicketService(Options.Create(settings));
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var accessToken = new JwtTokenService(Options.Create(settings))
            .GenerateToken(userId, "owner@example.com", sessionId);
        var accessValidation = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SecretKey)),
            ClockSkew = TimeSpan.Zero
        });
        accessValidation.IsValid.Should().BeTrue();
        var principal = new ClaimsPrincipal(accessValidation.ClaimsIdentity);
        var accessExpiry = principal.FindFirst("exp")!.Value;

        var (ticket, expiresAtUtc) = service.Create(principal);
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(ticket);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be(EventTicketService.AudienceFor(settings));
        jwt.Claims.Should().Contain(claim => claim.Type == "orbit_session_id" && claim.Value == sessionId.ToString());
        jwt.Claims.Should().Contain(claim => claim.Type == "orbit_access_exp" && claim.Value == accessExpiry);
        (expiresAtUtc - DateTime.UtcNow).Should().BeCloseTo(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(3));

        var validator = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var apiValidation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SecretKey)),
            ClockSkew = TimeSpan.Zero
        };
        Action useTicketAsApiToken = () => validator.ValidateToken(ticket, apiValidation, out _);
        useTicketAsApiToken.Should().Throw<SecurityTokenInvalidAudienceException>();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Ticket_RejectsMissingRequiredClaims(bool user, bool session, bool expiry)
    {
        var settings = new JwtSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience"
        };
        var claims = new List<Claim>();
        if (user) claims.Add(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        if (session) claims.Add(new Claim("orbit_session_id", Guid.NewGuid().ToString()));
        if (expiry) claims.Add(new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var service = new EventTicketService(Options.Create(settings));

        Action create = () => service.Create(principal);
        create.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Ticket_RejectsInvalidExpiry_AndResolvesOriginalAccessExpiry()
    {
        var settings = new JwtSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience"
        };
        var service = new EventTicketService(Options.Create(settings));
        var invalid = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("orbit_session_id", Guid.NewGuid().ToString()),
            new Claim("exp", "invalid")
        ], "test"));
        Action create = () => service.Create(invalid);
        create.Should().Throw<UnauthorizedAccessException>();

        var expiry = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var ticketPrincipal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("orbit_access_exp", expiry.ToString()),
            new Claim("exp", DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds().ToString())
        ], "test"));
        EventTicketService.GetStreamExpiry(ticketPrincipal).ToUnixTimeSeconds().Should().Be(expiry);
    }
}
