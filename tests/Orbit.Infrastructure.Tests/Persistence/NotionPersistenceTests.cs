using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Notion;
using Orbit.Infrastructure.Tests.Services.Notion;

namespace Orbit.Infrastructure.Tests.Persistence;

public class NotionPersistenceTests
{
    [Fact]
    public async Task TokenColumnsContainCiphertextAndRoundTripThroughConverters()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        await using var command = test.Db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"AccessToken\", \"RefreshToken\" FROM \"NotionConnections\"";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().StartWith("enc:").And.NotContain("access-token");
            reader.GetString(1).Should().StartWith("enc:").And.NotContain("refresh-token");
        }
        var loaded = await test.Db.NotionConnections.AsNoTracking().SingleAsync();
        loaded.AccessToken.Should().Be("access-token");
        loaded.RefreshToken.Should().Be("refresh-token");
        var flag = await test.Db.AppFeatureFlags.SingleAsync(f => f.Key == "notion_integration");
        flag.Enabled.Should().BeFalse();
        flag.PlanRequirement.Should().Be("Pro");
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("reset")]
    [InlineData("delete")]
    public async Task DataRightsRemoveAllNotionRowsAndPreserveOtherPeople(string operation)
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        var other = User.Create("Other", "other@example.com").Value;
        test.Db.Users.Add(other);
        test.Db.NotionConnections.Add(NotionConnection.Create(other.Id, "other-access", null, Guid.NewGuid(), "Other", Guid.NewGuid()));
        foreach (var userId in new[] { test.User.Id, other.Id })
        {
            test.Db.NotionTasks.Add(NotionTask.Create(userId, NotionExamples.DatabaseId, Guid.NewGuid(), "Task",
                null, null, "https://notion.so/page", DateTime.UtcNow, DateTime.UtcNow));
            test.Db.NotionOAuthStates.Add(NotionOAuthState.Create(userId, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                NotionTestContext.RedirectUri, DateTime.UtcNow));
        }
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        var revokeSawLocalData = false;
        test.Api.RevokeAsync("access-token", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            revokeSawLocalData = await test.Db.NotionConnections.AnyAsync(c => c.UserId == test.User.Id)
                && await test.Db.NotionTasks.AnyAsync(t => t.UserId == test.User.Id);
        });
        if (operation == "disconnect")
        {
            (await test.Connections.DisconnectAsync(test.User.Id, CancellationToken.None)).IsSuccess.Should().BeTrue();
            revokeSawLocalData.Should().BeTrue();
            test.Analytics.Received(1).CaptureUserEvent(test.User.Id, "notion_disconnected", "pro");
        }
        else if (operation == "reset")
            await new AccountResetRepository(test.Db, Substitute.For<Orbit.Domain.Events.IAccountEventCollector>())
                .DeleteAllUserDataAsync(test.User.Id);
        else await test.Db.Users.Where(u => u.Id == test.User.Id).ExecuteDeleteAsync();
        (await test.Db.NotionConnections.AnyAsync(c => c.UserId == test.User.Id)).Should().BeFalse();
        (await test.Db.NotionTasks.AnyAsync(t => t.UserId == test.User.Id)).Should().BeFalse();
        (await test.Db.NotionOAuthStates.AnyAsync(s => s.UserId == test.User.Id)).Should().BeFalse();
        (await test.Db.NotionConnections.CountAsync(c => c.UserId == other.Id)).Should().Be(1);
        (await test.Db.NotionTasks.CountAsync(t => t.UserId == other.Id)).Should().Be(1);
        (await test.Db.NotionOAuthStates.CountAsync(s => s.UserId == other.Id)).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectStillRemovesLocalDataWhenRevocationFailsOrTimesOut(bool timeout)
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        test.Api.RevokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException(timeout ? new TaskCanceledException("Provider timed out.") : new NotionApiException(503)));
        var result = await test.Connections.DisconnectAsync(test.User.Id, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        (await test.Db.NotionConnections.AnyAsync()).Should().BeFalse();
    }
}
