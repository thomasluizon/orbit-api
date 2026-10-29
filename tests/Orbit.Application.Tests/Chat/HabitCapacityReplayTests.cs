using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Grounding;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false, 2, 1, "root", false)]
    [InlineData("pt-BR", false, 2, 1, "root", false)]
    [InlineData("en", true, 2, 1, "root", false)]
    [InlineData("pt-BR", true, 2, 1, "root", false)]
    [InlineData("en", false, 3, 1, "root", false)]
    [InlineData("en", true, 3, 1, "root", false)]
    [InlineData("en", false, 3, 2, "root", false)]
    [InlineData("pt-BR", false, 3, 2, "root", false)]
    [InlineData("en", false, 4, 2, "root", false)]
    [InlineData("en", false, 2, 1, "root", true)]
    [InlineData("pt-BR", true, 2, 1, "root", true)]
    [InlineData("en", false, 2, 1, "sharedParent", false)]
    [InlineData("en", false, 2, 1, "itemParent", false)]
    [InlineData("en", false, 3, 1, "children", false)]
    [InlineData("en", false, 2, 1, "newParent", false)]
    [InlineData("en", false, 3, 1, "nestedRoot", false)]
    [InlineData("en", false, 3, 2, "mixed", false)]
    public async Task G8_CapturedCreateChecksCapacityBeforeHoldingPreview(string language, bool single, int limit, int count,
        string shape, bool pro)
    {
        var parents = SetupExistingParents(language);
        EnableComposition();
        if (pro) (await _userRepo.GetByIdAsync(UserId, default))!.GrantLifetimePro();
        var gate = new PayGateService(_habitRepo, _userRepo, _appConfig, _userDateService, _featureFlagService);
        _appConfig.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>()).Returns(limit);
        _habitRepo.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => parents.Count(call.Arg<Expression<Func<Habit, bool>>>().Compile()));
        var mediator = Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habitRepo, _tagRepo, _goalRepo,
            _userDateService, gate, _unitOfWork, users: _userRepo);
        IAiTool tool = single ? new CreateHabitTool(_userDateService, mediator) : new BulkCreateHabitsTool(mediator);
        var handler = CreateHandler(tool);
        var outcomes = new List<AgentExecuteOperationResponse>();
        var policy = UseCapacityReplayExecutor(tool, outcomes);
        var captured = CapturedCompositionPlan("g8-edge-free-limit-en-r1");
        var call = captured.ToolCalls!.Single();
        if (single)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(call.Args.GetProperty("habits")[0].GetRawText())!.AsObject();
            node["orbit_write_complete"] = true;
            call = call with { Name = tool.Name, Args = JsonSerializer.SerializeToElement(node) };
        }
        else if (count == 2)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(call.Args.GetRawText())!.AsObject();
            node["habits"]!.AsArray().Add(node["habits"]![0]!.DeepClone());
            node["habits"]![1]!["title"] = "Stretch after lunch";
            call = call with { Args = JsonSerializer.SerializeToElement(node) };
        }
        var shaped = System.Text.Json.Nodes.JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        var first = single ? shaped : shaped["habits"]![0]!.AsObject();
        if (shape == "sharedParent") shaped["parent_habit_id"] = parents[0].Id.ToString();
        if (shape is "itemParent" or "newParent" or "mixed") first["parent_habit_id"] = parents[0].Id.ToString();
        if (shape == "newParent") first["placement"] = "new_parent";
        if (shape is "children" or "nestedRoot")
            first["sub_habits"] = System.Text.Json.Nodes.JsonNode.Parse(shape == "children"
                ? """[{"title":"Child"},{"title":"Another child"}]"""
                : """[{"title":"Child","placement":"new_parent","frequency_unit":"Day"}]""");
        call = call with { Args = JsonSerializer.SerializeToElement(shaped) };
        SetupAiResponse(captured with { ToolCalls = [call] });
        ErrorCopy.TryResolve(ErrorCodes.HabitLimitReached, language == "pt-BR", [limit], out var expected).Should().BeTrue();
        ReplyToReplay(expected);

        var result = await handler.Handle(new(UserId, "Prepare the requested changes.",
            ClientContext: new(SupportsParts: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var operation = outcomes.Should().ContainSingle().Which.Operation;
        var roots = shape switch { "sharedParent" or "itemParent" => 0, "mixed" => 1, "nestedRoot" => 2, _ => count };
        if (parents.Length + roots > limit)
        {
            operation.Status.Should().Be(AgentOperationStatus.Failed);
            operation.ErrorCode.Should().Be(ErrorCodes.HabitLimitReached);
            operation.PolicyReason.Should().Be(expected);
            var failure = operation.Payload.Should().BeOfType<HabitCapacityFailure>().Subject;
            failure.ErrorCode.Should().Be(ErrorCodes.HabitLimitReached);
            failure.Arguments.Should().Equal(limit);
            operation.PendingOperationId.Should().BeNull();
            result.Value.PendingOperations.Should().BeNullOrEmpty();
            result.Value.AiMessage.Should().Be(expected);
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(expected);
            policy.DidNotReceive().Evaluate(Arg.Any<AgentPolicyEvaluationContext>());
            _pendingOperationStore.DidNotReceiveWithAnyArgs().Create(default, default!, default!, default!, default!, default!, default);
            await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                    && results[0].Error == expected && results[0].Payload is HabitCapacityFailure), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        }
        else
        {
            operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
            result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().NotBeEmpty();
            await AssertNoCompositionContinuation();
        }
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task G8_ReusedHabitsAndSubHabitsDoNotConsumeCreationCapacity(bool single, bool child)
    {
        var parents = SetupExistingParents("en");
        var mediator = Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habitRepo, _tagRepo, _goalRepo,
            _userDateService, _payGate, _unitOfWork);
        IAiTool tool = single ? new CreateHabitTool(_userDateService, mediator) : new BulkCreateHabitsTool(mediator);
        var captured = CapturedCompositionPlan("g8-edge-free-limit-en-r1").ToolCalls!.Single().Args;
        var node = System.Text.Json.Nodes.JsonNode.Parse(captured.GetProperty("habits")[0].GetRawText())!.AsObject();
        node[child ? "parent_habit_id" : "habit_id"] = parents[0].Id.ToString();
        var args = single ? JsonSerializer.SerializeToElement(node)
            : JsonSerializer.SerializeToElement(new { habits = new[] { node } });

        (await ((IArgumentCheckTool)tool).CheckArgumentsAsync(args, UserId, default)).IsSuccess.Should().BeTrue();

        await _payGate.DidNotReceive().CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    private IAgentPolicyEvaluator UseCapacityReplayExecutor(IAiTool tool, List<AgentExecuteOperationResponse> outcomes)
    {
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        _pendingOperationStore.Revise(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call =>
        {
            var context = call.Arg<AgentPolicyEvaluationContext>();
            var capability = catalog.GetCapability(context.CapabilityId)!;
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.ConfirmationRequired, capability,
                PendingOperation: new PendingAgentOperation(Guid.NewGuid(), capability.Id, tool.Name, tool.Name,
                    capability.RiskClass, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10)));
        });
        var registry = new AiToolRegistry([tool]);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var executor = new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(),
            ownership, Substitute.For<IAgentStepUpAuthorizationBridge>(), registry,
            new PendingOperationChangePreviewer(_habitRepo, _goalRepo, _tagRepo, _userDateService, registry),
            _pendingOperationStore, _unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var response = await executor.ExecuteAsync(call.Arg<AgentExecuteOperationRequest>(), call.Arg<CancellationToken>());
                outcomes.Add(response);
                return response;
            });
        return policy;
    }

    [Fact]
    public void G8_CapacityGroundingAcceptsOnlyTheTypedRefusalAndItsActualLimit()
    {
        var refusal = ToolResult.FromFailure(Result.Failure(ErrorMessages.HabitLimitReached.Format(37)));
        var result = new AiToolCallResult("bulk_create_habits", "capacity", false, null, null, refusal.Error, refusal.Payload);
        var facts = TurnFactSet.FromToolResults([result]);

        GroundednessChecker.Check("You've reached the 37 habit limit.", facts).Should().BeEmpty();
        GroundednessChecker.Check("Você chegou ao limite de 37 hábitos.", facts, language: "pt-BR").Should().BeEmpty();
        GroundednessChecker.Check("You've reached the 999 habit limit.", facts).Should().NotBeEmpty();
        TurnFactSet.FromToolResults([result with { Payload = new { limit = 999 } }]).Facts.Should().BeEmpty();
    }
}
