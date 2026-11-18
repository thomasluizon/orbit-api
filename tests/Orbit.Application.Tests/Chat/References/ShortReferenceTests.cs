using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.References;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat.References;

/// <summary>The reference rules, the reference book and the normalizer of the chat boundary (thomasluizon/orbit-tickets#2154).</summary>
public class ShortReferenceTests
{
    private static readonly ReferenceRecord Water = new(Guid.Parse("dab2ad98-e891-4a8f-a8c6-91d0c3b2a1f4"), "Water");
    private static readonly ReferenceRecord Twin = new(Guid.Parse("dab2ad98-77aa-4b1c-9d2e-3f4a5b6c7d8e"), "Water");
    private static readonly ReferenceRecord Reading = new(Guid.Parse("4c1e7f20-3b5a-4d6c-8e9f-0a1b2c3d4e5f"), "Reading");
    private static readonly ReferenceRecord Health = new(Guid.Parse("9e8d7c6b-5a4f-4e3d-a2c1-b0a9f8e7d6c5"), "Health");
    private static readonly ReferenceRecord Fitness = new(Guid.Parse("17263544-5362-4718-9a0b-c1d2e3f4a5b6"), "Fitness");

    private static string CutShort(Guid id)
    {
        var text = id.ToString();
        return $"{text[..8]}-{text[9..12]}-{text[14..18]}-{text[19..23]}-{text[24..27]}";
    }

    private static ReferenceBook Book(Dictionary<ReferenceRecordType, ReferenceRecord[]> records, List<ReferenceRecordType>? loads = null) =>
        new((type, _) =>
        {
            loads?.Add(type);
            return Task.FromResult<IReadOnlyList<ReferenceRecord>>(records.GetValueOrDefault(type) ?? []);
        });

    private static ToolCallReferenceNormalizer Normalizer(ReferenceBook book, params PreviewItemReference[] items) =>
        new(book, (_, _) => Task.FromResult<IReadOnlyList<PreviewItemReference>>(items));

    private static AiToolCall Call(string name, object args) => new(name, "call", JsonSerializer.SerializeToElement(args));

    [Fact]
    public void Resolve_Run4CutIdResolvesByItsFirstEightDigits()
    {
        ReferenceCodes.Resolve([Water, Reading], CutShort(Water.Id)).Record.Should().Be(Water);
        ReferenceCodes.Resolve([Water, Reading], "dab2ad98").Record.Should().Be(Water);
    }

    [Fact]
    public void Resolve_CollisionUsesTheNextDigitsAndWithoutThemNamesBoth()
    {
        ReferenceCodes.Resolve([Water, Twin], ReferenceCodes.Hex(Water.Id)[..12]).Record.Should().Be(Water);
        ReferenceCodes.Resolve([Water, Twin], ReferenceCodes.Hex(Twin.Id)[..12]).Record.Should().Be(Twin);
        var unknown = ReferenceCodes.Resolve([Water, Twin, Reading], "dab2ad98");
        unknown.IsResolved.Should().BeFalse();
        unknown.Closest.Take(2).Should().BeEquivalentTo([Water, Twin]);
        ReferenceCodes.Lengths([Water, Twin, Reading]).Should().BeEquivalentTo(new Dictionary<Guid, int>
        {
            [Water.Id] = 12,
            [Twin.Id] = 12,
            [Reading.Id] = 8
        });
    }

    [Fact]
    public void Resolve_FullGuidResolvesOnlyAsItself()
    {
        ReferenceCodes.Resolve([Water], Water.Id.ToString()).Record.Should().Be(Water);
        var foreign = Guid.Parse("dab2ad98-0000-4000-8000-000000000000");
        ReferenceCodes.Resolve([Water], foreign.ToString()).IsResolved.Should().BeFalse("a full GUID of no record of the person never resolves");
    }

    [Fact]
    public async Task Book_WritesReferencesForListedRecordsAndKeepsOtherGuids()
    {
        var other = Guid.NewGuid();
        var loads = new List<ReferenceRecordType>();
        using var book = Book(new() { [ReferenceRecordType.Goal] = [Fitness] }, loads);
        book.Seed(ReferenceRecordType.Habit, [Water, Twin, Reading]);

        var text = await book.WriteAsync($"{Water.Id} {Twin.Id} {Reading.Id} 1/{Reading.Id} {Fitness.Id} {other}", default);

        text.Should().Be($"dab2ad98e891 dab2ad9877aa 4c1e7f20 1/4c1e7f20 17263544 {other}");
        loads.Should().NotContain(ReferenceRecordType.Habit, "the turn context already read the habits");
        loads.Should().Contain(ReferenceRecordType.Goal);
        loads.Clear();
        (await book.WriteAsync(other.ToString(), default)).Should().Be(other.ToString());
        loads.Should().BeEmpty("a GUID of no listed type is known after the first read");
    }

    [Fact]
    public async Task Normalizer_ResolvesAReferenceAndACutIdOfOneArgument()
    {
        using var book = Book([]);
        book.Seed(ReferenceRecordType.Habit, [Water, Reading]);

        var result = await Normalizer(book).NormalizeAsync(Call("bulk_update_habits", new
        {
            filter = new { habit_ids = new[] { "4c1e7f20", CutShort(Water.Id) } },
            updates = new { title = "Water - 1L" }
        }), default);

        result.IsResolved.Should().BeTrue();
        result.Call.Args.GetProperty("filter").GetProperty("habit_ids").EnumerateArray().Select(id => id.GetGuid())
            .Should().Equal(Reading.Id, Water.Id);
        result.Call.Args.GetProperty("updates").GetProperty("title").GetString().Should().Be("Water - 1L");
    }

    [Fact]
    public async Task Normalizer_ResolvesTheOperationArgumentsOfEditsJsonAndOfAPickListItem()
    {
        using var book = Book(new() { [ReferenceRecordType.Goal] = [Fitness] });
        book.Seed(ReferenceRecordType.Habit, [Water, Reading]);

        var revision = await Normalizer(book).NormalizeAsync(Call("revise_pending_changes", new
        {
            pending_operation_id = (string?)null,
            items = new[] { new { item_id = "0", edits_json = JsonSerializer.Serialize(new { parent_habit_id = "4c1e7f20", goal_ids = new[] { "17263544" } }) } }
        }), default);
        var ask = await Normalizer(book).NormalizeAsync(Call("ask", new
        {
            input = new
            {
                form = "pickList",
                items = new object[]
                {
                    new { id = "a", label = "Stretch", arguments = JsonSerializer.Serialize(new { title = "Stretch", parent_habit_id = "4c1e7f20" }) },
                    new { id = "b", label = "Water", arguments = new { habit_id = "dab2ad98", date = "2026-04-03" } }
                }
            }
        }), default);

        revision.IsResolved.Should().BeTrue();
        using var edits = JsonDocument.Parse(revision.Call.Args.GetProperty("items")[0].GetProperty("edits_json").GetString()!);
        edits.RootElement.GetProperty("parent_habit_id").GetGuid().Should().Be(Reading.Id);
        edits.RootElement.GetProperty("goal_ids")[0].GetGuid().Should().Be(Fitness.Id);
        ask.IsResolved.Should().BeTrue();
        var items = ask.Call.Args.GetProperty("input").GetProperty("items");
        using var created = JsonDocument.Parse(items[0].GetProperty("arguments").GetString()!);
        created.RootElement.GetProperty("parent_habit_id").GetGuid().Should().Be(Reading.Id);
        items[1].GetProperty("arguments").GetProperty("habit_id").GetGuid().Should().Be(Water.Id);
        items[0].GetProperty("id").GetString().Should().Be("a");
    }

    [Fact]
    public async Task Normalizer_KeepsTheMemberIndexOfAChangeSetItemAndResolvesItsGuidPart()
    {
        using var book = Book(new() { [ReferenceRecordType.PendingOperation] = [new(Health.Id, "account_change_set")] });
        var items = new PreviewItemReference[]
        {
            new($"0/{Water.Id}", "Water"), new($"1/{Reading.Id}", "Reading"), new($"1/{Water.Id}:2026-04-03", "Water"), new("2/0", "Stretch")
        };

        var result = await Normalizer(book, items).NormalizeAsync(Call("revise_pending_changes", new
        {
            pending_operation_id = "9e8d7c6b",
            items = new[] { new { item_id = "1/4c1e7f20", edits_json = "{}" } },
            remove_item_ids = new[] { "0/dab2ad98", "1/dab2ad98:2026-04-03", "2/0" }
        }), default);

        result.IsResolved.Should().BeTrue();
        result.Call.Args.GetProperty("pending_operation_id").GetGuid().Should().Be(Health.Id);
        result.Call.Args.GetProperty("items")[0].GetProperty("item_id").GetString().Should().Be($"1/{Reading.Id}");
        result.Call.Args.GetProperty("remove_item_ids").EnumerateArray().Select(id => id.GetString())
            .Should().Equal($"0/{Water.Id}", $"1/{Water.Id}:2026-04-03", "2/0");
    }

    [Fact]
    public async Task Normalizer_TurnsDownAReferenceOfAnotherTypeAndAForeignPrefix()
    {
        using var book = Book(new() { [ReferenceRecordType.Goal] = [Fitness] });
        book.Seed(ReferenceRecordType.Habit, [Water, Reading]);

        var habitAsGoal = await Normalizer(book).NormalizeAsync(Call("update_goal", new { goal_id = "dab2ad98" }), default);
        var foreign = await Normalizer(book).NormalizeAsync(Call("update_habit", new { habit_id = "0f0f0f0f" }), default);

        habitAsGoal.IsResolved.Should().BeFalse("a habit reference names no goal");
        habitAsGoal.Error().Should().StartWith("unknown_reference: goal_id \"dab2ad98\" names no goal of this person.")
            .And.Contain($"\"{Fitness.Id}\" (Fitness)").And.NotContain("Water");
        foreign.IsResolved.Should().BeFalse();
        foreign.Error().Should().Contain("Water").And.Contain("Reading").And.NotContain("0f0f0f0f-");
        foreign.Call.Args.GetProperty("habit_id").GetString().Should().Be("0f0f0f0f", "a failed call keeps the arguments that the model sent");
    }

    [Fact]
    public async Task Normalizer_PassesTheArgumentsWithTheLabelNoReferenceAndTextThatIsNoReference()
    {
        using var book = Book([]);
        book.Seed(ReferenceRecordType.Habit, [Water]);
        var calendar = Guid.NewGuid().ToString();
        var call = Call("manage_calendar_sync", new
        {
            suggestion_id = calendar,
            calendar_ids = new[] { "primary", "dab2ad98-e891-4a8f-a8c6-91d0c3b2a1f4@group.calendar.google.com" },
            draft_call_id = "dab2ad98",
            habit_ids = new[] { "água", "" }
        });

        var result = await Normalizer(book).NormalizeAsync(call, default);

        result.IsResolved.Should().BeTrue();
        result.Call.Should().BeSameAs(call, "nothing in the call names a listed record by reference");
    }
}
