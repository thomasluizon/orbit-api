#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed partial class OfferDeadlineControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EquivalentGoalRequest_WhitespaceContinuationComposesToolResults(bool createGoal, bool cancelPreview)
    {
        await using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var transport = new EmptyContinuationTransport(createGoal);
        using var http = new HttpClient(transport);
        var (controller, goal, offers, mediator, messages) = CreateController(http, default, db);
        var pending = new PendingAgentOperationStore(db, Options.Create(new AgentPlatformSettings()));
        if (cancelPreview)
            transport.BeforeContinuation = () =>
            {
                var stored = db.PendingAgentOperations.Should().ContainSingle().Subject;
                pending.Cancel(goal.UserId, stored.Id, stored.OperationFingerprint).Should().BeTrue();
            };
        var input = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath("OfferDeadlineRequest.json")))!;

        var result = await controller.ProcessChat(input["message"]!.GetValue<string>(), "[]", null, default,
            input["clientContext"]!.ToJsonString(), evalTrace: "1");

        var reply = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ChatResponse>().Subject;
        if (createGoal && !cancelPreview)
        {
            var preview = reply.PendingOperations.Should().ContainSingle().Subject;
            preview.Items.Should().ContainSingle().Which.Fields.Should().Contain(field =>
                field.Field == "title" && field.NewValue == goal.Title);
            preview.PreviewFingerprint.Should().NotBeNullOrWhiteSpace();
            pending.IsAwaitingConfirmation(goal.UserId, preview.Id).Should().BeTrue();
            pending.GetExecution(goal.UserId, preview.Id)!.PreviewFingerprint.Should().Be(preview.PreviewFingerprint);
        }
        else
        {
            var card = reply.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Subject.Block
                .Should().BeOfType<GoalListCard>().Subject;
            card.Items.Should().ContainSingle(item => item.Id == goal.Id.ToString());
            reply.PendingOperations.Should().BeNullOrEmpty();
        }
        reply.AiMessage.Should().BeNullOrEmpty();
        reply.Actions.Should().BeEmpty();
        reply.FollowUps.Should().BeNullOrEmpty();
        reply.MessageAllowanceConsumed.Should().BeTrue();
        reply.EvalTrace!.ToolCalls.Select(call => call.Name).Should().BeEquivalentTo(
            createGoal ? ["query_goals", "create_goal"] : new[] { "query_goals" });
        messages.Should().Contain(message => message.Contains("RoundIndex=2", StringComparison.Ordinal)
            && message.Contains("ResponseStatus=Completed IncompleteReason=none", StringComparison.Ordinal)
            && message.Contains("OutputItemKinds={\"message\":1}", StringComparison.Ordinal)
            && message.Contains("ContentPartKinds={\"OutputText\":1}", StringComparison.Ordinal)
            && message.Contains("MessageRoles={\"Assistant\":1}", StringComparison.Ordinal)
            && message.Contains(createGoal ? "EarlierToolNames=create_goal,query_goals" : "EarlierToolNames=query_goals",
                StringComparison.Ordinal));
        transport.Requests.Should().HaveCount(2);
        db.Goals.Should().ContainSingle().Which.Id.Should().Be(goal.Id);
        await mediator.DidNotReceive().Send(Arg.Any<CreateGoalCommand>(), Arg.Any<CancellationToken>());
        await offers.DidNotReceiveWithAnyArgs().IssueAsync(default, default, default!, default);
    }

    private sealed class EmptyContinuationTransport(bool createGoal) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Action? BeforeContinuation { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Requests.Count == 2) BeforeContinuation?.Invoke();
            var response = ResponsesTestData.Response(" \t\n ");
            if (Requests.Count == 1)
            {
                response = ResponsesTestData.Response();
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("read", "query_goals", BinaryData.FromString("{}")));
                if (createGoal)
                    response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("create", "create_goal",
                        BinaryData.FromString("{\"title\":\"Run five kilometers\",\"target_value\":5,\"unit\":\"km\"}")));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ModelReaderWriter.Write(response).ToString(), Encoding.UTF8, "application/json")
            };
        }
    }
}
