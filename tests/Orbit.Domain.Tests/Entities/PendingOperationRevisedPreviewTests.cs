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
    public void Revise_WithPreview_KeepsThePreviewWithTheRevision()
    {
        var state = Revised(Preview);

        state.RevisedPreviewJson.Should().Be(Preview);
        state.ArgumentsJson.Should().Be(RevisedArguments);
        state.PreviewFingerprint.Should().Be("revised");
    }

    [Fact]
    public void Revise_WithoutPreview_StoresNoPreview() =>
        Revised().RevisedPreviewJson.Should().BeNull();

    [Fact]
    public void Revise_ExpiredOperation_StoresNoPreview()
    {
        var state = Proposal();

        state.Revise(RevisedArguments, AgentOperationFingerprint.Compute("create_habit", RevisedArguments), "revised",
            state.ExpiresAtUtc, Preview).Should().BeFalse();

        state.RevisedPreviewJson.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    public void Revise_NonObjectPreview_UsesTheFingerprintRule(string json)
    {
        var state = Revised(Preview);

        state.Revise(RevisedArguments, state.OperationFingerprint, "revised", DateTime.UtcNow, json).Should().BeTrue();
        state.RevisedPreviewJson.Should().Be(Preview);
        state.Revise(Arguments, AgentOperationFingerprint.Compute("create_habit", Arguments), "again", DateTime.UtcNow, json)
            .Should().BeTrue();
        state.RevisedPreviewJson.Should().BeNull();
    }

    [Fact]
    public void Revise_OverBudgetPreview_UsesTheFingerprintRule()
    {
        var json = JsonSerializer.Serialize(new { summary = new string('a', PendingAgentOperationState.MaxRevisedPreviewJsonLength) });
        var state = Revised(Preview);

        state.Revise(RevisedArguments, state.OperationFingerprint, "revised", DateTime.UtcNow, json).Should().BeTrue();
        state.RevisedPreviewJson.Should().Be(Preview);
        state.Revise(Arguments, AgentOperationFingerprint.Compute("create_habit", Arguments), "again", DateTime.UtcNow, json)
            .Should().BeTrue();
        state.RevisedPreviewJson.Should().BeNull();
    }

    [Fact]
    public void Revise_NewPreviewFingerprint_ClearsTheStoredPreview()
    {
        var state = Revised(Preview);

        state.Revise(Arguments, AgentOperationFingerprint.Compute("create_habit", Arguments), "again", DateTime.UtcNow).Should().BeTrue();

        state.RevisedPreviewJson.Should().BeNull();
    }

    [Fact]
    public void Revise_SamePreviewFingerprint_KeepsTheStoredPreview()
    {
        var state = Revised(Preview);

        state.Revise(RevisedArguments, state.OperationFingerprint, "revised", DateTime.UtcNow).Should().BeTrue();

        state.RevisedPreviewJson.Should().Be(Preview);
    }

    [Fact]
    public void Revise_NewPreview_ReplacesTheStoredPreview()
    {
        const string again = """{"changes":[],"changeTargetCount":1,"items":[{"itemId":"0","entityName":"Read"}],"previewFingerprint":"again"}""";
        var state = Revised(Preview);

        state.Revise(Arguments, AgentOperationFingerprint.Compute("create_habit", Arguments), "again", DateTime.UtcNow, again)
            .Should().BeTrue();

        state.RevisedPreviewJson.Should().Be(again);
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

    private static PendingAgentOperationState Revised(string? preview = null)
    {
        var state = Proposal();
        state.Revise(Arguments, state.OperationFingerprint, "bound", DateTime.UtcNow).Should().BeTrue();
        state.Revise(RevisedArguments, AgentOperationFingerprint.Compute("create_habit", RevisedArguments), "revised", DateTime.UtcNow,
            preview).Should().BeTrue();
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
