using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Orbit.Api.Controllers;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ApplicationHistoryRetentionTests
{
    private static readonly DateTime NowAtUtc = new(2030, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-1)]
    public async Task MarketingDeliveries_NegativeWindow_IsRejectedBeforePurging(int days)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var receipt = Delivery(NowAtUtc.AddDays(-32));
        db.Add(receipt);
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackgroundServices:Retention:MarketingDeliveriesDays"] = days.ToString()
        }).Build();

        var purge = () => ApplicationHistoryRetention.PurgeAsync(db, configuration, new Clock(), default);

        await purge.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await db.MarketingDeliveries.AnyAsync(row => row.Id == receipt.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task MarketingDeliveries_PurgeBatchesSentRowsAndPreservesPendingAndBoundary()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var expired = Enumerable.Range(0, 5).Select(_ => Delivery(NowAtUtc.AddDays(-32))).ToArray();
        var boundary = Delivery(NowAtUtc.AddDays(-31));
        var recent = Delivery(NowAtUtc.AddDays(-1));
        var pending = MarketingDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "pending@example.com", "en",
            "Subject", "Assunto", "Body", "Corpo", NowAtUtc.AddDays(-100));
        factory.Context.AddRange(expired);
        factory.Context.AddRange(boundary, recent, pending);
        await factory.Context.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackgroundServices:CleanupBatchSize"] = "2"
        }).Build();

        await ApplicationHistoryRetention.PurgeAsync(factory.Context, configuration, new Clock(), default);

        (await factory.Context.MarketingDeliveries.AsNoTracking().Select(row => row.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { boundary.Id, recent.Id, pending.Id });
    }

    private static MarketingDelivery Delivery(DateTime sentAtUtc)
    {
        var row = MarketingDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), "recipient@example.com", "en",
            "Subject", "Assunto", "Body", "Corpo", sentAtUtc.AddMinutes(-1));
        row.MarkSent(sentAtUtc);
        return row;
    }

    [Theory]
    [InlineData("Notifications", 90)]
    [InlineData("AiBudgetCalls", 8)]
    [InlineData("PendingAgentOperations", 7)]
    [InlineData("Sessions", 30)]
    [InlineData("IssuedAstraBlocks", 30)]
    [InlineData("PendingClarifications", 7)]
    [InlineData("AgentAuditLogs", 365)]
    [InlineData("AiUsageDaily", 400)]
    [InlineData("DesktopHandoffs", 1)]
    [InlineData("ProactiveItems", 30)]
    [InlineData("ProactiveCalendar", 30)]
    [InlineData("MarketingDeliveries", 31)]
    public async Task Purge_OnlyRowsOutsideWindowAreDeleted_AndZeroDisables(string name, int days)
    {
        foreach (var enabled in new[] { false, true })
        {
            using var factory = new SqliteOrbitDbContextFactory();
            var db = factory.Context;
            var user = User.Create("History", "history@example.com").Value;
            db.Add(user);
            var expired = Row(name, user.Id, NowAtUtc.AddDays(-days - 1));
            var retained = Row(name, user.Id, NowAtUtc.AddDays(-days + 1));
            db.AddRange(expired, retained);
            if (name == "ProactiveItems")
            {
                db.Add(Row(name, user.Id, NowAtUtc.AddDays(-1)));
                db.Add(Row(name, user.Id, NowAtUtc.AddDays(-2)));
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"BackgroundServices:Retention:{name}Days"] = enabled ? days.ToString() : "0"
            }).Build();

            await ApplicationHistoryRetention.PurgeAsync(db, settings, new Clock(), default);

            (await ExistsAsync(db, retained)).Should().BeTrue();
            (await ExistsAsync(db, expired)).Should().Be(!enabled);
        }
    }

    [Fact]
    public async Task Sessions_ActiveSessionKeepsEveryToken_RevokedSessionLosesTokens()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Sessions", "sessions@example.com").Value;
        var active = UserSession.Create(user.Id, "active", NowAtUtc.AddDays(1)).Value;
        var revoked = UserSession.Create(user.Id, "revoked", NowAtUtc.AddDays(100)).Value;
        revoked.Revoke(NowAtUtc.AddDays(-31));
        var activeToken = UserSessionRefreshToken.Create(active.Id, "active-token").Value;
        var revokedToken = UserSessionRefreshToken.Create(revoked.Id, "revoked-token").Value;
        db.AddRange(user, active, revoked, activeToken, revokedToken);
        await db.SaveChangesAsync();

        await ApplicationHistoryRetention.PurgeAsync(db, new ConfigurationBuilder().Build(), new Clock(), default);

        (await db.UserSessions.Select(row => row.Id).ToListAsync()).Should().Equal(active.Id);
        (await db.UserSessionRefreshTokens.Select(row => row.Id).ToListAsync()).Should().Equal(activeToken.Id);
    }

    [Fact]
    public async Task ProactiveRetention_PreservesBoundedRestEvidenceBeyondThirtyDays()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Rest", "rest@example.com").Value;
        db.Add(user);
        var old = Enumerable.Range(0, 4).Select(index => (ProactiveItem)Row("ProactiveItems", user.Id, NowAtUtc.AddDays(-40 - index))).ToList();
        var recent = (ProactiveItem)Row("ProactiveItems", user.Id, NowAtUtc.AddDays(-1));
        db.AddRange(old);
        db.Add(recent);
        await db.SaveChangesAsync();
        var expected = ProactiveEngineService.IsResting(old.Append(recent), ProactiveTrigger.EveningCheckin, NowAtUtc);
        expected.Should().BeTrue();

        await ApplicationHistoryRetention.PurgeAsync(db, new ConfigurationBuilder().Build(), new Clock(), default);

        var remaining = await db.ProactiveItems.AsNoTracking().ToListAsync();
        remaining.Should().HaveCount(3);
        ProactiveEngineService.IsResting(remaining, ProactiveTrigger.EveningCheckin, NowAtUtc).Should().Be(expected);
        new[] { ProactiveRules.RestDays, ProactiveRules.GoalCooldownDays, ProactiveRules.RestartLookbackDays + 1, ProactiveRules.WeekDays }
            .Should().OnlyContain(window => window <= 30);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AiBudgetRetention_ExtendedWeeklyAllowance_KeepsCallsStillCountedByBudget(bool deactivated)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Budget", "budget@example.com").Value;
        user.GrantLifetimePro();
        user.SetWeekStartDay(1).IsSuccess.Should().BeTrue();
        var start = new DateOnly(2030, 6, 3);
        user.IncrementAiMessageCount(start);
        user.SetWeekStartDay(0).IsSuccess.Should().BeTrue();
        var today = DateOnly.FromDateTime(NowAtUtc);
        var active = AiAllowancePeriod.ActiveFor(today, true, user.WeekStartDay, user.AiMessagesWeekStart);
        var callDate = start.AddDays(1);
        today.DayNumber.Should().BeLessThan(active.End.DayNumber);
        callDate.DayNumber.Should().BeGreaterThanOrEqualTo(active.Start.DayNumber);
        if (deactivated) user.Deactivate(NowAtUtc.AddDays(30));
        var call = AiBudgetCall.Reserve(user.Id, callDate, 100);
        db.AddRange(user, call);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ApplicationHistoryRetention.PurgeAsync(db, new ConfigurationBuilder().Build(), new Clock(), default);

        (await db.AiBudgetCalls.Where(row => row.UserId == user.Id && row.LocalDate >= active.Start && row.LocalDate < active.End)
            .SumAsync(row => row.ChargedMicroDollars)).Should().Be(100);

        await ApplicationHistoryRetention.PurgeAsync(db, new ConfigurationBuilder().Build(), new Clock(3), default);
        (await db.AiBudgetCalls.AnyAsync(row => row.Id == call.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task NotificationRetention_OldLiveRowBecomesSyncTombstone_RecentTombstoneSurvives()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Tombstones", "tombstones@example.com").Value;
        var tombstone = Notification.Create(user.Id, "Old", "Deleted recently", "/");
        var live = Notification.Create(user.Id, "Old", "Still live", "/");
        Set(tombstone, nameof(Notification.CreatedAtUtc), NowAtUtc.AddDays(-100));
        Set(live, nameof(Notification.CreatedAtUtc), NowAtUtc.AddDays(-100));
        tombstone.SoftDelete();
        Set(tombstone, nameof(Notification.DeletedAtUtc), NowAtUtc.AddDays(-1));
        db.AddRange(user, tombstone, live);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ApplicationHistoryRetention.PurgeAsync(db, new ConfigurationBuilder().Build(), new Clock(), default);

        var rows = await db.Notifications.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        var deleted = rows.Single(row => row.Id == live.Id);
        deleted.IsDeleted.Should().BeTrue();
        deleted.DeletedAtUtc.Should().Be(NowAtUtc);
        deleted.UpdatedAtUtc.Should().Be(NowAtUtc);
        rows.Single(row => row.Id == tombstone.Id).DeletedAtUtc.Should().Be(NowAtUtc.AddDays(-1));
        var controller = new SyncController(db, NullLogger<SyncController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test")) }
            }
        };
        var response = (await controller.GetChangesV2(NowAtUtc.AddMinutes(-1), default))
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<SyncController.SyncChangesV2Response>().Subject;
        response.Notifications.Deleted.Should().ContainSingle(row => row.Id == live.Id && row.DeletedAtUtc == NowAtUtc);
        response.Notifications.Updated.Should().BeEmpty();
    }

    private static Task<bool> ExistsAsync(DbContext db, Entity row) => row switch
    {
        MarketingDelivery => db.Set<MarketingDelivery>().AnyAsync(item => item.Id == row.Id),
        Notification => db.Set<Notification>().AnyAsync(item => item.Id == row.Id),
        AiBudgetCall => db.Set<AiBudgetCall>().AnyAsync(item => item.Id == row.Id),
        PendingAgentOperationState => db.Set<PendingAgentOperationState>().AnyAsync(item => item.Id == row.Id),
        UserSession => db.Set<UserSession>().AnyAsync(item => item.Id == row.Id),
        IssuedAstraBlock => db.Set<IssuedAstraBlock>().AnyAsync(item => item.Id == row.Id),
        PendingClarification => db.Set<PendingClarification>().AnyAsync(item => item.Id == row.Id),
        AgentAuditLog => db.Set<AgentAuditLog>().AnyAsync(item => item.Id == row.Id),
        AiUsageDaily => db.Set<AiUsageDaily>().AnyAsync(item => item.Id == row.Id),
        DesktopHandoff => db.Set<DesktopHandoff>().AnyAsync(item => item.Id == row.Id),
        ProactiveItem => db.Set<ProactiveItem>().AnyAsync(item => item.Id == row.Id),
        ProactiveCalendarDay => db.Set<ProactiveCalendarDay>().AnyAsync(item => item.Id == row.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(row))
    };

    private static Entity Row(string name, Guid userId, DateTime atUtc)
    {
        Entity row = name switch
        {
            "MarketingDeliveries" => Delivery(atUtc),
            "Notifications" => Notification.Create(userId, "Title", "Body", "/"),
            "AiBudgetCalls" => AiBudgetCall.Reserve(userId, DateOnly.FromDateTime(atUtc), 1),
            "PendingAgentOperations" => PendingAgentOperationState.Create(new()
            {
                UserId = userId,
                Capability = new("capability", "Display", "Description", "domain", "scope", default, true, false, default),
                OperationId = "operation",
                ArgumentsJson = "{}",
                Summary = "Preview",
                OperationFingerprint = "fingerprint",
                Surface = AgentExecutionSurface.Chat,
                ExpiresAtUtc = atUtc
            }),
            "Sessions" => UserSession.Create(userId, Guid.NewGuid().ToString(), atUtc).Value,
            "IssuedAstraBlocks" => IssuedAstraBlock.Create(userId, new("choice", "When?", [new("a", "First"), new("b", "Second")], [])),
            "PendingClarifications" => PendingClarification.Create(userId, "tool", "{}", "field", "Question?", "[]", atUtc),
            "AgentAuditLogs" => AgentAuditLog.Create(new(userId, "capability", "source", default, default, default, default, default)),
            "AiUsageDaily" => AiUsageDaily.Create(DateOnly.FromDateTime(atUtc), "model", "purpose", new(1, 0, 1, 1, 2, 0), userId),
            "DesktopHandoffs" => DesktopHandoff.Create(userId, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), "state", new string('a', 43), "S256", "orbit://callback", "orbit://callback", atUtc).Value,
            "ProactiveItems" => ProactiveItem.Create(userId, ProactiveTrigger.EveningCheckin, DateOnly.FromDateTime(atUtc), "{}", "Text", "Action", "Prompt", atUtc),
            "ProactiveCalendar" => ProactiveCalendarDay.Claim(userId, DateOnly.FromDateTime(atUtc)),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        if (name is "Notifications" or "AgentAuditLogs") Set(row, "CreatedAtUtc", atUtc);
        if (name is "IssuedAstraBlocks" or "DesktopHandoffs") Set(row, "ExpiresAtUtc", atUtc);
        if (row is ProactiveItem item)
        {
            item.Show(atUtc);
            item.End(ProactiveEndReason.DayEnded, atUtc.AddHours(1));
        }
        return row;
    }

    private static void Set(Entity row, string property, DateTime atUtc) => row.GetType().GetProperty(property)!.SetValue(row, atUtc);
    private sealed class Clock(int offsetDays = 0) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(NowAtUtc.AddDays(offsetDays)); }
}
