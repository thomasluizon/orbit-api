using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class FirstTryPreviewTests
{
    [Theory]
    [InlineData("en", "Skip", "Every 2 hours")]
    [InlineData("pt-BR", "Pular", "A cada 2 horas")]
    public async Task RevisedPreviews_StoreTheLocalizedValuesOfToday(string locale, string skip, string reminder)
    {
        var water = AddHabit("Water");
        var other = AddHabit("Stretch");
        var client = OfferClient(locale);
        var skipPreview = await BindAsync("bulk_skip_habits",
            new { revised_items = new[] { new { habit_id = water.Id, date = "2026-03-20" } } }, client);
        var reminderPreview = await BindAsync("update_habit",
            new { habit_id = other.Id, repeat_reminder = new { interval_minutes = 120 } }, client);
        var refreshed = await _revisions.RefreshAsync(_userId, skipPreview.Id, default, client);
        var revised = await _revisions.ReviseAsync(_userId, reminderPreview.Id, new(reminderPreview.PreviewFingerprint!,
            [new(reminderPreview.Items![0].ItemId, JsonSerializer.SerializeToElement(new { title = "Stretch more" }))], client), default);
        refreshed.IsSuccess.Should().BeTrue(refreshed.Error);
        revised.IsSuccess.Should().BeTrue(revised.Error);
        var first = await BindAsync("bulk_skip_habits",
            new { revised_items = new[] { new { habit_id = other.Id, date = "2026-03-20" } } }, client);
        var second = await BindAsync("update_habit", new { habit_id = water.Id, repeat_reminder = new { interval_minutes = 120 } }, client);
        var set = (await _changeSets.CombineAsync(_userId, [first, second], default, client))!.PendingOperation;
        var setRevised = await _revisions.ReviseAsync(_userId, set.Id, new(set.PreviewFingerprint!,
            set.Items!.Select(item => new RevisedPendingOperationItem(item.ItemId)).ToArray(), client), default);
        setRevised.IsSuccess.Should().BeTrue(setRevised.Error);

        StoredValues(skipPreview.Id).Should().Equal(skip);
        StoredValues(reminderPreview.Id).Should().ContainSingle().Which.Should().StartWith(reminder);
        StoredValues(set.Id).Should().HaveCount(2).And.Contain(skip).And.Contain(value => value!.StartsWith(reminder));
        refreshed.Preview!.Items!.Single().Fields.Single(field => field.Field == "action").NewValue.Should().Be(skip);
    }

    private IReadOnlyList<string?> StoredValues(Guid pendingId)
    {
        _factory.Context.ChangeTracker.Clear();
        var json = _factory.Context.PendingAgentOperations.AsNoTracking().Single(row => row.Id == pendingId).RevisedPreviewJson;
        json.Should().NotBeNull();
        var preview = JsonSerializer.Deserialize<PendingOperationChangePreview>(json!, JsonSerializerOptions.Web)!;
        return preview.Items!.SelectMany(item => item.Fields).Where(field => field.Field is "action" or "repeat_reminder")
            .Select(field => field.NewValue).ToArray();
    }
}
