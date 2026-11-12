using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

internal static class AccountRemovalRows
{
    internal static void Seed(OrbitDbContext db, Guid userId)
    {
        var session = UserSession.Create(userId, $"session-{userId}", null).Value;
        db.UserSessions.Add(session);
        db.UserSessionRefreshTokens.Add(UserSessionRefreshToken.Create(session.Id, $"refresh-{userId}").Value);
        db.AiBudgetCalls.Add(AiBudgetCall.Reserve(userId, new DateOnly(2026, 1, 1), 1));
        db.AppFeatureFlagAccounts.Add(AppFeatureFlagAccount.Create("notion_integration", userId));
        db.ConversationOffers.Add(ConversationOffer.Create(userId, Guid.NewGuid(), "owned-offer").Value);
        db.DesktopHandoffs.Add(DesktopHandoff.Create(userId, userId.ToString("N") + userId.ToString("N"), "state", new string('b', 43),
            "S256", "orbit://auth", "orbit://auth", DateTime.UtcNow).Value);
        db.IssuedAiBlocks.Add(IssuedAiBlock.Create(userId, new("choice", "When?",
            [new("a", "Morning"), new("b", "Evening")], [])));
        db.ApiKeys.Add(ApiKey.Create(userId, "Connector", oauthFamilyId: Guid.NewGuid(), oauthClientId: "client").Value.Entity);
    }

    internal static async Task AssertAsync(OrbitDbContext db, Guid userId, int dataCount, int sessionCount, bool erased)
    {
        (await db.Database.SqlQueryRaw<int>("PRAGMA foreign_keys").ToListAsync()).Should().Equal(1);
        (await db.UserSessions.CountAsync(row => row.UserId == userId)).Should().Be(sessionCount);
        (await db.UserSessionRefreshTokens.CountAsync(row => db.UserSessions.Any(session => session.Id == row.UserSessionId && session.UserId == userId)))
            .Should().Be(sessionCount);
        if (erased)
        {
            (await db.UserSessionRefreshTokens.CountAsync()).Should().Be(1);
            (await db.AiBudgetCalls.CountAsync(row => row.UserId == userId)).Should().Be(dataCount);
            (await db.AppFeatureFlagAccounts.CountAsync(row => row.UserId == userId)).Should().Be(dataCount);
        }
        (await db.ConversationOffers.CountAsync(row => row.UserId == userId)).Should().Be(dataCount);
        (await db.DesktopHandoffs.CountAsync(row => row.UserId == userId)).Should().Be(dataCount);
        (await db.IssuedAiBlocks.CountAsync(row => row.UserId == userId)).Should().Be(dataCount);
        (await db.ApiKeys.CountAsync(row => row.UserId == userId && row.OAuthFamilyId != null && row.OAuthClientId != null)).Should().Be(dataCount);
    }
}
