using System.Text.Json;
using FluentAssertions;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.Chat;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Tags.Queries;

namespace Orbit.Application.Tests.Chat;

public class RecordListCardBuilderTests
{
    [Fact]
    public void Notifications_UsesTotalCountAndTenNewest()
    {
        var start = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var items = Enumerable.Range(0, 37)
            .Select(index => new NotificationItemDto(
                Guid.NewGuid(), $"Notice {index}", new string('x', 130),
                "https://example.test/private", null, index % 2 == 0, start.AddMinutes(index)))
            .ToList();

        var card = RecordListCardBuilder.BuildNotifications(new GetNotificationsResponse(items, 18, 37));

        card.TotalCount.Should().Be(37);
        card.Items.Should().HaveCount(10);
        card.Items[0].Title.Should().Be("Notice 36");
        card.Items[0].Detail.Should().HaveLength(120);
        card.NextCursor.Should().NotBeNull();
        JsonSerializer.Serialize(card, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().NotContain("example.test");
    }

    [Fact]
    public void Tags_DoNotSerializeColor()
    {
        var card = RecordListCardBuilder.BuildTags([new TagResponse(Guid.NewGuid(), "Health", "#ff0000")]);

        JsonSerializer.Serialize(card, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ToLowerInvariant()
            .Should().NotContain("color")
            .And.NotContain("#ff0000");
    }

    [Fact]
    public void Keys_ExposeOnlyIdNamePrefixDateAndState()
    {
        var now = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var key = new ApiKeyResponse(Guid.NewGuid(), "Automation", "orb_123", ["read"],
            true, now.AddDays(-1), now.AddDays(-7), null, false);

        var card = RecordListCardBuilder.BuildKeys([key], now);

        card.Items.Single().Detail.Should().Be("orb_123");
        card.Items.Single().State.Should().Be("expired");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(card.Items.Single(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        json.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["id", "title", "detail", "date", "state"]);
    }

    [Fact]
    public void Keys_RevokedState_OmitsEnglishSuffixAndOverridesExpiry()
    {
        var now = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var key = new ApiKeyResponse(Guid.NewGuid(), "Automation", "orb_123", ["read"],
            true, now.AddDays(-1), now.AddDays(-7), null, true);

        var item = RecordListCardBuilder.BuildKeys([key], now).Items.Single();

        item.State.Should().Be("revoked");
        item.Detail.Should().Be("orb_123");
        item.Detail.Should().NotContain("revoked");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("a")]
    public void Cursor_RejectsMalformedInput(string cursor)
    {
        RecordListCursor.TryRead(cursor, Guid.NewGuid(), "tags", out _).Should().BeFalse();
    }

    [Fact]
    public void Cursor_RejectsWrongPageBoundary()
    {
        var userId = Guid.NewGuid();

        RecordListCursor.TryRead(RecordListCursor.Create(userId, "tags", 11), userId, "tags", out _)
            .Should().BeFalse();
    }
}
