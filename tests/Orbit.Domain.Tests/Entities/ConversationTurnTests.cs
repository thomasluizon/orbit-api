using FluentAssertions;
using System.Text.Json;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class ConversationTurnTests
{
    [Theory]
    [InlineData("assistant", "{\"parts\":[]}", true)]
    [InlineData("assistant", "[]", false)]
    [InlineData("assistant", "invalid", false)]
    [InlineData("user", "{}", false)]
    public void Create_ResponseRequiresAssistantAndJsonObject(string role, string json, bool accepted)
    {
        var result = ConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), role, DateTime.UtcNow, null, "hello", "[]", json);
        result.IsSuccess.Should().Be(accepted);
        if (accepted) result.Value.ResponseJson.Should().Be(json);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    public void Create_SupportedRoles_StoresMetadata(string role)
    {
        var userId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var createdAtUtc = DateTime.UtcNow;
        var result = ConversationTurn.Create(userId, turnId, role, createdAtUtc, "model", "content", "[]");
        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(userId);
        result.Value.TurnId.Should().Be(turnId);
        result.Value.CreatedAtUtc.Should().Be(createdAtUtc);
        result.Value.Role.Should().Be(role);
    }

    [Theory]
    [InlineData("system", "hello", "[]")]
    [InlineData("user", "", "[]")]
    [InlineData("assistant", "hello", "{}")]
    [InlineData("assistant", "hello", "invalid")]
    public void Create_InvalidData_IsRejected(string role, string content, string tools) =>
        ConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), role, DateTime.UtcNow, null, content, tools)
            .IsFailure.Should().BeTrue();

    [Fact]
    public void Create_InvalidIdentifiersTimestampAndLengths_AreRejected()
    {
        var user = Guid.NewGuid();
        var turn = Guid.NewGuid();
        ConversationTurn.Create(Guid.Empty, turn, "user", DateTime.UtcNow, null, "hello", "[]").IsFailure.Should().BeTrue();
        ConversationTurn.Create(user, Guid.Empty, "user", DateTime.UtcNow, null, "hello", "[]").IsFailure.Should().BeTrue();
        ConversationTurn.Create(user, turn, "user", DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified), null, "hello", "[]").IsFailure.Should().BeTrue();
        ConversationTurn.Create(user, turn, "user", DateTime.UtcNow, new string('m', 201), "hello", "[]").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_LargeAssistantFields_PreservesFullDisplayData()
    {
        var text = new string('a', 100_000);
        var json = System.Text.Json.JsonSerializer.Serialize(new { text });
        var result = ConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), "assistant", DateTime.UtcNow,
            null, text, "[" + json + "]", json, previewOutcomesJson: "[" + json + "]");
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be(text);
        result.Value.ResponseJson.Should().Be(json);
        result.Value.ToolCallsJson.Should().Be("[" + json + "]");
    }

    [Fact]
    public void UpdatePreviewOutcome_LargeSnapshot_PreservesEveryProposal()
    {
        var arguments = JsonSerializer.SerializeToElement(new { text = new string('a', 7_000) });
        var previews = Enumerable.Range(0, 10).Select(_ => new ConversationPreviewOutcome(Guid.NewGuid(), "pending", arguments, 1)).ToArray();
        var json = JsonSerializer.Serialize(previews);
        json.Length.Should().BeGreaterThan(65_536);
        var turn = ConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), "assistant", DateTime.UtcNow, null,
            "Review these proposals", "[]", previewOutcomesJson: json).Value;

        turn.UpdatePreviewOutcome(previews[^1].PendingOperationId, "approved", arguments, 2);

        var updated = JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(turn.PreviewOutcomesJson)!;
        updated.Should().HaveCount(10);
        updated[^1].Outcome.Should().Be("approved");
        updated.Take(9).Should().OnlyContain(preview => preview.Outcome == "pending");
    }
    [Theory]
    [InlineData(null, 1, false)]
    [InlineData("11111111-1111-4111-8111-111111111111", 0, false)]
    [InlineData("11111111-1111-4111-8111-111111111111", 1, true)]
    public void Create_ConversationRequiresOrderedRows(string? id, long order, bool accepted)
    {
        var result = ConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), "user", DateTime.UtcNow, null,
            "Hello", "[]", conversationId: id is null ? null : Guid.Parse(id), turnOrder: order);
        result.IsSuccess.Should().Be(accepted);
    }

    [Fact]
    public void Create_ImageMetadataMustBelongToAccountAndConversation()
    {
        var user = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var sha = new string('a', 64);
        var accepted = ConversationTurn.Create(user, Guid.NewGuid(), "user", DateTime.UtcNow, null, "Hello", "[]",
            conversationId: conversation, turnOrder: 1, imageObjectKey: $"{user}/conversations/{conversation}/image.png",
            imageContentType: "image/png", imageSha256: sha);
        accepted.IsSuccess.Should().BeTrue();
        var rejected = ConversationTurn.Create(user, Guid.NewGuid(), "user", DateTime.UtcNow, null, "Hello", "[]",
            conversationId: conversation, turnOrder: 1, imageObjectKey: $"{Guid.NewGuid()}/conversations/{conversation}/image.png",
            imageContentType: "image/png", imageSha256: sha);
        rejected.IsFailure.Should().BeTrue();
    }

}
