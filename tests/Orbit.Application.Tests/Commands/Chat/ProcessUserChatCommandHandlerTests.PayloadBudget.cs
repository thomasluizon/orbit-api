#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private static readonly JsonSerializerOptions PayloadBudgetSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TemplatePayloadBudget_ProductionReadSurvivesModelHandoff(bool supports)
    {
        SetupUserAndPayGate();
        var templates = Enumerable.Range(0, 40).Select(index => ChecklistTemplate.Create(UserId,
            $"Template {index:D2}", Enumerable.Range(0, 10)
                .Select(item => $"Step {item:D2}: " + new string('x', 72)).ToList()).Value).ToList();
        _checklistTemplateRepo.FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(templates);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var queryHandler = new GetChecklistTemplatesQueryHandler(_checklistTemplateRepo, cache);
        _mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetChecklistTemplatesQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetChecklistTemplatesTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "templates", "{}"));
        ContinueWithReadDirective("[[orbit:read:templates]]");

        var result = await handler.Handle(new(UserId, "Show my templates", ClientContext: new AgentClientContext(
            SupportsParts: supports, PartKinds: ["recordList"], SupportsRecordListCard: supports)), default);

        result.IsSuccess.Should().BeTrue();
        var full = (await tool.ExecuteAsync(ParseArguments("{}"), UserId, default)).Payload
            .Should().BeAssignableTo<IReadOnlyList<ChecklistTemplateResponse>>().Subject;
        full.Should().HaveCount(40).And.OnlyContain(template => template.Items.Count == 10);
        JsonSerializer.Serialize(full, PayloadBudgetSerializerOptions).Length.Should().BeGreaterThan(AppConstants.MaxAiToolPayloadJsonLength);
        var sent = CapturedBudgetResult();
        await AssertActualModelHandoffAsync(sent);
        var json = JsonSerializer.Serialize(sent.Payload, PayloadBudgetSerializerOptions);
        Console.WriteLine($"Template model payload: {json.Length}");
        json.Length.Should().BeLessThan(AppConstants.MaxAiToolPayloadJsonLength);
        if (supports)
        {
            var card = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Block
                .Should().BeOfType<RecordListCard>().Subject;
            card.TotalCount.Should().Be(40);
            card.Items.Should().HaveCount(10).And.OnlyContain(item => item.Count == 10);
            var ids = card.Items.Select(item => item.Id).ToList();
            var pages = new GetRecordListPageQueryHandler(_mediator);
            while (card.NextCursor is { } cursor)
            {
                var page = await pages.Handle(new(UserId, "templates", cursor), default);
                page.IsSuccess.Should().BeTrue();
                card = page.Value;
                ids.AddRange(card.Items.Select(item => item.Id));
            }
            ids.Should().Equal(full.Select(template => template.Id.ToString()));
        }
        else result.Value.AiMessage.Should().Contain("Template").And.NotContain("[[orbit:");
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(100, true)]
    [InlineData(100, false)]
    public async Task RankingPayloadBudget_ProductionReadSurvivesModelHandoff(int pageSize, bool supports)
    {
        SetupUserAndPayGate();
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var habits = Enumerable.Range(0, 100).Select(index => Habit.Create(new(HabitSchedule.Recurring,
            UserId, $"Daily habit {index:D3} " + new string('x', 50), FrequencyUnit.Day, 1, Today)).Value).ToList();
        foreach (var habit in habits) habit.Log(Today, advanceDueDate: false).IsSuccess.Should().BeTrue();
        _habitRepo.ProjectAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<HabitRankingRow>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<IQueryable<Habit>, IQueryable<HabitRankingRow>>>(1)(
                habits.Where(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()).AsQueryable()).ToList());
        var tokens = new HabitRankingTokenService(Options.Create(new JwtSettings
        {
            SecretKey = "payload-budget-ranking-signing-secret",
            Issuer = "test",
            Audience = "test"
        }));
        var queryHandler = new GetHabitRankingQueryHandler(_habitRepo, _userDateService, tokens,
            _productAnalytics, NullLogger<GetHabitRankingQueryHandler>.Instance);
        _mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetHabitRankingQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetHabitRankingTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var args = JsonSerializer.Serialize(new { date_from = Today, date_to_exclusive = Today.AddDays(1), page_size = pageSize });
        SetupAiResponse(ToolResponse(tool.Name, "ranking", args));
        ContinueWithReadDirective("[[orbit:ranking]]");

        var result = await handler.Handle(new(UserId, "Rank my completions", ClientContext: new AgentClientContext(
            SupportsParts: supports, PartKinds: ["habitRanking"])), default);

        result.IsSuccess.Should().BeTrue();
        var full = (await tool.ExecuteAsync(ParseArguments(args), UserId, default)).Payload.Should().BeOfType<HabitRankingResponse>().Subject;
        full.Rows.Should().HaveCount(pageSize);
        full.Query.PageSize.Should().Be(pageSize);
        var sent = CapturedBudgetResult();
        await AssertActualModelHandoffAsync(sent);
        var json = JsonSerializer.Serialize(sent.Payload, PayloadBudgetSerializerOptions);
        Console.WriteLine($"Ranking page size {pageSize} model payload: {json.Length}");
        json.Length.Should().BeLessThan(AppConstants.MaxAiToolPayloadJsonLength);
        if (supports)
            result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Block
                .Should().BeOfType<HabitRankingCard>().Which.Ranking.Should().BeEquivalentTo(full);
        else
            foreach (var row in full.Rows) result.Value.AiMessage.Should().Contain($"{row.Name}: {row.CompletedCount}");
        var refreshed = await queryHandler.Handle(new(UserId, QueryToken: full.QueryToken, Refresh: true), default);
        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Should().BeEquivalentTo(full);
        var ids = full.Rows.Select(row => row.HabitId).ToList();
        var pageResponse = full;
        while (pageResponse.NextCursor is { } cursor)
        {
            var page = await queryHandler.Handle(new(UserId, QueryToken: full.QueryToken, Cursor: cursor), default);
            page.IsSuccess.Should().BeTrue();
            pageResponse = page.Value;
            pageResponse.QueryId.Should().Be(full.QueryId);
            ids.AddRange(pageResponse.Rows.Select(row => row.HabitId));
        }
        ids.Should().BeEquivalentTo(habits.Select(habit => habit.Id)).And.OnlyHaveUniqueItems();
    }

    private AiToolCallResult CapturedBudgetResult() => _aiIntentService.ReceivedCalls()
        .Single(call => call.GetMethodInfo().Name == nameof(IAiIntentService.ContinueWithToolResultsAsync))
        .GetArguments()[1].Should().BeAssignableTo<IReadOnlyList<AiToolCallResult>>().Subject.Single();

    private static async Task AssertActualModelHandoffAsync(AiToolCallResult sent)
    {
        using var transport = new PayloadBudgetTransport();
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(transport))
        }), NullLogger<AiCompletionClient>.Instance, usage);
        var budgeted = new BudgetedAiCompletionClient(client, Substitute.For<IAiDailyBudget>(), new HttpContextAccessor(),
            NullLogger<BudgetedAiCompletionClient>.Instance, Substitute.For<IAiRequestCostEstimator>());
        var service = new AiIntentService(budgeted, usage, NullLogger<AiIntentService>.Instance);
        var context = new AiConversationContext
        {
            UserId = UserId,
            Messages = new List<ResponseItem> { ResponseItem.CreateFunctionCallItem(sent.Id, sent.Name, BinaryData.FromString("{}")) },
            Options = new CreateResponseOptions { Model = "gpt-6-luna" }
        };

        var response = await service.ContinueWithToolResultsAsync(context, [sent]);

        response.IsSuccess.Should().BeTrue();
        transport.RequestBody.Should().NotBeNull();
        transport.RequestBody.Should().NotContain("\\\"dropped\\\":true", "the production handoff must retain typed-read facts");
    }

    private sealed class PayloadBudgetTransport : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            var response = new ResponseResult
            {
                Id = "resp_budget",
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1700000000),
                Model = "gpt-6-luna",
                Status = ResponseStatus.Completed
            };
            response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Done"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ModelReaderWriter.Write(response).ToString(), Encoding.UTF8, "application/json")
            };
        }
    }
}
