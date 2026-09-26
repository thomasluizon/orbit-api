using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class DestructiveOperationPreviewerTests
{
    [Fact]
    public async Task DeleteAllNotifications_ListsOnlyOwnedTargetsAndDetectsStateChange()
    {
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"DestructivePreview_{Guid.NewGuid()}").Options;
        await using var db = new OrbitDbContext(options);
        var first = Notification.Create(userId, "First", "A");
        var second = Notification.Create(userId, "Second", "B");
        db.Notifications.AddRange(first, second,
            Notification.Create(Guid.NewGuid(), "Foreign", "C"));
        await db.SaveChangesAsync();
        var previewer = new DestructiveOperationPreviewer(db);
        var args = JsonDocument.Parse("""{"action":"delete_all"}""").RootElement.Clone();

        var before = await previewer.PreviewAsync(userId, "delete_notifications", args);
        first.MarkAsRead();
        await db.SaveChangesAsync();
        var after = await previewer.PreviewAsync(userId, "delete_notifications", args);

        before!.ChangeTargetCount.Should().Be(2);
        before.Items!.Select(item => item.EntityId).Should().BeEquivalentTo([first.Id, second.Id]);
        before.Items!.SelectMany(item => item.Fields).Select(field => field.Field)
            .Should().OnlyContain(field => field == "delete");
        after!.PreviewFingerprint.Should().NotBe(before.PreviewFingerprint);
    }

    [Fact]
    public async Task DeleteFacts_ListsEveryRequestedOwnedFact()
    {
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"DestructivePreview_{Guid.NewGuid()}").Options;
        await using var db = new OrbitDbContext(options);
        var first = UserFact.Create(userId, "Likes tea", null).Value;
        var second = UserFact.Create(userId, "Likes coffee", null).Value;
        db.UserFacts.AddRange(first, second);
        await db.SaveChangesAsync();
        var args = JsonDocument.Parse($"{{\"fact_ids\":[\"{first.Id}\",\"{second.Id}\"]}}")
            .RootElement.Clone();

        var preview = await new DestructiveOperationPreviewer(db)
            .PreviewAsync(userId, "delete_user_facts", args);

        preview!.Items.Should().HaveCount(2);
        preview.ChangeTargetCount.Should().Be(2);
    }
}
