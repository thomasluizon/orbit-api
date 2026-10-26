#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.ChecklistTemplates.Validators;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AgentChatWriteHoldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestTokenCeiling_ThreeCallTemplateEdit_PreservesElevenItemPreview(bool cached)
    {
        var result = await ReplayTemplateEditAsync(cached: cached);

        result.Response.IsSuccess.Should().BeTrue();
        result.Transport.Bodies.Count.Should().Be(3);
        result.Transport.TotalTokens.Should().BeLessThan(AppConstants.MaxAiRequestTotalTokens);
        using var lastRequest = JsonDocument.Parse(result.Transport.Bodies[2]);
        var read = lastRequest.RootElement.GetProperty("input").EnumerateArray()
            .Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                && item.GetProperty("call_id").GetString() == "lookup");
        using var readBody = JsonDocument.Parse(read.GetProperty("output").GetString()!);
        readBody.RootElement.GetProperty("payload").GetProperty("retired").GetBoolean().Should().BeTrue();
        AssertTemplatePreview(result.Response.Value, result.Template);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestTokenCeiling_AfterStagedTemplatePreview_PreservesPreview(bool toolCallAtCeiling)
    {
        var result = await ReplayTemplateEditAsync(forceCeiling: true, toolCallAtCeiling);

        result.Response.IsSuccess.Should().BeTrue("the write preview was already staged before the ceiling");
        result.Transport.Bodies.Count.Should().Be(3);
        AssertTemplatePreview(result.Response.Value, result.Template);
    }

    [Fact]
    public async Task RequestTokenCeiling_TargetedReadBeforeEdit_CompletesDespiteRawCachedTotal()
    {
        var result = await ReplayTemplateEditAsync(cached: true, rereadTarget: true);

        result.Response.IsSuccess.Should().BeTrue();
        result.Transport.Bodies.Count.Should().Be(4);
        result.Transport.TotalTokens.Should().BeGreaterThan(AppConstants.MaxAiRequestTotalTokens);
        using var thirdRequest = JsonDocument.Parse(result.Transport.Bodies[2]);
        var lookup = thirdRequest.RootElement.GetProperty("input").EnumerateArray()
            .Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                && item.GetProperty("call_id").GetString() == "target");
        using var payload = JsonDocument.Parse(lookup.GetProperty("output").GetString()!);
        payload.RootElement.GetProperty("payload").EnumerateArray().Single().GetProperty("items")
            .EnumerateArray().Select(item => item.GetString()).Should().Equal(result.Template.Items);
        AssertTemplatePreview(result.Response.Value, result.Template);
    }

    private void AssertTemplatePreview(ChatResponse response, ChecklistTemplate template)
    {
        var preview = response.PendingOperations.Should().ContainSingle().Which;
        _pendingOperationStore.IsAwaitingConfirmation(_userId, preview.Id).Should().BeTrue();
        var staged = _pendingOperationStore.GetExecution(_userId, preview.Id)!;
        staged.OperationId.Should().Be("update_checklist_template");
        staged.Arguments.GetProperty("items").EnumerateArray().Select(item => item.GetString())
            .Should().Equal(template.Items.Append("Step 11 of template 11"));
        template.Items.Should().HaveCount(10, "approval is still required");
        response.AiMessage.Should().NotBeNullOrWhiteSpace();
    }

    private async Task<(Result<ChatResponse> Response, TemplateEditTransport Transport, ChecklistTemplate Template)>
        ReplayTemplateEditAsync(bool forceCeiling = false, bool toolCallAtCeiling = false, bool cached = false, bool rereadTarget = false)
    {
        var today = new DateOnly(2026, 10, 26);
        var templates = Enumerable.Range(1, 40).Select(index => ChecklistTemplate.Create(_userId,
            $"Synthetic template {index:D2} routine", Enumerable.Range(1, 10)
                .Select(step => $"Step {step:D2} of template {index:D2}").ToList()).Value).ToArray();
        var habits = Enumerable.Range(1, 100).Select(index => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, _userId, $"Synthetic ranking habit {index:D3}", FrequencyUnit.Day, 1, today)).Value).ToArray();
        foreach (var habit in habits) habit.Log(today, advanceDueDate: false).IsSuccess.Should().BeTrue();
        _dbContext.ChecklistTemplates.AddRange(templates);
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();
        var target = templates[10];
        var templateRepo = new GenericRepository<ChecklistTemplate>(_dbContext);
        var habitRepo = new GenericRepository<Habit>(_dbContext);
        var goalRepo = new GenericRepository<Goal>(_dbContext);
        var tagRepo = new GenericRepository<Tag>(_dbContext);
        var mediator = Substitute.For<IMediator>();
        using var services = new ServiceCollection()
            .AddSingleton<IGenericRepository<ChecklistTemplate>>(templateRepo)
            .AddSingleton<IValidator<UpdateChecklistTemplateCommand>>(new UpdateChecklistTemplateCommandValidator())
            .BuildServiceProvider();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => new CheckChatCommandQueryHandler(services).Handle(call.Arg<CheckChatCommandQuery>(), call.Arg<CancellationToken>()));
        mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => new GetChecklistTemplatesQueryHandler(templateRepo, _flagCache)
                .Handle(call.Arg<GetChecklistTemplatesQuery>(), call.Arg<CancellationToken>()));
        var tools = AiInputBudgetTests.RegisteredTools().Select(tool => tool.Name switch
        {
            "get_checklist_templates" => (IAiTool)new GetChecklistTemplatesTool(mediator),
            "update_checklist_template" => new UpdateChecklistTemplateTool(mediator),
            _ => tool
        }).ToArray();
        var registry = new AiToolRegistry(tools);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        dates.GetUserWeekStartDayAsync(_userId, Arg.Any<CancellationToken>()).Returns(1);
        var previewer = new PendingOperationChangePreviewer(habitRepo, goalRepo, tagRepo, dates, registry,
            templateRepository: templateRepo);
        var transport = new TemplateEditTransport(target, forceCeiling, toolCallAtCeiling, cached, rereadTarget);
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(new ResponsesClient(new ApiKeyCredential("test"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(transport))
        }), NullLogger<AiCompletionClient>.Instance, usage);
        var intent = new AiIntentService(AiBudgetTestDouble.Wrap(client), usage, NullLogger<AiIntentService>.Instance,
            Options.Create(new AiSettings
            {
                Pricing = new()
                {
                    ["gpt-6-luna"] = new() { InputPerMillionUsd = 0.10m, CachedInputPerMillionUsd = 0.01m }
                }
            }));
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(_userId, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var work = Substitute.For<IUnitOfWork>();
        work.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => _dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(_userId, today, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var executor = CreateExecutor(registry, previewer);
        using var readServices = new ServiceCollection().AddSingleton<IAgentOperationExecutor>(executor)
            .AddSingleton<IPendingClarificationStore>(Substitute.For<IPendingClarificationStore>()).BuildServiceProvider();
        var handler = new ProcessUserChatCommandHandler(
            new(habitRepo, goalRepo, new GenericRepository<User>(_dbContext), tagRepo, templateRepo, Substitute.For<IFeatureFlagService>()),
            new(intent, registry, new SystemPromptBuilder(), new AgentCatalogService(tools)),
            new(dates, Substitute.For<IUserStreakService>(), quota, work, readServices.GetRequiredService<IServiceScopeFactory>(),
                executor, Substitute.For<IPendingClarificationStore>(), progress,
                Substitute.For<IGamificationService>(), mediator, Substitute.For<IProductAnalytics>(),
                Substitute.For<IHabitScheduleLogReader>(), Substitute.For<IIssuedAstraBlockStore>(), _pendingOperationStore,
                PendingOperationPreviewer: previewer),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), ModerationTestDouble.Clean());

        var response = await handler.Handle(new(_userId,
            "Add a step called 'Step 11 of template 11' to my checklist template 'Synthetic template 11 routine'",
            ClientContext: new AgentClientContext(SupportsPendingOperationChanges: true)), default);
        return (response, transport, target);
    }

    private sealed class TemplateEditTransport(ChecklistTemplate template, bool forceCeiling, bool toolCallAtCeiling,
        bool cached, bool rereadTarget) : HttpMessageHandler
    {
        private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        public List<string> Bodies { get; } = [];
        public int TotalTokens { get; private set; }
        private int _originalReadTokens;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (Bodies.Count == 1)
                foreach (var tool in root.GetProperty("tools").EnumerateArray().OrderByDescending(tool => _tokenizer.CountTokens(tool.GetRawText())).Take(8))
                    Console.WriteLine($"Tool {tool.GetProperty("name").GetString()}: {_tokenizer.CountTokens(tool.GetRawText())}");
            var input = root.GetProperty("input").EnumerateArray().ToArray();
            var toolTokens = _tokenizer.CountTokens(JsonSerializer.Serialize(root.GetProperty("tools"),
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            var read = input.FirstOrDefault(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                && item.GetProperty("call_id").GetString() == "lookup");
            if (Bodies.Count == 2) _originalReadTokens = CountInput(read);
            int[] recordedInputTokens = [36_991, 41_088, 41_970];
            int[] recordedOutputTokens = [21, 42, 4];
            var finalRound = rereadTarget ? 4 : 3;
            var recordedIndex = Math.Min(Bodies.Count - 1, 2);
            var inputTokens = recordedInputTokens[recordedIndex]
                - (Bodies.Count == finalRound ? _originalReadTokens - CountInput(read) : 0);
            var outputTokens = recordedOutputTokens[recordedIndex];
            var response = ResponsesTestData.Response(inputTokens: inputTokens, outputTokens: outputTokens,
                cachedTokens: cached && Bodies.Count > 1 ? 36_988 : 0);
            if (Bodies.Count == 1)
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("lookup", "get_checklist_templates", BinaryData.FromString("{}")));
            else if (rereadTarget && Bodies.Count == 2)
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("target", "get_checklist_templates",
                    BinaryData.FromObjectAsJson(new { template_id = template.Id })));
            else if (Bodies.Count == finalRound - 1)
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("edit", "update_checklist_template", BinaryData.FromObjectAsJson(new
                {
                    template_id = template.Id,
                    items = template.Items.Append("Step 11 of template 11")
                })));
            else if (toolCallAtCeiling)
                response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("unexecuted", "get_checklist_templates", BinaryData.FromString("{}")));
            else if (forceCeiling)
            {
                response.Status = ResponseStatus.Incomplete;
                response.IncompleteStatusDetails = new ResponseIncompleteStatusDetails { Reason = ResponseIncompleteStatusReason.MaxOutputTokens };
            }
            else
                response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Review the proposed step."));
            if (forceCeiling && Bodies.Count == 3)
            {
                response.Usage.InputTokenCount = AppConstants.MaxAiRequestTotalTokens + 1;
                response.Usage.TotalTokenCount = response.Usage.InputTokenCount + response.Usage.OutputTokenCount;
            }
            TotalTokens += response.Usage.TotalTokenCount;
            Console.WriteLine($"Replay call {Bodies.Count}: tools={toolTokens}, "
                + $"system={CountInput(input[0])}, context={CountInput(input[1])}, "
                + $"toolResults={input.Where(item => item.TryGetProperty("type", out var kind) && kind.GetString() == "function_call_output").Sum(CountInput)}, "
                + $"input={inputTokens}, output={outputTokens}, cached={response.Usage.InputTokenDetails.CachedTokenCount} "
                + $"(recorded provider usage, adjusted by removed read tokens), cumulative={TotalTokens}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ModelReaderWriter.Write(response).ToString(), Encoding.UTF8, "application/json")
            };
        }

        private int CountInput(JsonElement item)
        {
            if (item.TryGetProperty("content", out var content))
                return 8 + content.EnumerateArray().Sum(part => _tokenizer.CountTokens(part.GetProperty("text").GetString()!));
            if (item.TryGetProperty("output", out var output))
                return 8 + _tokenizer.CountTokens(output.GetString()!);
            return _tokenizer.CountTokens(item.GetRawText());
        }
    }
}
