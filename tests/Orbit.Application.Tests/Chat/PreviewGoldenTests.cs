using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Chat;

/// <summary>
/// Pins the <c>Items</c> and <c>Changes</c> of one preview for each item builder, in en and pt-BR, through the public
/// previewer method. The golden file holds the values that the previewer gave before the fingerprint moved to the hash
/// view. Set <c>ORBIT_RECORD_PREVIEW_GOLDEN=1</c> to write the file again.
/// </summary>
public sealed partial class PreviewGoldenTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid ConversationId = Guid.Parse("00000000-0000-0000-0000-0000000000cc");
    private static readonly DateOnly Today = new(2026, 10, 19);
    private static readonly string[] Locales = ["en", "pt-BR"];
    private readonly OrbitDbContext _db = new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly PendingOperationChangePreviewer _previewer;
    private readonly Habit[] _water;
    private readonly Habit _read;
    private readonly Habit _reminder;
    private readonly Habit _routine;
    private readonly Goal _goal;
    private readonly Tag _tag;

    public PreviewGoldenTests()
    {
        var user = User.Create("Alex", "alex@example.com").Value;
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(user, UserId);
        user.GrantLifetimePro();
        _db.Users.Add(user);
        _dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _dates.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        _water = Enumerable.Range(1, 7).Select(index => AddHabit(index, "Water")).ToArray();
        _read = AddHabit(8, "Read", emoji: "📖");
        _reminder = AddHabit(9, "Stretch", repeat: new(480, StartAtUtc: new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc)));
        _routine = AddHabit(10, "Morning routine");
        _routine.UpdateChecklist([new ChecklistItem("Coffee", false)]);
        _goal = Goal.Create(UserId, "Read 12 books", 12, "books").Value;
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(_goal, Id(20));
        _db.Goals.Add(_goal);
        _tag = Tag.Create(UserId, "Health", "#123456").Value;
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(_tag, Id(30));
        _db.Tags.Add(_tag);
        _db.SaveChanges();
        var habits = new GenericRepository<Habit>(_db);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var inference = Substitute.For<IHabitEmojiInferenceService>();
        inference.InferAsync(UserId, Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success<IReadOnlyDictionary<Guid, string>>(call.Arg<IReadOnlyList<HabitEmojiInferenceInput>>()
                .ToDictionary(input => input.HabitId, _ => "💧")));
        var unit = Substitute.For<IUnitOfWork>();
        var planner = new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), unit);
        var registry = new AiToolRegistry([
            new CreateHabitTool(_dates, mediator), new CreateSubHabitTool(mediator), new BulkCreateHabitsTool(mediator),
            new UpdateHabitTool(habits, _dates, mediator), new BulkUpdateHabitsTool(mediator),
            new BulkDeleteHabitsTool(mediator, habits), new BulkLogHabitsTool(mediator, habits, _dates, planner),
            new BulkSkipHabitsTool(mediator, habits, _dates, planner),
            new BulkUpdateHabitEmojisTool(habits, inference, unit, NullLogger<BulkUpdateHabitEmojisTool>.Instance),
            new SkipHabitTool(mediator, habits), new DeleteHabitTool(mediator, habits), new UpdateChecklistTool(mediator, habits),
            new AssignTagsTool(habits, new GenericRepository<Tag>(_db), unit), new CreateGoalTool(mediator),
            new DeleteGoalTool(mediator)]);
        _previewer = new PendingOperationChangePreviewer(habits, new GenericRepository<Goal>(_db),
            new GenericRepository<Tag>(_db), _dates, registry, new DestructiveOperationPreviewer(_db));
    }

    [Fact]
    public async Task Golden_EachItemBuilderKeepsTheValuesRecordedBeforeTheHashView()
    {
        var actual = new JsonObject();
        foreach (var (name, operationId, arguments, offers) in Cases())
            foreach (var locale in Locales)
                actual[$"{name}/{locale}"] = await ValuesAsync(operationId, arguments, Client(locale, offers));
        var text = Normalize(actual.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));

        if (Environment.GetEnvironmentVariable("ORBIT_RECORD_PREVIEW_GOLDEN") == "1")
            await File.WriteAllTextAsync(GoldenPath(), text + "\n");

        text.Should().Be((await File.ReadAllTextAsync(GoldenPath())).TrimEnd('\n'));
    }

    private async Task<JsonNode> ValuesAsync(string operationId, string arguments, AgentClientContext client)
    {
        var result = await _previewer.PreviewAsync(UserId, operationId, JsonDocument.Parse(arguments).RootElement.Clone(),
            default, client);
        if (result.IsFailure) return JsonValue.Create("failure:" + (result.ErrorCode ?? result.Error))!;
        if (result.Value is not { } preview) return JsonValue.Create("no_preview")!;
        return new JsonObject
        {
            ["changes"] = JsonSerializer.SerializeToNode(preview.Changes),
            ["items"] = JsonSerializer.SerializeToNode(preview.Items)
        };
    }

    private IEnumerable<(string Name, string OperationId, string Arguments, bool Offers)> Cases()
    {
        var water = _water.Select(habit => habit.Id).ToArray();
        string Json(object value) => JsonSerializer.Serialize(value);
        yield return ("01-bulk-update", "bulk_update_habits", Json(new { filter = new { habit_ids = water[..2] }, updates = new { title = "Agua" } }), false);
        yield return ("01-bulk-delete", "bulk_delete_habits", Json(new { filter = new { habit_ids = water[..2] } }), false);
        yield return ("01-bulk-emoji", "bulk_update_habit_emojis", Json(new { filter = new { habit_ids = water[..2] }, emoji = "🥤" }), false);
        yield return ("01-bulk-log", "bulk_log_habits", Json(new { filter = new { habit_ids = water[..2] } }), false);
        yield return ("01-bulk-skip", "bulk_skip_habits", Json(new { filter = new { habit_ids = water[..2] } }), false);
        yield return ("02-dated-skip", "bulk_skip_habits", Json(new { revised_items = water[..2].Select(id => new { habit_id = id, date = "2026-10-19" }) }), false);
        yield return ("02-dated-log", "bulk_log_habits", Json(new { revised_items = water[..2].Select(id => new { habit_id = id, date = "2026-10-18" }) }), false);
        yield return ("03-single-skip", "skip_habit", Json(new { habit_id = _read.Id }), false);
        yield return ("03-single-update-reminder", "update_habit", Json(new { habit_id = _reminder.Id, repeat_reminder = new { interval_minutes = 30 } }), false);
        yield return ("04-assign-tags", "assign_tags", Json(new { habit_ids = new[] { _read.Id, water[0] }, tag_names = new[] { "Health" } }), false);
        yield return ("05-checklist-append", "update_checklist", Json(new { habit_id = _routine.Id, item_to_add = "Stretch" }), false);
        yield return ("06-single-delete", "delete_habit", Json(new { habit_id = _read.Id }), false);
        yield return ("07-create-reminder", "create_habit", Json(new { title = "Drink", frequency_unit = "Day", frequency_quantity = 1, repeat_reminder = new { interval_minutes = 180 } }), false);
        yield return ("07-create-placement", "create_sub_habit", Json(new { parent_habit_id = _routine.Id, title = "Stretch", frequency_unit = "Day" }), false);
        yield return ("08-emoji-inference", "bulk_update_habit_emojis", Json(new { filter = new { habit_ids = water[..2] }, infer_from_title = true }), false);
        yield return ("09-destructive-goal", "delete_goal", Json(new { goal_id = _goal.Id }), false);
        yield return ("09-destructive-tag", "delete_tag", Json(new { tag_id = _tag.Id }), false);
        yield return ("10-dependency-names", "create_goal", Json(new { title = "Read more", habit_ids = new[] { _read.Id } }), false);
        yield return ("11-date-field", "create_habit", Json(new { title = "Submit form", frequency_unit = "OneTime", due_date = "2026-10-21", due_time = "14:00" }), false);
        yield return ("12-offer-repeat", "create_habit", Json(new { title = "Water", is_general = true, suggested_fields = new { repeat_reminder = new { interval_minutes = 120, active_start = "08:00", active_end = "20:00" }, reminder_enabled = true, emoji = "💧" } }), true);
        yield return ("12-offer-nested-repeat", "bulk_create_habits", """{"habits":[{"title":"Daily routine","frequency_unit":"Day","frequency_quantity":1,"sub_habits":[{"title":"Stretch","suggested_fields":{"repeat_reminder":{"interval_minutes":120},"reminder_enabled":true,"emoji":"🌱"}},{"title":"Read","due_time":"20:00"}]}]}""", true);
        yield return ("13-change-set", AccountChangeSet.OperationName, ChangeSet(
            ("bulk_delete_habits", Json(new { filter = new { habit_ids = water[..2] } })),
            ("bulk_update_habits", Json(new { filter = new { habit_ids = water[2..6] }, updates = new { title = "Agua" } })),
            ("skip_habit", Json(new { habit_id = water[6] }))), false);
        yield return ("14-set-members", AccountChangeSet.OperationName, ChangeSet(
            ("update_habit", Json(new { habit_id = _read.Id, title = "Read more" })),
            ("update_habit", Json(new { habit_id = _reminder.Id, repeat_reminder = new { interval_minutes = 30 } }))), false);
        yield return ("14-set-offer-member", AccountChangeSet.OperationName, ChangeSet(
            ("update_habit", Json(new { habit_id = _read.Id, title = "Read more" })),
            ("bulk_create_habits", """{"habits":[{"title":"Daily routine","frequency_unit":"Day","frequency_quantity":1,"sub_habits":[{"title":"Stretch","suggested_fields":{"repeat_reminder":{"interval_minutes":120},"reminder_enabled":true,"emoji":"🌱"}}]}]}""")), true);
        yield return ("15-picklist-skip", "bulk_skip_habits", AskTool.BuildBulkArguments("skip_habits", water[..2]
            .Select(id => new AiBlockItem(id.ToString(), "Water", JsonSerializer.SerializeToElement(new { habit_id = id, date = "2026-10-19" })))
            .ToArray()).GetRawText(), false);
    }

    private static string ChangeSet(params (string OperationId, string Arguments)[] members) =>
        JsonSerializer.Serialize(new
        {
            operations = members.Select((member, index) => new AccountChangeSet.Member(Id(100 + index), member.OperationId,
                JsonDocument.Parse(member.Arguments).RootElement.Clone(), "member-preview"))
        });

    private static AgentClientContext Client(string locale, bool offers) => new(Locale: locale, SupportsParts: true,
        SupportsPendingOperationChanges: true, SupportsPendingOperationPlacement: true,
        SupportsProactiveOffers: offers, ConversationId: ConversationId);

    private Habit AddHabit(int index, string title, string? emoji = null, RepeatReminderOptions? repeat = null)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, title, FrequencyUnit.Day, 1, Today,
            Emoji: emoji, ReminderEnabled: repeat is not null, RepeatReminder: repeat)).Value;
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(habit, Id(index));
        _db.Habits.Add(habit);
        return habit;
    }

    private static Guid Id(int index) => Guid.Parse($"00000000-0000-0000-0000-{index:D12}");

    private static string Normalize(string json) => Fingerprint().Replace(json, "<fingerprint>");

    [GeneratedRegex("[0-9A-F]{64}")]
    private static partial Regex Fingerprint();

    private static string GoldenPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Golden", "preview-values.json");

    public void Dispose() => _db.Dispose();
}
