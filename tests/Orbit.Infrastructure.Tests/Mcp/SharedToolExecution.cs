using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Mcp;

internal static class SharedToolExecution
{
    internal static UpdateHabitTool CreateHabitTool(IGenericRepository<Habit> repository,
        IUserDateService dates, IUnitOfWork unitOfWork)
    {
        var mediator = Substitute.For<IMediator>();
        var handler = new UpdateHabitCommandHandler(repository, Substitute.For<IGenericRepository<SentReminder>>(),
            Substitute.For<IGenericRepository<Goal>>(), Substitute.For<IPayGateService>(),
            Substitute.For<IGoalCompletionService>(), dates, unitOfWork, new MemoryCache(new MemoryCacheOptions()));
        mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
        return new UpdateHabitTool(repository, dates, mediator);
    }

    internal static AgentOperationExecutor Create(IAiTool tool, IUnitOfWork unitOfWork)
    {
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(new AgentPolicyDecision(
            AgentPolicyDecisionStatus.Allowed, catalog.GetCapabilityByChatTool(tool.Name)!));
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var stepUp = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUp.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        return new AgentOperationExecutor(
            catalog, policy, Substitute.For<IAgentAuditService>(), ownership, stepUp,
            new AiToolRegistry([tool]), Substitute.For<IPendingOperationChangePreviewer>(),
            Substitute.For<IPendingAgentOperationStore>(), unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
    }
}
