using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed partial class PendingOperationChangePreviewerTests
{
    [Theory]
    [InlineData("update_habit", """{"habit_id":"$id","emoji":"📚","due_date":"2026-10-01"}""")]
    [InlineData("log_habit", """{"habit_id":"$id"}""")]
    [InlineData("skip_habit", """{"habit_id":"$id"}""")]
    [InlineData("delete_habit", """{"habit_id":"$id"}""")]
    [InlineData("assign_tags", """{"habit_ids":["$id"],"tag_names":["Health"]}""")]
    [InlineData("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"📚"}}""")]
    [InlineData("bulk_reschedule_habits", """{"filter":{"all":true},"due_date":"2026-10-01"}""")]
    [InlineData("bulk_delete_habits", """{"filter":{"all":true}}""")]
    [InlineData("bulk_log_habits", """{"filter":{"all":true}}""")]
    [InlineData("bulk_skip_habits", """{"filter":{"all":true}}""")]
    [InlineData("bulk_update_habit_emojis", """{"filter":{"all":true},"emoji":"📚"}""")]
    [InlineData("bulk_log_habits", """{"revised_items":[{"habit_id":"$id","date":"2026-09-25"}]}""")]
    [InlineData("bulk_skip_habits", """{"revised_items":[{"habit_id":"$id","date":"2026-09-25"}]}""")]
    public async Task HabitPreview_UsesStoredEmoji(string operation, string arguments)
    {
        var habit = CreateHabit("Brush teeth", "🦷");
        Setup([habit]);

        var preview = await Preview(operation, arguments.Replace("$id", habit.Id.ToString()));

        preview.Should().NotBeNull();
        AssertEmoji(preview!, "🦷");
        habit.Emoji.Should().Be("🦷");
    }

    [Theory]
    [InlineData("create_habit", """{"title":"Brush teeth","emoji":"🦷"}""")]
    [InlineData("create_sub_habit", """{"title":"Brush teeth","emoji":"🦷"}""")]
    [InlineData("bulk_create_habits", """{"habits":[{"title":"Brush teeth","emoji":"🦷"}]}""")]
    public async Task CreatePreview_UsesDraftEmoji(string operation, string arguments)
    {
        var preview = await Preview(operation, arguments);

        preview.Should().NotBeNull();
        AssertEmoji(preview!, "🦷");
    }

    [Theory]
    [InlineData("update_habit", """{"habit_id":"$id","emoji":"📚"}""")]
    [InlineData("bulk_update_habits", """{"filter":{"all":true},"updates":{"emoji":"📚"}}""")]
    [InlineData("create_habit", """{"title":"Read"}""")]
    [InlineData("bulk_create_habits", """{"habits":[{"title":"Read"}]}""")]
    [InlineData("create_habit", """{"title":"Read","emoji":"  "}""")]
    [InlineData("bulk_create_habits", """{"habits":[{"title":"Read","emoji":null}]}""")]
    [InlineData("create_goal", """{"title":"Read","emoji":"📚"}""")]
    public async Task Preview_WithoutHabitEmoji_OmitsMetadata(string operation, string arguments)
    {
        var habit = CreateHabit("Read", null);
        Setup([habit]);

        var preview = await Preview(operation, arguments.Replace("$id", habit.Id.ToString()));

        preview.Should().NotBeNull();
        AssertEmoji(preview!, null);
    }

    [Fact]
    public async Task GoalPreview_OmitsHabitEmoji()
    {
        var goal = Goal.Create(UserId, "Read more", 10, "books").Value;
        _goals.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>()).Returns([goal]);

        var preview = await Preview("update_goal", JsonSerializer.Serialize(new { goal_id = goal.Id, title = "Read daily" }));

        preview.Should().NotBeNull();
        AssertEmoji(preview!, null);
    }

    [Theory]
    [InlineData("🦷")]
    [InlineData(null)]
    public async Task ReusedCreation_UsesStoredEmojiInsteadOfDraft(string? emoji)
    {
        var habit = CreateHabit("Brush teeth", emoji);
        Setup([habit]);
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns([habit]);

        var preview = await Preview("bulk_create_habits",
            $$"""{"habits":[{"habit_id":"{{habit.Id}}","title":"Brush teeth","emoji":"📚"}]}""");

        preview.Should().NotBeNull();
        AssertEmoji(preview!, emoji);
    }

    [Fact]
    public async Task AccountChanges_KeepEachTargetsEmoji()
    {
        var habit = CreateHabit("Brush teeth", "🦷");
        Setup([habit]);
        var arguments = JsonSerializer.Serialize(new
        {
            operations = new[]
            {
                new AccountChangeSet.Member(Guid.NewGuid(), "update_habit",
                    JsonSerializer.SerializeToElement(new { habit_id = habit.Id, emoji = "📚" }), "previous"),
                new AccountChangeSet.Member(Guid.NewGuid(), "create_habit",
                    JsonSerializer.SerializeToElement(new { title = "Walk", emoji = "🚶" }), "previous")
            }
        });

        var preview = await Preview(AccountChangeSet.OperationName, arguments);

        preview.Should().NotBeNull();
        var json = JsonSerializer.SerializeToElement(preview, JsonSerializerOptions.Web);
        json.GetProperty("items")[0].GetProperty("entityEmoji").GetString().Should().Be("🦷");
        json.GetProperty("items")[1].GetProperty("entityEmoji").GetString().Should().Be("🚶");
        foreach (var change in json.GetProperty("changes").EnumerateArray())
            change.GetProperty("entityEmoji").GetString().Should().Be(
                change.GetProperty("entityId").GetGuid() == habit.Id ? "🦷" : "🚶");
    }

    private static void AssertEmoji(PendingOperationChangePreview preview, string? expected)
    {
        preview.Items.Should().NotBeNullOrEmpty();
        var json = JsonSerializer.SerializeToElement(preview, JsonSerializerOptions.Web);
        foreach (var item in json.GetProperty("items").EnumerateArray())
        {
            AssertValue(item);
            foreach (var field in item.GetProperty("fields").EnumerateArray())
                AssertValue(field);
        }
        foreach (var change in json.GetProperty("changes").EnumerateArray())
            AssertValue(change);

        void AssertValue(JsonElement value)
        {
            value.TryGetProperty("entityEmoji", out var emoji).Should().Be(expected is not null);
            if (expected is not null) emoji.GetString().Should().Be(expected);
        }
    }
}
