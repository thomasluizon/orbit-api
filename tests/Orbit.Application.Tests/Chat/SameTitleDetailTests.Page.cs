using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed partial class SameTitleDetailTests
{
    private static readonly string[] Outcomes = ["pending", "approved", "rejected", "edited", "expired"];

    /// <summary>
    /// A page with one assistant turn for each stored outcome. Each turn holds a preview that renames the fifth
    /// "Water" and deletes the sixth.
    /// </summary>
    private async Task<ConversationPage> PageAsync(bool parts)
    {
        var turns = new List<ConversationDisplayTurn>();
        foreach (var (outcome, index) in Outcomes.Select((outcome, index) => (outcome, index)))
        {
            var preview = await PreviewAsync(AccountChangeSet.OperationName, new
            {
                operations = new[]
                {
                    new AccountChangeSet.Member(Guid.NewGuid(), "update_habit", JsonSerializer.SerializeToElement(
                        new { habit_id = _water[4].Id, title = "Agua" }), "member-preview"),
                    new AccountChangeSet.Member(Guid.NewGuid(), "delete_habit", JsonSerializer.SerializeToElement(
                        new { habit_id = _water[5].Id }), "member-preview")
                }
            }, null);
            var response = new ChatResponse("Review the changes.", [], PendingOperations: [preview],
                Parts: parts ? [new ChatTextPart("Review the changes.")] : null);
            var outcomes = new[]
            {
                new ConversationPreviewOutcome(preview.Id, outcome, JsonSerializer.SerializeToElement(new { }), outcome == "edited" ? 2 : 1)
            };
            turns.Add(new ConversationDisplayTurn(Guid.NewGuid(), index * 2, "user", DateTime.UtcNow, "Change my Water habits.", null, "[]", null));
            turns.Add(new ConversationDisplayTurn(Guid.NewGuid(), index * 2 + 1, "assistant", DateTime.UtcNow, "Review the changes.",
                JsonSerializer.Serialize(response, ChatStreamEvent.SerializerOptions), JsonSerializer.Serialize(outcomes), null));
        }
        return new ConversationPage(Guid.NewGuid(), turns, null);
    }

    public static TheoryData<string, bool?, bool> PageClients()
    {
        var clients = new TheoryData<string, bool?, bool>();
        foreach (var locale in new[] { "en", "pt-BR" })
            foreach (var flag in new bool?[] { true, false, null })
                foreach (var parts in new[] { true, false })
                    clients.Add(locale, flag, parts);
        return clients;
    }

    /// <summary>
    /// A null flag is a request with no <c>clientContext</c> parameter: the text follows the profile language.
    /// </summary>
    [Theory]
    [MemberData(nameof(PageClients))]
    public async Task Page_WritesTheDetailOnAPendingPreviewAndReturnsEachOtherStoredValueUnchanged(string locale, bool? flag, bool parts)
    {
        _user.SetLanguage(locale);
        await _db.SaveChangesAsync();
        var page = await PageAsync(parts);
        var client = flag is { } withFlag ? Client(locale, withFlag) : null;

        var written = await _writer.WriteAsync(UserId, page, client, default);

        written.ConversationId.Should().Be(page.ConversationId);
        foreach (var (turn, stored) in written.Turns.Zip(page.Turns))
        {
            var outcome = stored.Role == "assistant"
                ? JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(stored.PreviewOutcomesJson)!.Single().Outcome : null;
            if (outcome != "pending")
            {
                turn.Should().Be(stored, $"the page returns a turn with the outcome {outcome ?? "none"} as it is stored");
                continue;
            }
            turn.Should().Be(stored with { ResponseJson = turn.ResponseJson });
            var response = JsonNode.Parse(turn.ResponseJson!)!.AsObject();
            var plain = JsonNode.Parse(stored.ResponseJson!)!.AsObject();
            var items = response["pendingOperations"]![0]!["items"]!.AsArray();
            var rename = $"Morning, {Place(locale, 5, 6)}";
            var delete = $"Morning, {Place(locale, 6, 6)}";
            items.Select(item => item!["entityName"]!.GetValue<string>()).Should().Equal(
                flag == true ? ["Water", "Water"] : [$"Water ({rename})", $"Water ({delete})"]);
            items.Select(item => item!["entityDetail"]?.GetValue<string>()).Should().Equal(
                flag == true ? [rename, delete] : [null, null]);
            items[0]!["fields"]!.AsArray().Single(field => field!["field"]!.GetValue<string>() == "title")!["newValue"]!
                .GetValue<string>().Should().Be("Agua");
            var line = $"- Water ({rename}) → Agua";
            if (flag == true)
            {
                response["aiMessage"]!.GetValue<string>().Should().Be("Review the changes.");
                (response["parts"]?.AsArray().Count ?? 0).Should().Be(parts ? 1 : 0);
            }
            else if (parts)
            {
                response["aiMessage"]!.GetValue<string>().Should().Be("Review the changes.");
                response["parts"]!.AsArray().Select(part => part!["text"]!.GetValue<string>()).Should().Equal("Review the changes.", line);
                response["parts"]!.AsArray().Should().OnlyContain(part => part!["type"]!.GetValue<string>() == "text");
            }
            else
                response["aiMessage"]!.GetValue<string>().Should().Be("Review the changes.\n\n" + line);
            foreach (var node in new[] { response, plain })
            {
                node.Remove("aiMessage");
                node.Remove("parts");
                foreach (var item in node["pendingOperations"]![0]!["items"]!.AsArray())
                {
                    item!.AsObject().Remove("entityName");
                    item.AsObject().Remove("entityDetail");
                }
            }
            JsonNode.DeepEquals(response, plain).Should().BeTrue("the page changes no other stored value");
        }
    }

    [Fact]
    public async Task Page_ReadsNothingWhenNoPreviewIsPending()
    {
        var page = await PageAsync(parts: true);
        var closed = page with { Turns = [.. page.Turns.Where(turn => !turn.PreviewOutcomesJson.Contains("pending", StringComparison.Ordinal))] };
        var habits = Substitute.For<Orbit.Domain.Interfaces.IGenericRepository<Habit>>();
        var users = Substitute.For<Orbit.Domain.Interfaces.IGenericRepository<User>>();

        var written = await new SameTitleDetailWriter(habits, users).WriteAsync(UserId, closed, null, default);

        written.Should().BeSameAs(closed);
        habits.ReceivedCalls().Should().BeEmpty();
        users.ReceivedCalls().Should().BeEmpty();
    }
}
