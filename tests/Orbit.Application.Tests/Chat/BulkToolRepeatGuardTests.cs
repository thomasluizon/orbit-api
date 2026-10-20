using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class BulkToolRepeatGuardTests
{
    [Theory]
    [InlineData("update_habit", "bulk_update_habits")]
    [InlineData("log_habit", "bulk_log_habits")]
    [InlineData("skip_habit", "bulk_skip_habits")]
    [InlineData("delete_habit", "bulk_delete_habits")]
    [InlineData("create_habit", "bulk_create_habits")]
    [InlineData("create_sub_habit", "bulk_create_habits")]
    [InlineData("update_checklist", "bulk_update_habits")]
    public void FindRedirects_AtThreshold_RedirectsEveryRepeatedSingleEntityCall(string name, string bulkTool)
    {
        var calls = Enumerable.Range(1, BulkToolRepeatGuard.Threshold)
            .Select(index => Call(name, $"call_{index}"))
            .ToList();

        var redirects = BulkToolRepeatGuard.FindRedirects(calls);

        redirects.Should().HaveCount(BulkToolRepeatGuard.Threshold);
        redirects.Values.Should().OnlyContain(value => value == bulkTool);
    }

    [Fact]
    public void FindRedirects_BelowThreshold_DoesNotFire()
    {
        var calls = Enumerable.Range(1, BulkToolRepeatGuard.Threshold - 1)
            .Select(index => Call("update_habit", $"call_{index}")).ToList();

        BulkToolRepeatGuard.FindRedirects(calls).Should().BeEmpty();
    }

    [Fact]
    public void FindRedirects_DistinctSingleEntityOperations_DoesNotFire()
    {
        var calls = new[]
        {
            Call("update_habit", "call_1"),
            Call("log_habit", "call_2"),
            Call("skip_habit", "call_3")
        };

        BulkToolRepeatGuard.FindRedirects(calls).Should().BeEmpty();
    }

    [Fact]
    public void FindRedirects_HeterogeneousUpdates_DoesNotFire()
    {
        var calls = new[]
        {
            Call("update_habit", "call_1", """{"habit_id":"00000000-0000-0000-0000-000000000001","title":"First"}"""),
            Call("update_habit", "call_2", """{"habit_id":"00000000-0000-0000-0000-000000000002","title":"Second"}"""),
            Call("update_habit", "call_3", """{"habit_id":"00000000-0000-0000-0000-000000000003","title":"Third"}""")
        };

        BulkToolRepeatGuard.FindRedirects(calls).Should().BeEmpty();
    }

    [Fact]
    public void FindRedirects_EquivalentUpdatesWithDifferentHabitIds_RedirectsAll()
    {
        var calls = new[]
        {
            Call("update_habit", "call_1", """{"habit_id":"00000000-0000-0000-0000-000000000001","description":"Shared"}"""),
            Call("update_habit", "call_2", """{"description":"Shared","habit_id":"00000000-0000-0000-0000-000000000002"}"""),
            Call("update_habit", "call_3", """{"habit_id":"00000000-0000-0000-0000-000000000003","description":"Shared"}""")
        };

        var redirects = BulkToolRepeatGuard.FindRedirects(calls);

        redirects.Should().HaveCount(3);
        redirects.Values.Should().OnlyContain(value => value == "bulk_update_habits");
    }

    private static AiToolCall Call(string name, string id) =>
        new(name, id, JsonDocument.Parse("{}").RootElement.Clone());

    private static AiToolCall Call(string name, string id, string args) =>
        new(name, id, JsonDocument.Parse(args).RootElement.Clone());
}
