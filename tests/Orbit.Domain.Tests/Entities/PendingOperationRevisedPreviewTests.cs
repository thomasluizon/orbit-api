using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public sealed class PendingOperationRevisedPreviewTests
{
    private const string Arguments = """{"title":"Read"}""";
    private const string RevisedArguments = """{"title":"Read more"}""";
    private const string Preview = """{"changes":[],"changeTargetCount":1,"items":[{"itemId":"0","entityName":"Read more"}],"previewFingerprint":"revised"}""";

    [Fact]
    public void RecordRevisedPreview_CurrentFingerprint_KeepsThePreview()
    {
        var state = Revised();

        state.RecordRevisedPreview("revised", Preview, DateTime.UtcNow).Should().BeTrue();

        state.RevisedPreviewJson.Should().Be(Preview);
    }

    [Fact]
    public void RecordRevisedPreview_OtherFingerprint_IsRejected()
    {
        var state = Revised();

        state.RecordRevisedPreview("bound", Preview, DateTime.UtcNow).Should().BeFalse();

        state.RevisedPreviewJson.Should().BeNull();
    }

    [Fact]
    public void RecordRevisedPreview_UnboundOperation_IsRejected() =>
        Proposal().RecordRevisedPreview("revised", Preview, DateTime.UtcNow).Should().BeFalse();

    [Fact]
    public void RecordRevisedPreview_ConfirmedOrConsumedOperation_IsRejected()
    {
        var confirmed = Revised();
        confirmed.SetConfirmationTokenHash("hash");
        var consumed = Revised();
        consumed.Cancel(DateTime.UtcNow).Should().BeTrue();

        confirmed.RecordRevisedPreview("revised", Preview, DateTime.UtcNow).Should().BeFalse();
        consumed.RecordRevisedPreview("revised", Preview, DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void RecordRevisedPreview_ExpiredOperation_IsRejected()
    {
        var state = Revised();

        state.RecordRevisedPreview("revised", Preview, state.ExpiresAtUtc).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    public void RecordRevisedPreview_NonObjectJson_IsRejected(string json) =>
        Revised().RecordRevisedPreview("revised", json, DateTime.UtcNow).Should().BeFalse();

    [Fact]
    public void RecordRevisedPreview_OverBudget_IsRejected()
    {
        var json = JsonSerializer.Serialize(new { summary = new string('a', PendingAgentOperationState.MaxRevisedPreviewJsonLength) });

        Revised().RecordRevisedPreview("revised", json, DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Revise_NewPreviewFingerprint_ClearsTheStoredPreview()
    {
        var state = Revised();
        state.RecordRevisedPreview("revised", Preview, DateTime.UtcNow).Should().BeTrue();

        state.Revise(Arguments, AgentOperationFingerprint.Compute("create_habit", Arguments), "again", DateTime.UtcNow).Should().BeTrue();

        state.RevisedPreviewJson.Should().BeNull();
    }

    [Fact]
    public void Revise_SamePreviewFingerprint_KeepsTheStoredPreview()
    {
        var state = Revised();
        state.RecordRevisedPreview("revised", Preview, DateTime.UtcNow).Should().BeTrue();

        state.Revise(RevisedArguments, state.OperationFingerprint, "revised", DateTime.UtcNow).Should().BeTrue();

        state.RevisedPreviewJson.Should().Be(Preview);
    }

    [Fact]
    public void PreviewOutcome_StoredWithoutRevisedPreview_LoadsAndWritesTheSameJson()
    {
        var id = Guid.NewGuid();
        var stored = $$$"""[{"PendingOperationId":"{{{id}}}","Outcome":"approved","Arguments":{"title":"Read"},"Revision":3,"Execution":{"status":"Succeeded"}}]""";

        var outcomes = JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(stored)!;

        outcomes.Single().RevisedPreview.Should().BeNull();
        JsonSerializer.Serialize(outcomes).Should().Be(stored);
    }

    [Fact]
    public void PreviewOutcome_WithRevisedPreview_WritesItAfterTheExistingFields()
    {
        var outcome = new ConversationPreviewOutcome(Guid.NewGuid(), "approved", JsonSerializer.SerializeToElement(new { title = "Read more" }),
            3, JsonSerializer.SerializeToElement(new { status = "Succeeded" }), JsonSerializer.Deserialize<JsonElement>(Preview));

        var json = JsonSerializer.SerializeToElement(outcome);

        json.EnumerateObject().Select(property => property.Name).Should()
            .Equal("PendingOperationId", "Outcome", "Arguments", "Revision", "Execution", "RevisedPreview");
        json.GetProperty("RevisedPreview").GetProperty("items")[0].GetProperty("entityName").GetString().Should().Be("Read more");
    }

    private static PendingAgentOperationState Revised()
    {
        var state = Proposal();
        state.Revise(Arguments, state.OperationFingerprint, "bound", DateTime.UtcNow).Should().BeTrue();
        state.Revise(RevisedArguments, AgentOperationFingerprint.Compute("create_habit", RevisedArguments), "revised", DateTime.UtcNow)
            .Should().BeTrue();
        return state;
    }

    private static PendingAgentOperationState Proposal() => PendingAgentOperationState.Create(new()
    {
        UserId = Guid.NewGuid(),
        Capability = new AgentCapability("habits.write", "Tasks", "Tasks", "habits", "write",
            AgentRiskClass.Low, IsMutation: true, IsPhaseOneReadOnly: false, AgentConfirmationRequirement.FreshConfirmation),
        OperationId = "create_habit",
        ArgumentsJson = Arguments,
        Summary = "Read",
        OperationFingerprint = AgentOperationFingerprint.Compute("create_habit", Arguments),
        Surface = AgentExecutionSurface.Chat,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
    });
}
