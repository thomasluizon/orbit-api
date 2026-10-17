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
        before.Items.Should().OnlyContain(item => item.RemovesData == true);
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
        preview.Items.Should().OnlyContain(item => item.RemovesData == true);
        preview.ChangeTargetCount.Should().Be(2);
    }

    [Fact]
    public async Task DismissCalendarSuggestion_PreviewsOwnedSuggestionWithoutRemovingDataAndDetectsStateChange()
    {
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"DestructivePreview_{Guid.NewGuid()}").Options;
        await using var db = new OrbitDbContext(options);
        var startDateUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var suggestion = GoogleCalendarSyncSuggestion.Create(userId, "owned-event", "Team meeting",
            startDateUtc, "{}", startDateUtc.AddDays(-1));
        var foreign = GoogleCalendarSyncSuggestion.Create(Guid.NewGuid(), "foreign-event", "Foreign meeting",
            startDateUtc, "{}", startDateUtc.AddDays(-1));
        db.GoogleCalendarSyncSuggestions.AddRange(suggestion, foreign);
        await db.SaveChangesAsync();
        var previewer = new DestructiveOperationPreviewer(db);
        var args = JsonDocument.Parse($$"""{"action":"dismiss_suggestion","suggestion_id":"{{suggestion.Id}}"}""")
            .RootElement.Clone();
        var foreignArgs = JsonDocument.Parse($$"""{"action":"dismiss_suggestion","suggestion_id":"{{foreign.Id}}"}""")
            .RootElement.Clone();

        var before = await previewer.PreviewAsync(userId, "manage_calendar_sync", args);
        suggestion.MarkDismissed(startDateUtc.AddHours(1));
        await db.SaveChangesAsync();
        var after = await previewer.PreviewAsync(userId, "manage_calendar_sync", args);
        var foreignPreview = await previewer.PreviewAsync(userId, "manage_calendar_sync", foreignArgs);

        before.Should().NotBeNull();
        before!.ChangeTargetCount.Should().Be(1);
        var item = before.Items.Should().ContainSingle().Which;
        item.EntityId.Should().Be(suggestion.Id);
        item.EntityName.Should().Be(suggestion.Title);
        after.Should().NotBeNull();
        var changedItem = after!.Items.Should().ContainSingle().Which;
        changedItem.StateFingerprint.Should().NotBe(item.StateFingerprint);
        after.PreviewFingerprint.Should().NotBe(before.PreviewFingerprint);
        foreignPreview.Should().NotBeNull();
        foreignPreview!.ChangeTargetCount.Should().Be(0);
        foreignPreview.Items.Should().BeEmpty();
        foreignPreview.Changes.Should().BeEmpty();
        item.RemovesData.Should().BeFalse();
        changedItem.RemovesData.Should().BeFalse();
    }

    [Fact]
    public async Task SetCalendarAutoSync_PreviewsCurrentAndProposedValuesWithoutRemovingData()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"DestructivePreview_{Guid.NewGuid()}").Options;
        await using var db = new OrbitDbContext(options);
        var user = User.Create("Calendar user", "calendar@example.com").Value;
        user.DisableCalendarAutoSync();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var args = JsonDocument.Parse("""{"action":"set_auto_sync","enabled":true}""").RootElement.Clone();

        var preview = await new DestructiveOperationPreviewer(db)
            .PreviewAsync(user.Id, "manage_calendar_sync", args);

        preview.Should().NotBeNull();
        preview!.ChangeTargetCount.Should().Be(1);
        var item = preview.Items.Should().ContainSingle().Which;
        item.EntityId.Should().Be(user.Id);
        item.EntityName.Should().Be("Calendar sync");
        item.RemovesData.Should().BeFalse();
        var change = item.Fields.Should().ContainSingle().Which;
        change.Field.Should().Be("enabled");
        change.OldValue.Should().Be(user.GoogleCalendarAutoSyncEnabled.ToString());
        change.NewValue.Should().Be(bool.TrueString);
        change.ValueType.Should().Be("boolean");
        change.ProposedValue!.Value.GetBoolean().Should().BeTrue();
        change.IsEditable.Should().BeTrue();
        preview.Changes.Should().ContainSingle().Which.Should().Be(change);
    }

    [Fact]
    public async Task RunCalendarSync_PreviewsActionWithoutRemovingData()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"DestructivePreview_{Guid.NewGuid()}").Options;
        await using var db = new OrbitDbContext(options);
        var user = User.Create("Calendar user", "calendar@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var args = JsonDocument.Parse("""{"action":"run_sync"}""").RootElement.Clone();

        var preview = await new DestructiveOperationPreviewer(db)
            .PreviewAsync(user.Id, "manage_calendar_sync", args);

        preview.Should().NotBeNull();
        preview!.ChangeTargetCount.Should().Be(1);
        var item = preview.Items.Should().ContainSingle().Which;
        item.EntityId.Should().Be(user.Id);
        item.EntityName.Should().Be("Calendar sync");
        item.RemovesData.Should().BeFalse();
        var change = item.Fields.Should().ContainSingle().Which;
        change.Field.Should().Be("run_sync");
        change.ValueType.Should().Be("action");
        change.NewValue.Should().BeNull();
        change.IsEditable.Should().BeFalse();
        preview.Changes.Should().ContainSingle().Which.Should().Be(change);
    }

    [Fact]
    public async Task UnknownCalendarAction_ReturnsNoPreview()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"DestructivePreview_{Guid.NewGuid()}").Options;
        await using var db = new OrbitDbContext(options);
        var user = User.Create("Calendar user", "calendar@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var args = JsonDocument.Parse("""{"action":"unknown"}""").RootElement.Clone();

        var preview = await new DestructiveOperationPreviewer(db)
            .PreviewAsync(user.Id, "manage_calendar_sync", args);

        preview.Should().BeNull();
    }
}
