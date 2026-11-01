using Orbit.Tests.Shared;
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
using Orbit.Application.ChecklistTemplates.Commands;
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
    [Fact]
    public async Task LargeAccountPayload_ProductionHabitReadKeepsMatchingItemsAtProvider()
    {
        SetupUserAndPayGate();
        var habits = Enumerable.Range(0, 120).Select(index => Habit.Create(new(HabitSchedule.OneTime,
            UserId, $"Reservation {index:D3} " + new string('x', 80), null, null, Today)).Value).ToList();
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>()).Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        var tool = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var read = await tool.ExecuteAsync(ParseArguments("{\"limit\":120}"), UserId, default);
        var full = read.Payload.Should().BeOfType<HabitListCard>().Subject;
        full.Items.Should().HaveCount(120);
        JsonSerializer.Serialize(full, PayloadBudgetSerializerOptions).Length.Should().BeGreaterThan(AppConstants.MaxAiToolPayloadJsonLength);
        await AssertActualModelHandoffAsync(new(tool.Name, "large", true, null, read.EntityName, null, full), truncated: true);
    }

    private static readonly JsonSerializerOptions PayloadBudgetSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(true, true)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 11)]
    [InlineData(true, false, 400)]
    public async Task TemplatePayloadBudget_ProductionReadSurvivesModelHandoff(bool supports, bool escapedNames = false, int total = 40)
    {
        SetupUserAndPayGate();
        var templates = Enumerable.Range(0, total).Select(index => ChecklistTemplate.Create(UserId,
            escapedNames ? $"Template {index:D2} ".PadRight(63, '\u6f22') + "\U0001f600" + new string('\u6f22', 35)
                : $"Template {index:D2}", Enumerable.Range(0, 10)
                .Select(item => $"Step {item:D2}: " + new string('x', 72)).ToList()).Value).ToList();
        _checklistTemplateRepo.FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(templates);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var queryHandler = new GetChecklistTemplatesQueryHandler(_checklistTemplateRepo, cache);
        _mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetChecklistTemplatesQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetChecklistTemplatesTool(_mediator);
        var handler = CreateHandler(tool);
        var blocks = UseProductionReadExecutor(tool);
        SetupAiResponse(ToolResponse(tool.Name, "templates", "{}"));
        ContinueWithReadDirective("[[orbit:read:templates]]");

        var result = await handler.Handle(new(UserId, "Show my templates", ClientContext: new AgentClientContext(
            SupportsParts: supports, PartKinds: ["recordList"], SupportsRecordListCard: supports)), default);

        result.IsSuccess.Should().BeTrue();
        var full = (await tool.ExecuteAsync(ParseArguments("{}"), UserId, default)).Payload
            .Should().BeAssignableTo<IReadOnlyList<ChecklistTemplateResponse>>().Subject;
        full.Should().HaveCount(total).And.OnlyContain(template => template.Items.Count == 10);
        foreach (var template in full)
            template.Items.Should().Equal(templates.Single(original => original.Id == template.Id).Items);
        var fullLength = JsonSerializer.Serialize(full, PayloadBudgetSerializerOptions).Length;
        var sent = CapturedBudgetResult();
        await AssertActualModelHandoffAsync(sent);
        var json = JsonSerializer.Serialize(sent.Payload, PayloadBudgetSerializerOptions);
        Console.WriteLine($"Template model payload: {json.Length}");
        json.Length.Should().BeLessThan(AppConstants.MaxAiToolPayloadJsonLength);
        if (fullLength <= AppConstants.MaxAiToolPayloadJsonLength)
            sent.Payload.Should().BeAssignableTo<IReadOnlyList<ChecklistTemplateResponse>>()
                .Subject.Should().BeEquivalentTo(full, "reads that fit must retain every fact");
        else
        {
            var model = sent.Payload.Should().BeOfType<ChecklistTemplateModelPayload>().Subject;
            model.TotalCount.Should().Be(total);
            model.OmittedTemplates.Should().Be(total - model.Templates.Count);
            if (total <= 40) model.OmittedTemplates.Should().Be(0);
            model.Templates.Should().NotBeEmpty();
            model.Templates.Select(row => row.Id).Should().Equal(full.Take(model.Templates.Count).Select(template => template.Id));
            model.Templates.Should().OnlyContain(row => row.ItemCount == 10);
            model.Templates.Should().OnlyContain(row => row.Name.Length >= Math.Min(16, full.Single(template => template.Id == row.Id).Name.Length));
            model.Templates.Should().OnlyContain(row => row.Name.Length <= 64 && !char.IsHighSurrogate(row.Name[row.Name.Length - 1]));
            if (model.OmittedTemplates == 0) model.Templates.SelectMany(row => row.Items).Should().NotBeEmpty();
            foreach (var row in model.Templates)
                row.Items.Should().Equal(full.Single(template => template.Id == row.Id).Items.Take(row.Items.Count));
        }
        if (supports)
        {
            var card = result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Block
                .Should().BeOfType<RecordListCard>().Subject;
            card.TotalCount.Should().Be(total);
            card.Items.Should().HaveCount(Math.Min(10, total)).And.OnlyContain(item => item.Count == 10);
            card.Read!.RefreshToken.Should().NotBeNullOrWhiteSpace();
            card.Read.QueryFamily.Should().Be(tool.Name);
            var readTokens = new ReadQueryTokenService(Options.Create(new JwtSettings
            {
                SecretKey = "test-read-query-signing-key-with-32-bytes",
                Issuer = "test",
                Audience = "test"
            }));
            var refresh = new RefreshReadBlockQueryHandler(readTokens, _operationExecutor, blocks, _featureFlagService);
            var refreshed = await refresh.Handle(new(UserId, card.Read.RefreshToken,
                new(SupportsParts: true, PartKinds: ["recordList"], SupportsRecordListCard: true)), default);
            refreshed.IsSuccess.Should().BeTrue();
            refreshed.Value.Block.Should().BeOfType<RecordListCard>().Which.Items.Should().BeEquivalentTo(card.Items);
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
    [InlineData(100, true, true, true)]
    [InlineData(20, true, true, true)]
    [InlineData(100, true, true, false, 101)]
    [InlineData(100, false, false, false, 100, "pt-BR")]
    [InlineData(100, true, false, false, 0)]
    public async Task RankingPayloadBudget_ProductionReadSurvivesModelHandoff(int pageSize, bool supports,
        bool escapedNames = false, bool filtered = false, int total = 100, string language = "en")
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        _userDateService.GetUserTimeZoneAsync(UserId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var habits = Enumerable.Range(0, total).Select(index => Habit.Create(new(HabitSchedule.Recurring,
            UserId, escapedNames ? ($"Daily habit {index:D3} ".PadRight(63, '\u6f22') + "\U0001f600")
                .PadRight(AppConstants.MaxHabitTitleLength, '\u6f22')
                : $"Daily habit {index:D3} " + new string('x', 50), FrequencyUnit.Day, 1, Today)).Value).ToList();
        foreach (var habit in habits) habit.Log(Today, advanceDueDate: false).IsSuccess.Should().BeTrue();
        var tokens = new HabitRankingTokenService(Options.Create(new JwtSettings
        {
            SecretKey = "payload-budget-ranking-signing-secret",
            Issuer = "test",
            Audience = "test"
        }));
        var queryHandler = new GetHabitRankingQueryHandler(new InMemoryHabitRankingReader(habits), _userDateService, tokens,
            _productAnalytics, NullLogger<GetHabitRankingQueryHandler>.Instance);
        _mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetHabitRankingQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetHabitRankingTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var args = JsonSerializer.Serialize(new
        {
            date_from = Today,
            date_to_exclusive = Today.AddDays(1),
            page_size = pageSize,
            habit_ids = filtered ? habits.Select(habit => habit.Id).ToArray() : null
        });
        SetupAiResponse(ToolResponse(tool.Name, "ranking", args));
        ContinueWithReadDirective("[[orbit:ranking]]");

        var result = await handler.Handle(new(UserId, "Rank my completions", ClientContext: new AgentClientContext(
            SupportsParts: supports, PartKinds: ["habitRanking"])), default);

        result.IsSuccess.Should().BeTrue();
        var full = (await tool.ExecuteAsync(ParseArguments(args), UserId, default)).Payload.Should().BeOfType<HabitRankingResponse>().Subject;
        full.Rows.Should().HaveCount(Math.Min(pageSize, total));
        full.Query.PageSize.Should().Be(pageSize);
        var sent = CapturedBudgetResult();
        await AssertActualModelHandoffAsync(sent);
        var json = JsonSerializer.Serialize(sent.Payload, PayloadBudgetSerializerOptions);
        Console.WriteLine($"Ranking page size {pageSize} model payload: {json.Length}");
        json.Length.Should().BeLessThan(AppConstants.MaxAiToolPayloadJsonLength);
        if (JsonSerializer.Serialize(full, PayloadBudgetSerializerOptions).Length <= AppConstants.MaxAiToolPayloadJsonLength)
            sent.Payload.Should().BeOfType<HabitRankingResponse>()
                .Subject.Should().BeEquivalentTo(full, "ranking pages that fit must retain every fact");
        else
        {
            var model = sent.Payload.Should().BeOfType<HabitRankingModelPayload>().Subject;
            model.Rows.Select(row => Guid.Parse((string)row[0])).Should().Equal(full.Rows.Select(row => row.HabitId));
            model.Rows.Select(row => (int)row[1]).Should().Equal(full.Rows.Select(row => row.CompletedCount));
            model.Rows.Select((_, index) => model.Offset + index + 1).Should().Equal(full.Rows.Select(row => row.Rank));
            model.QueryToken.Should().Be(full.QueryToken);
            model.NextCursor.Should().Be(full.NextCursor);
            model.Revision.Should().Be(full.Revision);
            model.IsComplete.Should().Be(full.IsComplete);
            model.Query.DateFrom.Should().Be(full.Query.DateFrom);
            model.Query.DateToExclusive.Should().Be(full.Query.DateToExclusive);
            model.Query.FilteredHabitCount.Should().Be(filtered ? total : 0);
            model.OmittedNames.Should().Be(full.Rows.Count - model.Names.Count);
            model.Names.Should().OnlyContain(pair => full.Rows.Any(row => row.HabitId.ToString("N") == pair.Key
                && row.Name.StartsWith(pair.Value, StringComparison.Ordinal)));
            model.Names.Values.Should().OnlyContain(name => name.Length <= 64 && !char.IsHighSurrogate(name[name.Length - 1]));
        }
        var maxCounts = HabitRankingModelPayload.From(full with
        {
            Rows = full.Rows.Select(row => row with { CompletedCount = int.MaxValue }).ToArray()
        });
        JsonSerializer.Serialize(maxCounts, PayloadBudgetSerializerOptions).Length.Should().BeLessThan(AppConstants.MaxAiToolPayloadJsonLength);
        maxCounts.Rows.Should().HaveCount(full.Rows.Count);
        await AssertActualModelHandoffAsync(sent with { Payload = maxCounts });
        if (supports)
            result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Block
                .Should().BeOfType<HabitRankingCard>().Which.Ranking.Should().BeEquivalentTo(full);
        else
        {
            foreach (var row in full.Rows) result.Value.AiMessage.Should().Contain($"{row.Name}: {row.CompletedCount}");
            result.Value.AiMessage.Should().Contain(language == "pt-BR" ? "Conclusões de hábitos" : "Habit completions");
        }
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
            await AssertActualModelHandoffAsync(sent with { Payload = HabitRankingModelPayload.From(pageResponse) });
        }
        ids.Should().BeEquivalentTo(habits.Select(habit => habit.Id)).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task TemplatePayloadBudget_EleventhTemplateLookupPreservesItemsForAppend()
    {
        SetupUserAndPayGate();
        var templates = Enumerable.Range(0, 40).Select(index => ChecklistTemplate.Create(UserId,
            $"Template {index:D2}", Enumerable.Range(0, 10)
                .Select(item => $"Step {item:D2}: " + new string('x', 72)).ToList()).Value).ToList();
        var foreign = ChecklistTemplate.Create(Guid.NewGuid(), "Foreign", ["Private"]).Value;
        _checklistTemplateRepo.FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => templates.Append(foreign).Where(call.Arg<Expression<Func<ChecklistTemplate, bool>>>().Compile()).ToList());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var queryHandler = new GetChecklistTemplatesQueryHandler(_checklistTemplateRepo, cache);
        _mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => queryHandler.Handle(call.Arg<GetChecklistTemplatesQuery>(), call.Arg<CancellationToken>()));
        var tool = new GetChecklistTemplatesTool(_mediator);
        var handler = CreateHandler(tool);
        UseProductionReadExecutor(tool);
        var ordered = (await queryHandler.Handle(new(UserId), default)).Value;
        var target = ordered[10];
        SetupAiResponse(ToolResponse(tool.Name, "template_lookup", JsonSerializer.Serialize(new { template_id = target.Id })));
        ContinueWithReadDirective("[[orbit:read:template_lookup]]");

        var result = await handler.Handle(new(UserId, "Append a step to my eleventh template"), default);

        result.IsSuccess.Should().BeTrue();
        var sent = CapturedBudgetResult();
        await AssertActualModelHandoffAsync(sent);
        var lookup = sent.Payload.Should().BeAssignableTo<IReadOnlyList<ChecklistTemplateResponse>>().Subject
            .Should().ContainSingle().Which;
        lookup.Should().BeEquivalentTo(target);
        _checklistTemplateRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(),
            cancellationToken: Arg.Any<CancellationToken>()).Returns(call => templates
                .SingleOrDefault(call.Arg<Expression<Func<ChecklistTemplate, bool>>>().Compile()));
        var updateHandler = new UpdateChecklistTemplateCommandHandler(_checklistTemplateRepo, _unitOfWork, cache);
        _mediator.Send(Arg.Any<UpdateChecklistTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => updateHandler.Handle(call.Arg<UpdateChecklistTemplateCommand>(), call.Arg<CancellationToken>()));
        var updated = await new UpdateChecklistTemplateTool(_mediator).ExecuteAsync(ParseArguments(
            JsonSerializer.Serialize(new { template_id = lookup.Id, items = lookup.Items.Append("New step") })), UserId, default);
        updated.Success.Should().BeTrue();
        templates.Single(template => template.Id == target.Id).Items.Should().Equal(target.Items.Append("New step"));
        var missing = await tool.ExecuteAsync(ParseArguments(JsonSerializer.Serialize(new { template_id = foreign.Id })), UserId, default);
        missing.Payload.Should().BeAssignableTo<IReadOnlyList<ChecklistTemplateResponse>>().Subject.Should().BeEmpty();
    }

    private AiToolCallResult CapturedBudgetResult() => _aiIntentService.ReceivedCalls()
        .Single(call => call.GetMethodInfo().Name == nameof(IAiIntentService.ContinueWithToolResultsAsync))
        .GetArguments()[1].Should().BeAssignableTo<IReadOnlyList<AiToolCallResult>>().Subject.Single();

    private static async Task AssertActualModelHandoffAsync(AiToolCallResult sent, bool truncated = false)
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
        using var request = JsonDocument.Parse(transport.RequestBody!);
        var output = request.RootElement.GetProperty("input").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "function_call_output");
        using var body = JsonDocument.Parse(output.GetProperty("output").GetString()!);
        var payload = body.RootElement.GetProperty("payload");
        (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("dropped", out _)).Should().BeFalse("the production handoff must retain typed-read facts");
        if (truncated)
        {
            payload.GetRawText().Length.Should().BeLessThanOrEqualTo(AppConstants.MaxAiToolPayloadJsonLength);
            payload.GetProperty("truncated").GetBoolean().Should().BeTrue();
            var data = payload.GetProperty("data");
            data.GetProperty("totalCount").GetInt32().Should().Be(120);
            var items = data.GetProperty("items");
            items.GetArrayLength().Should().BeInRange(1, 119);
            items[0].GetProperty("title").GetString().Should().StartWith("Reservation");
            payload.GetProperty("collections")[0].GetProperty("has_more").GetBoolean().Should().BeTrue();
        }
        else payload.GetRawText().Should().Be(JsonSerializer.Serialize(sent.Payload, PayloadBudgetSerializerOptions));
        if (sent.ReadDirective is not null)
            body.RootElement.GetProperty("readDirective").GetString().Should().Be(sent.ReadDirective);
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
