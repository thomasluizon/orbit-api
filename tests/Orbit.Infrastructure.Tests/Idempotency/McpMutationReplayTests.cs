using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Orbit.Api.Idempotency;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Idempotency;

public class McpMutationReplayTests
{
    [Fact]
    public async Task FailedTransactionalTool_RollsBackWrites_AndKeepsReplayResponse()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Rollback", "rollback@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var tool = Substitute.For<IAiTool, ITransactionalAiTool>();
        tool.Name.Returns("assign_tags");
        tool.Description.Returns("Assign tags");
        tool.GetParameterSchema().Returns(new { type = "object" });
        tool.ExecuteAsync(Arg.Any<JsonElement>(), user.Id, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            db.Tags.Add(Tag.Create(user.Id, "Must roll back", "#ffffff").Value);
            await db.SaveChangesAsync(call.Arg<CancellationToken>());
            return new ToolResult(false, Error: "refused after a write");
        });
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(new AgentPolicyDecision(
            AgentPolicyDecisionStatus.Allowed, catalog.GetCapabilityByChatTool("assign_tags")));
        using var services = new ServiceCollection().AddScoped(_ => factory.CreateContext()).BuildServiceProvider();
        var executor = new AgentOperationExecutor(catalog, policy, new AgentAuditService(services.GetRequiredService<IServiceScopeFactory>()),
            new AgentTargetOwnershipService(db), Substitute.For<IAgentStepUpAuthorizationBridge>(), new AiToolRegistry([tool]),
            Substitute.For<IPendingOperationChangePreviewer>(), Substitute.For<IPendingAgentOperationStore>(), work,
            NullLogger<AgentOperationExecutor>.Instance);
        var request = new ExecuteAgentMutationCommand(new AgentExecuteOperationRequest(user.Id, "assign_tags",
            JsonSerializer.SerializeToElement(new { }), AgentExecutionSurface.Mcp, AgentAuthMethod.ApiKey, [AgentScopes.WriteTags]));
        async Task<AgentExecuteOperationResponse> Run()
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "ApiKey"));
            context.Request.Headers["Idempotency-Key"] = "failed-request";
            var behavior = new IdempotencyBehavior<ExecuteAgentMutationCommand, AgentExecuteOperationResponse>(
                new HttpIdempotencyContext(new HttpContextAccessor { HttpContext = context }), new IdempotencyStore(db), work);
            return await behavior.Handle(request, token => executor.ExecuteAsync(request.Request, token), default);
        }
        var response = await Run();
        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        db.Tags.Count().Should().Be(0);
        db.AgentAuditLogs.Count().Should().Be(1);
        var replay = await Run();
        JsonSerializer.Serialize(replay).Should().Be(JsonSerializer.Serialize(response));
        db.ProcessedRequests.Count().Should().Be(1);
        db.AgentAuditLogs.Count().Should().Be(1);
    }

    [Fact]
    public async Task MutationReplay_ReturnsSamePayload_AndAppliesOncePerKey()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Replay", "replay@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var store = new IdempotencyStore(db);
        var request = new ExecuteAgentMutationCommand(new AgentExecuteOperationRequest(user.Id, "create_tag",
            JsonSerializer.SerializeToElement(new { name = "Tag" }), AgentExecutionSurface.Mcp, AgentAuthMethod.ApiKey));
        var applications = 0;
        async Task<AgentExecuteOperationResponse> Run(string key)
        {
            var context = new DefaultHttpContext();
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "ApiKey"));
            context.Request.Headers["Idempotency-Key"] = key;
            var idempotency = new HttpIdempotencyContext(new HttpContextAccessor { HttpContext = context });
            var behavior = new IdempotencyBehavior<ExecuteAgentMutationCommand, AgentExecuteOperationResponse>(idempotency, store, work);
            return await behavior.Handle(request, async token =>
            {
                applications++;
                var tag = Tag.Create(user.Id, $"Tag {applications}", "#ffffff").Value;
                db.Tags.Add(tag);
                await db.SaveChangesAsync(token);
                return new AgentExecuteOperationResponse(new AgentOperationResult("create_tag", "create_tag", AgentRiskClass.Low,
                    AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, TargetId: tag.Id.ToString(),
                    Payload: new { tagId = tag.Id }));
            }, default);
        }
        var first = await Run("request-one");
        var replay = await Run("request-one");
        JsonSerializer.Serialize(replay).Should().Be(JsonSerializer.Serialize(first));
        applications.Should().Be(1);
        await Run("request-two");
        applications.Should().Be(2);
        db.Tags.Count().Should().Be(2);
        db.ProcessedRequests.Count().Should().Be(2);
    }
}
