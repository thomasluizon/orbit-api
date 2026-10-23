using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Models;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Chat;

public sealed class ChatResponseNegotiationTests
{
    [Theory]
    [InlineData("choice", false)]
    [InlineData("pickList", false)]
    [InlineData("choice", true)]
    [InlineData("pickList", true)]
    public void Replay_ClarificationsKeepTheirQuestionWhenCapabilityIsWithdrawn(string kind, bool disabled)
    {
        var issued = IssuedAstraBlock.Create(Guid.NewGuid(), kind == "choice"
            ? new("choice", "Which time?", [new("morning", "Morning"), new("evening", "Evening")], [])
            : new("pickList", "Choose habits", [], [new("read", "Read", JsonSerializer.SerializeToElement(new { title = "Read" }))],
                MultiSelect: true, Action: "create_habits"));
        var definition = issued.ReadDefinition();
        object block = kind == "choice"
            ? new AstraChoiceBlock(issued.Id, definition.Title, definition.Options, definition.MultiSelect, definition.AllowFreeText, issued.ExpiresAtUtc)
            : new AstraPickListBlock(issued.Id, definition.Title, definition.Items.Select(item => new AstraPickListItem(item.Id, item.Label, item.Detail)).ToArray(),
                definition.Action!, issued.ExpiresAtUtc);
        var response = Replay(new(null, [], Parts: [new ChatBlockPart(kind, block)]));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: [kind]);

        var supported = ChatResponseNegotiation.ForClient(response, client, []);
        supported.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Kind.Should().Be(kind);
        supported.AiMessage.Should().BeNull();
        var fallback = ChatResponseNegotiation.ForClient(response, disabled ? client : client with { PartKinds = [] },
            disabled ? [FeatureFlagKeys.AstraAskBlocksDisabled] : []);
        fallback.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(definition.Title);
        fallback.AiMessage.Should().Be(definition.Title);
        fallback.Actions.Should().BeEmpty();
        fallback.PendingOperations.Should().BeNull();
    }

    [Theory]
    [InlineData("unknown", "{}")]
    [InlineData("habitList", "42")]
    [InlineData("habitList", "null")]
    public void Replay_UnknownOrMalformedBlockReportsUnavailable(string kind, string json)
    {
        var response = new ChatResponse(null, [], Parts: [new ChatBlockPart(kind, JsonSerializer.Deserialize<JsonElement>(json))]);
        var result = ChatResponseNegotiation.ForClient(response,
            new(SupportsParts: true, PartKinds: [kind], SupportsHabitListCard: true), []);
        result.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text
            .Should().Be("This response is unavailable on this client.");
        result.AiMessage.Should().Be("This response is unavailable on this client.");
    }

    [Theory]
    [InlineData("parts")]
    [InlineData("followups")]
    [InlineData("context")]
    [InlineData("legacy")]
    public void Replay_RespectsIndependentCompositionAndFollowUpCapabilities(string mode)
    {
        var response = Replay(new(null, [], Parts: [new ChatBlockPart("habitList", new HabitListCard("all", []))],
            FollowUps: ["Show habits?"], FollowUpContext: [new("Habits", "Show habits?", target: "habits")]));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true,
            SupportsFollowUps: mode != "legacy", SupportsFollowUpContext: mode != "context");
        var flags = mode switch
        {
            "parts" => new[] { FeatureFlagKeys.AstraPartsDisabled },
            "followups" => [FeatureFlagKeys.AstraFollowUpsDisabled],
            _ => Array.Empty<string>()
        };
        var result = ChatResponseNegotiation.ForClient(response, client, flags);
        if (mode == "parts")
        {
            result.Parts.Should().BeNull();
            result.AiMessage.Should().Be("all\n0");
        }
        else result.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>();
        if (mode is "followups" or "legacy") result.FollowUps.Should().BeNull();
        else result.FollowUps.Should().Equal("Show habits?");
        if (mode is "followups" or "legacy" or "context") result.FollowUpContext.Should().BeNull();
        else result.FollowUpContext.Should().ContainSingle().Which.Question.Should().Be("Show habits?");
    }

    [Theory]
    [InlineData("habitList")]
    [InlineData("recordList")]
    public void Replay_SubtypeRequiresItsOwnCapability(string kind)
    {
        object block = kind == "habitList" ? new HabitListCard("date", [], Date: new(2026, 4, 3))
            : new RecordListCard("habitHistory", 0, []);
        var response = Replay(new(null, [], Parts: [new ChatBlockPart(kind, block)]));
        var client = new AgentClientContext(SupportsParts: true, PartKinds: [kind], SupportsHabitListCard: true,
            SupportsRecordListCard: true);
        var result = ChatResponseNegotiation.ForClient(response, client, []);
        result.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>();
        result.AiMessage.Should().Contain(kind == "habitList" ? "2026-04-03" : "0 records");
        ChatResponseNegotiation.ForClient(response, client with
        { SupportsDatedHabitListCard = true, SupportsHabitHistoryCard = true }, []).Parts
            .Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>();
    }

    [Fact]
    public void Negotiation_PreservesLegacyAndTextOnlyResponses()
    {
        var legacy = new ChatResponse("Readable answer", []);
        ChatResponseNegotiation.ForClient(legacy, null, []).Should().BeSameAs(legacy);
        var text = legacy with { Parts = [new ChatTextPart("Readable answer")] };
        var result = ChatResponseNegotiation.ForClient(text, new(SupportsParts: true), []);
        result.AiMessage.Should().Be("Readable answer");
        result.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be("Readable answer");
    }

    private static ChatResponse Replay(ChatResponse response) => JsonSerializer.Deserialize<ChatResponse>(
        JsonSerializer.Serialize(response, ChatStreamEvent.SerializerOptions), ChatStreamEvent.SerializerOptions)!;
}
