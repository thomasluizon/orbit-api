using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Chat;

/// <summary>
/// The same-title detail on the items that the real previewer builds. The account holds six "Water" habits: four
/// under "Evening", which comes first in the app list, and two under "Morning". It also holds two top-level "Tea"
/// habits and one "Walk".
/// </summary>
public sealed partial class SameTitleDetailTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000ab");
    private static readonly DateOnly Today = new(2026, 10, 19);
    private readonly OrbitDbContext _db = new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly PendingOperationChangePreviewer _previewer;
    private readonly SameTitleDetailWriter _writer;
    private readonly User _user;
    private readonly Habit _morning;
    private readonly Habit[] _water;
    private readonly Habit[] _tea;
    private readonly Habit _walk;

    public SameTitleDetailTests()
    {
        _user = User.Create("Alex", "alex@example.com").Value;
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(_user, UserId);
        _user.GrantLifetimePro();
        _db.Users.Add(_user);
        var evening = AddHabit("Evening", 1);
        _morning = AddHabit("Morning", 2);
        _water =
        [
            .. Enumerable.Range(1, 4).Select(position => AddHabit("Water", position, evening.Id)),
            .. Enumerable.Range(1, 2).Select(position => AddHabit("Water", position, _morning.Id))
        ];
        _tea = [AddHabit("Tea", 3), AddHabit("Tea", 4)];
        _walk = AddHabit("Walk", 5);
        _db.SaveChanges();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var habits = new GenericRepository<Habit>(_db);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var unit = Substitute.For<IUnitOfWork>();
        var planner = new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), unit);
        var registry = new AiToolRegistry([
            new BulkCreateHabitsTool(mediator), new UpdateHabitTool(habits, dates, mediator), new BulkUpdateHabitsTool(mediator),
            new BulkDeleteHabitsTool(mediator, habits), new DeleteHabitTool(mediator, habits),
            new BulkLogHabitsTool(mediator, habits, dates, planner), new BulkSkipHabitsTool(mediator, habits, dates, planner)]);
        _previewer = new PendingOperationChangePreviewer(habits, new GenericRepository<Goal>(_db),
            new GenericRepository<Tag>(_db), dates, registry);
        _writer = new SameTitleDetailWriter(habits, new GenericRepository<User>(_db));
    }

    private Habit AddHabit(string title, int position, Guid? parent = null)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, title, FrequencyUnit.Day, 1, Today,
            ParentHabitId: parent, Position: position)).Value;
        _db.Habits.Add(habit);
        return habit;
    }

    private static AgentClientContext Client(string locale, bool withFlag) => new(Locale: locale, SupportsParts: true,
        SupportsPendingOperationChanges: true, SupportsPendingOperationEntityDetail: withFlag ? true : null);

    private static string Place(string locale, int place, int total) =>
        locale == "en" ? $"{place} of {total}" : $"{place}º de {total}";

    private async Task<PendingAgentOperation> PreviewAsync(string operationId, object arguments, AgentClientContext? client)
    {
        var result = await _previewer.PreviewAsync(UserId, operationId, JsonSerializer.SerializeToElement(arguments), default, client);
        result.IsSuccess.Should().BeTrue(result.Error);
        var preview = result.Value!;
        return new PendingAgentOperation(Guid.NewGuid(), operationId, operationId, operationId, AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10), preview.Changes,
            preview.ChangeTargetCount, preview.Items, preview.PreviewFingerprint);
    }

    /// <summary>One preview of the fifth "Water" for each row kind: its operation and its arguments.</summary>
    private (string OperationId, object Arguments) Row(string kind)
    {
        var id = _water[4].Id;
        return kind switch
        {
            "delete" => ("delete_habit", new { habit_id = id }),
            "log" => ("bulk_log_habits", new { filter = new { habit_ids = new[] { id } } }),
            "skip" => ("bulk_skip_habits", new { filter = new { habit_ids = new[] { id } } }),
            "field-change" => ("update_habit", new { habit_id = id, description = "One glass" }),
            _ => ("update_habit", new { habit_id = id, title = "Agua" })
        };
    }

    public static TheoryData<string, string, bool> Rows()
    {
        var rows = new TheoryData<string, string, bool>();
        foreach (var kind in new[] { "delete", "log", "skip", "field-change", "rename" })
            foreach (var locale in new[] { "en", "pt-BR" })
                foreach (var withFlag in new[] { true, false })
                    rows.Add(kind, locale, withFlag);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Row_GetsTheDetailInTheFieldForAClientWithTheFlagAndInTheNameForAClientWithoutIt(string kind, string locale,
        bool withFlag)
    {
        var client = Client(locale, withFlag);
        var (operationId, arguments) = Row(kind);
        var preview = await PreviewAsync(operationId, arguments, client);
        var response = new ChatResponse("Review the change.", [], PendingOperations: [preview],
            Parts: [new ChatTextPart("Review the change.")]);

        var written = await _writer.WriteAsync(UserId, response, client, null, default);

        var plain = preview.Items!.Single();
        var item = written.PendingOperations!.Single().Items!.Single();
        var detail = $"Morning, {Place(locale, 5, 6)}";
        item.EntityName.Should().Be(withFlag ? "Water" : $"Water ({detail})");
        item.EntityDetail.Should().Be(withFlag ? detail : null);
        item.Should().BeEquivalentTo(plain, options => options
            .Excluding(value => value.EntityName).Excluding(value => value.EntityDetail));
        item.Fields.Should().OnlyContain(field => field.EntityName == "Water");
        written.PendingOperations!.Single().Changes.Should().BeEquivalentTo(preview.Changes);
        written.PendingOperations!.Single().PreviewFingerprint.Should().Be(preview.PreviewFingerprint);
        written.AiMessage.Should().Be("Review the change.");
        var texts = written.Parts!.OfType<ChatTextPart>().Select(part => part.Text).ToArray();
        if (kind == "rename" && !withFlag)
        {
            texts.Should().Equal("Review the change.", $"- Water ({detail}) → Agua");
            item.Fields.Single(field => field.Field == "title").NewValue.Should().Be("Agua");
        }
        else
            texts.Should().Equal("Review the change.");
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task RenameLines_GoIntoOneTextPartWhenPartsHasItemsAndIntoAiMessageWhenItHasNone(string locale, bool parts)
    {
        var client = Client(locale, withFlag: false);
        var preview = await PreviewAsync("bulk_update_habits",
            new { filter = new { habit_ids = new[] { _water[4].Id, _water[5].Id } }, updates = new { title = "Agua" } }, client);
        var response = new ChatResponse("Review the changes.", [], PendingOperations: [preview],
            Parts: parts ? [new ChatTextPart("Review the changes.")] : null);
        var lines = $"- Water (Morning, {Place(locale, 5, 6)}) → Agua\n- Water (Morning, {Place(locale, 6, 6)}) → Agua";

        var written = await _writer.WriteAsync(UserId, response, client, null, default);

        if (parts)
        {
            written.Parts!.OfType<ChatTextPart>().Select(part => part.Text).Should().Equal("Review the changes.", lines);
            written.AiMessage.Should().Be("Review the changes.");
        }
        else
        {
            written.Parts.Should().BeNull();
            written.AiMessage.Should().Be("Review the changes.\n\n" + lines);
        }
        written.PendingOperations!.Single().Items!.SelectMany(item => item.Fields)
            .Where(field => field.Field == "title").Should().OnlyContain(field => field.NewValue == "Agua");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task RenameLines_AreTheWholeAnswerWhenTheResponseHasNoText(string locale)
    {
        var client = Client(locale, withFlag: false);
        var preview = await PreviewAsync("update_habit", new { habit_id = _water[4].Id, title = "Agua" }, client);

        var written = await _writer.WriteAsync(UserId, new ChatResponse(null, [], PendingOperations: [preview]), client, null, default);

        written.AiMessage.Should().Be($"- Water (Morning, {Place(locale, 5, 6)}) → Agua");
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task Detail_IsThePlaceOnlyForARecordWithNoParentAndNothingForAUniqueTitleOrANewRecord(string locale, bool withFlag)
    {
        var client = Client(locale, withFlag);
        var delete = await PreviewAsync("bulk_delete_habits",
            new { filter = new { habit_ids = new[] { _tea[1].Id, _walk.Id } } }, client);
        var create = await PreviewAsync("bulk_create_habits",
            new { habits = new[] { new { title = "Water", frequency_unit = "Day", frequency_quantity = 1 } } }, client);

        var written = await _writer.WriteAsync(UserId, new ChatResponse("Review.", [], PendingOperations: [delete, create]),
            client, null, default);

        var items = written.PendingOperations!.SelectMany(operation => operation.Items!).ToArray();
        var tea = items.Single(item => item.EntityId == _tea[1].Id);
        tea.EntityName.Should().Be(withFlag ? "Tea" : $"Tea ({Place(locale, 2, 2)})");
        tea.EntityDetail.Should().Be(withFlag ? Place(locale, 2, 2) : null);
        items.Single(item => item.EntityId == _walk.Id).Should().BeEquivalentTo(
            delete.Items!.Single(item => item.EntityId == _walk.Id));
        var created = items.Single(item => item.EntityId is null);
        created.Should().BeEquivalentTo(create.Items!.Single());
        created.EntityName.Should().Be("Water");
        written.AiMessage.Should().Be("Review.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Detail_IsNotWrittenForARecordThatNoLongerExists(bool withFlag)
    {
        var client = Client("en", withFlag);
        var preview = await PreviewAsync("delete_habit", new { habit_id = _water[4].Id }, client);
        _db.Habits.Remove(await _db.Habits.SingleAsync(habit => habit.Id == _water[4].Id));
        await _db.SaveChangesAsync();

        var written = await _writer.WriteAsync(UserId, new ChatResponse("Review.", [], PendingOperations: [preview]), client, null, default);

        written.PendingOperations!.Single().Items.Should().BeEquivalentTo(preview.Items);
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task ChangeSet_EachMemberItemGetsItsDetailAndKeepsItsItemId(string locale, bool withFlag)
    {
        var client = Client(locale, withFlag);
        var preview = await PreviewAsync(AccountChangeSet.OperationName, ChangeSet(), client);

        var written = await _writer.WriteAsync(UserId, new ChatResponse("Review.", [], PendingOperations: [preview],
            Parts: [new ChatTextPart("Review.")]), client, null, default);

        var items = written.PendingOperations!.Single().Items!;
        items.Select(item => item.ItemId).Should().Equal(preview.Items!.Select(item => item.ItemId));
        items.Select(item => item.StateFingerprint).Should().Equal(preview.Items!.Select(item => item.StateFingerprint));
        var details = _water.Select((habit, index) => (habit.Id,
            Detail: $"{(index < 4 ? "Evening" : "Morning")}, {Place(locale, index + 1, 6)}")).ToDictionary(pair => pair.Id, pair => pair.Detail);
        foreach (var item in items)
        {
            item.EntityName.Should().Be(withFlag ? "Water" : $"Water ({details[item.EntityId!.Value]})");
            item.EntityDetail.Should().Be(withFlag ? details[item.EntityId!.Value] : null);
        }
        var texts = written.Parts!.OfType<ChatTextPart>().Select(part => part.Text).ToArray();
        if (withFlag)
            texts.Should().Equal("Review.");
        else
            texts.Should().Equal("Review.", string.Join("\n", items
                .Where(item => _water[2..6].Any(habit => habit.Id == item.EntityId))
                .Select(item => $"- Water ({details[item.EntityId!.Value]}) → Agua")));
        items.Count(item => item.Fields.Any(field => field.Field == "title")).Should().Be(4, "the lines follow the item order");
    }

    /// <summary>A change set over the six "Water" habits: two deletes, three renames and one rename of the last.</summary>
    private object ChangeSet() => new
    {
        operations = new[]
        {
            new AccountChangeSet.Member(Guid.NewGuid(), "bulk_delete_habits", JsonSerializer.SerializeToElement(
                new { filter = new { habit_ids = _water[..2].Select(habit => habit.Id) } }), "member-preview"),
            new AccountChangeSet.Member(Guid.NewGuid(), "bulk_update_habits", JsonSerializer.SerializeToElement(
                new { filter = new { habit_ids = _water[2..5].Select(habit => habit.Id) }, updates = new { title = "Agua" } }), "member-preview"),
            new AccountChangeSet.Member(Guid.NewGuid(), "update_habit", JsonSerializer.SerializeToElement(
                new { habit_id = _water[5].Id, title = "Agua" }), "member-preview")
        }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fingerprint_IsEqualForAClientWithTheFlagWithoutTheFlagAndWithNoClientContext(bool changeSet)
    {
        var (operationId, arguments) = changeSet ? (AccountChangeSet.OperationName, ChangeSet()) : Row("rename");
        var fingerprints = new List<string?>();
        var stored = new List<string>();
        foreach (var client in new[] { Client("en", true), Client("en", false), Client("pt-BR", true), null })
        {
            var preview = await PreviewAsync(operationId, arguments, client);
            fingerprints.Add(preview.PreviewFingerprint);
            stored.Add(PreviewHashView.Serialize(preview.Items!));
            preview.Items.Should().OnlyContain(item => item.EntityDetail == null && item.EntityName == "Water",
                "the previewer builds plain items, so each stored preview and each hash holds no detail");
        }

        fingerprints.Distinct().Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        stored.Should().OnlyContain(json => !json.Contains("EntityDetail", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Detail_ReadsNoHabitWhenNoOpenPreviewItemNamesARecord()
    {
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var users = Substitute.For<IGenericRepository<User>>();
        var writer = new SameTitleDetailWriter(habits, users);
        var create = await PreviewAsync("bulk_create_habits",
            new { habits = new[] { new { title = "Water", frequency_unit = "Day", frequency_quantity = 1 } } }, null);
        var response = new ChatResponse("Review.", [], PendingOperations: [create]);

        (await writer.WriteAsync(UserId, response, null, null, default)).Should().BeSameAs(response);
        (await writer.WriteAsync(UserId, new ChatResponse("Hello.", []), null, null, default)).PendingOperations.Should().BeNull();
        (await writer.WriteAsync(UserId, create, null, default)).Should().BeSameAs(create);

        await habits.DidNotReceiveWithAnyArgs().FindAsync(default(Expression<Func<Habit, bool>>)!, default(CancellationToken));
        await users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    public void Dispose() => _db.Dispose();
}
