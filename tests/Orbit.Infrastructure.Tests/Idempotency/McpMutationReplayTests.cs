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
            context.Items[HttpIdempotencyContext.McpRequestKey] = key;
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
