using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// A change list that fails only on a refusal that a repair cannot fix (thomasluizon/orbit-tickets#2160): the habit limit,
/// or a plan, feature or policy denial. The turn ends with the explanation of that refusal, as the old path does, and
/// never with the "not prepared" answer that asks the person to send the request again.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private const string RefusedTool = "stub_refused_create";
    private const int HabitLimit = 3;

    /// <summary>A write tool that the executor refuses in these tests. It has no atomic support.</summary>
    private sealed class RefusedChangeTool : IAiTool
    {
        public string Name => RefusedTool;
        public string Description => "A change that the executor refuses.";
        public object GetParameterSchema() => new
        {
            type = "object",
            properties = new { note = new { type = "string" } },
            required = new[] { "note" }
        };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => Task.FromResult(new ToolResult(true));
    }

    private static string LimitReasonFor(string language)
    {
        ErrorCopy.TryResolve(ErrorCodes.HabitLimitReached, language == "pt-BR", [HabitLimit], out var reason).Should().BeTrue();
        return reason;
    }

    private static string CapacityNextStepFor(string language) => language == "pt-BR"
        ? "Você pode adicionar um sub-hábito a um hábito existente."
        : "You can add a sub-habit to an existing habit.";

    /// <summary>The executor refuses each call of <see cref="RefusedTool"/>, as the real executor refuses a create at the limit.</summary>
    private void RefuseWritesOf(string language, string refusal) =>
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == RefusedTool),
                Arg.Any<CancellationToken>())
            .Returns(_ => new AgentExecuteOperationResponse(refusal == "capacity"
                ? new AgentOperationResult(RefusedTool, RefusedTool, AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation,
                    AgentOperationStatus.Failed, PolicyReason: LimitReasonFor(language), Payload: new HabitCapacityFailure(LimitReasonFor(language), [HabitLimit]),
                    ErrorCode: ErrorCodes.HabitLimitReached, Access: "write")
                : new AgentOperationResult(RefusedTool, RefusedTool, AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation,
                    AgentOperationStatus.Denied, PolicyReason: "feature_requires_pro", Access: "write")));

    /// <summary>
    /// The list holds a change that the habit limit or a denial refuses, alone or beside a delete that would prepare. The
    /// repair round cannot fix the refusal, so the model explains it. The turn holds nothing, and its answer is the
    /// explanation: the capacity refusal with its next step, or the text of the model for a denial.
    /// </summary>
    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en", "capacity", false)]
    [InlineData(ChangeDeclarationShape.Kinds, "pt-BR", "capacity", false)]
    [InlineData(ChangeDeclarationShape.Wrapper, "en", "capacity", false)]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR", "capacity", false)]
    [InlineData(ChangeDeclarationShape.Kinds, "en", "capacity", true)]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR", "capacity", true)]
    [InlineData(ChangeDeclarationShape.Kinds, "en", "denial", false)]
    [InlineData(ChangeDeclarationShape.Kinds, "pt-BR", "denial", false)]
    [InlineData(ChangeDeclarationShape.Wrapper, "en", "denial", false)]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR", "denial", true)]
    public async Task ChangeList_ARefusalThatARepairCannotFixEndsWithItsExplanationAndNotTheNotPreparedAnswer(
        ChangeDeclarationShape shape, string language, string refusal, bool withSibling)
    {
        var run = await RunChangeListAsync(shape, language,
            children => ChangeList(shape, null, true,
                [.. withSibling ? new[] { DeleteChange("delete", children[4..]) } : [], ("refused", RefusedTool, new { note = "one more habit" })]),
            _ => [Text(language)],
            arrange: (_, _) => RefuseWritesOf(language, refusal),
            tools: [new RefusedChangeTool()]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.ToolResults.Should().ContainSingle("the list failed once and the model explained it");
        (await run.Store.GetAwaitingAsync(UserId)).Should().BeEmpty("a list with an error holds nothing, also the delete beside the refusal");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
        run.Result.Value.AiMessage.Should().Be(refusal == "capacity"
            ? LimitReasonFor(language) + " " + CapacityNextStepFor(language)
            : Text(language).TextMessage);
    }

    /// <summary>A second list that the limit refuses ends the turn with the capacity answer, not with an empty answer.</summary>
    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_ASecondListThatTheLimitRefusesEndsWithTheCapacityAnswer(ChangeDeclarationShape shape, string language)
    {
        var refused = () => ChangeList(shape, null, true, ("refused", RefusedTool, new { note = "one more habit" }));
        var run = await RunChangeListAsync(shape, language, _ => refused(), _ => [refused(), Text(language)],
            arrange: (_, _) => RefuseWritesOf(language, "capacity"),
            tools: [new RefusedChangeTool()]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        (await run.Store.GetAwaitingAsync(UserId)).Should().BeEmpty();
        run.Result.Value.AiMessage.Should().Be(LimitReasonFor(language) + " " + CapacityNextStepFor(language));
    }
}
