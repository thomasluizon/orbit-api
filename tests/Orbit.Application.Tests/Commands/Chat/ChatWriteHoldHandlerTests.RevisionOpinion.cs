using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", false, "Start small and adjust as it feels easier. Keep it comfortable.", "Start small and adjust as it feels easier.")]
    [InlineData("en", true, "Start small and adjust as it feels easier. Keep it comfortable.", "Start small and adjust as it feels easier.")]
    [InlineData("pt-BR", false, "Comece devagar e ajuste conforme ficar mais fácil. Mantenha confortável.", "Comece devagar e ajuste conforme ficar mais fácil.")]
    [InlineData("pt-BR", true, "Comece devagar e ajuste conforme ficar mais fácil. Mantenha confortável.", "Comece devagar e ajuste conforme ficar mais fácil.")]
    [InlineData("en", true, "Try 10 pages first.", null)]
    [InlineData("pt-BR", true, "Tente 10 páginas primeiro.", null)]
    public async Task RevisionQuestion_UsesContinuationWhenToolRoundHasNoAnswer(
        string language, bool parts, string answer, string? expected)
    {
        _user.SetLanguage(language);
        ScriptToolCall("""{"title":"Read","frequency_unit":"Day","due_time":"21:00"}""");
        var first = await CreateHandler().Handle(new(_userId, "Create a reading habit"), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        ScriptToolCall(JsonSerializer.Serialize(new
        {
            pending_operation_id = pending.Id,
            items = new[] { new { item_id = pending.Items!.Single().ItemId, edits_json = "{\"due_time\":\"22:00\"}" } },
            orbit_read_complete = true,
            answer_question = true
        }), "revise_pending_changes");
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var toolResult = call.Arg<IReadOnlyList<AiToolCallResult>>().Should().ContainSingle().Which;
            JsonSerializer.SerializeToElement(toolResult.Payload).GetProperty("instruction").GetString()
                .Should().StartWith("Answer the person's question");
            return Result.Success(new AiResponse { TextMessage = answer });
        });
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler().Handle(new(_userId, language == "en"
                ? "Move it to 22:00 and explain how to keep it manageable."
                : "Mude para 22:00 e explique como manter a rotina leve.",
            ClientContext: new(SupportsParts: parts, SupportsPendingOperationChanges: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Be(expected);
        if (parts && expected is not null)
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(expected);
        else
            result.Value.Parts.Should().BeNullOrEmpty();
        events.Where(item => item.Type == "delta").Select(item => item.Text).Should()
            .Equal(expected is null ? [] : [expected]);
        result.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(pending.Id);
        _pendingOperationStore.IsAwaitingConfirmation(_userId, pending.Id).Should().BeTrue();
    }

    [Theory]
    [InlineData("en", false, true)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", false, true)]
    [InlineData("pt-BR", true, true)]
    [InlineData("en", false, false)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", false, false)]
    [InlineData("pt-BR", true, false)]
    public async Task RevisionOpinion_ProviderReplayKeepsAnswerAndOnlyChangesTime(
        string language, bool composition, bool question)
    {
        _user.SetLanguage(language);
        if (composition)
            _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
                .Returns([FeatureFlagKeys.AstraBlockFirst]);
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "PendingRevisions", language + ".json")));
        var capture = fixture.RootElement;
        using var transport = new RevisionOpinionResponseHandler(capture.GetProperty("responses").Clone(), question);
        using var http = new HttpClient(transport);
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(Options.Create(new AiSettings { ApiKey = "test-key", MaxRetries = 0 }),
            NullLogger<AiCompletionClient>.Instance, usage, new HttpClientPipelineTransport(http));
        var budgeted = new BudgetedAiCompletionClient(client, Substitute.For<IAiDailyBudget>(), new HttpContextAccessor(),
            NullLogger<BudgetedAiCompletionClient>.Instance, Substitute.For<IAiRequestCostEstimator>());
        var adapter = new AiIntentService(budgeted, usage, NullLogger<AiIntentService>.Instance);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(call => adapter.SendWithToolsAsync(
                call.Arg<AiToolRequest>(), null, call.Arg<CancellationToken>()));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call => adapter.ContinueWithToolResultsAsync(
                call.Arg<AiConversationContext>(), call.Arg<IReadOnlyList<AiToolCallResult>>(), null, call.Arg<CancellationToken>()));
        var handler = CreateHandler();
        var clientContext = new AgentClientContext(SupportsParts: true, SupportsPendingOperationChanges: true);
        var createMessage = capture.GetProperty("create_message").GetString()!;

        var first = await handler.Handle(new(_userId, createMessage, ClientContext: clientContext), default);

        first.IsSuccess.Should().BeTrue(first.Error);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        var before = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        transport.PendingId = pending.Id;
        transport.ItemId = pending.Items!.Single().ItemId;
        var events = new List<ChatStreamEvent>();
        var message = question ? capture.GetProperty("revision_message").GetString()!
            : language == "en" ? "Make it 22:00 instead." : "Mude para 22:00.";

        var result = await handler.Handle(new(_userId, message,
            History: [new("user", createMessage), new("assistant", JsonSerializer.Serialize(first.Value, ChatStreamEvent.SerializerOptions))],
            ClientContext: clientContext, StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        transport.Requests.Should().Be(4);
        var revised = result.Value.PendingOperations.Should().ContainSingle().Which;
        revised.Id.Should().Be(pending.Id);
        revised.PreviewFingerprint.Should().NotBe(pending.PreviewFingerprint);
        var after = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        after.OperationId.Should().Be(before.OperationId);
        after.ExpiresAtUtc.Should().Be(before.ExpiresAtUtc);
        after.Arguments.GetProperty("due_time").GetString().Should().Be("22:00");
        foreach (var property in before.Arguments.EnumerateObject().Where(property => property.Name != "due_time"))
            after.Arguments.GetProperty(property.Name).GetRawText().Should().Be(property.Value.GetRawText(), property.Name);
        after.Arguments.EnumerateObject().Select(property => property.Name).Should()
            .BeEquivalentTo(before.Arguments.EnumerateObject().Select(property => property.Name));
        foreach (var field in pending.Items!.Single().Fields.Where(field => field.Field != "due_time"))
            JsonSerializer.Serialize(revised.Items!.Single().Fields.Single(current => current.Field == field.Field))
                .Should().Be(JsonSerializer.Serialize(field), field.Field);
        _pendingOperationStore.IsAwaitingConfirmation(_userId, pending.Id).Should().BeTrue();
        _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle();
        result.Value.Actions.Should().BeEmpty();
        if (question)
        {
            var answer = capture.GetProperty("answer").GetString();
            result.Value.AiMessage.Should().Be(answer);
            result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(answer);
            events.Where(item => item.Type == "delta").Select(item => item.Text).Should().Equal(answer);
        }
        else
        {
            result.Value.AiMessage.Should().BeNullOrEmpty();
            result.Value.Parts.Should().BeEmpty();
            events.Should().NotContain(item => item.Type == "delta");
        }
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "pt-BR", false)]
    [InlineData("pt-BR", "en", false)]
    [InlineData("en", "pt-BR", true)]
    [InlineData("pt-BR", "en", true)]
    public async Task FailedRevision_ProviderReplayUsesRequestLocale(string language, string profileLanguage, bool composition)
    {
        _user.SetLanguage(profileLanguage);
        if (composition)
            _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
                .Returns([FeatureFlagKeys.AstraBlockFirst]);
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "PendingRevisions", language + ".json")));
        var capture = fixture.RootElement;
        using var transport = new RevisionOpinionResponseHandler(capture.GetProperty("responses").Clone(), false);
        using var http = new HttpClient(transport);
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(Options.Create(new AiSettings { ApiKey = "test-key", MaxRetries = 0 }),
            NullLogger<AiCompletionClient>.Instance, usage, new HttpClientPipelineTransport(http));
        var budgeted = new BudgetedAiCompletionClient(client, Substitute.For<IAiDailyBudget>(), new HttpContextAccessor(),
            NullLogger<BudgetedAiCompletionClient>.Instance, Substitute.For<IAiRequestCostEstimator>());
        var adapter = new AiIntentService(budgeted, usage, NullLogger<AiIntentService>.Instance);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(call => adapter.SendWithToolsAsync(
                call.Arg<AiToolRequest>(), null, call.Arg<CancellationToken>()));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call => adapter.ContinueWithToolResultsAsync(
                call.Arg<AiConversationContext>(), call.Arg<IReadOnlyList<AiToolCallResult>>(), null, call.Arg<CancellationToken>()));
        var handler = CreateHandler();
        var clientContext = new AgentClientContext(Locale: language, SupportsParts: true, SupportsPendingOperationChanges: true);
        var first = await handler.Handle(new(_userId, capture.GetProperty("create_message").GetString()!, ClientContext: clientContext), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        var before = _pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments.GetRawText();
        transport.PendingId = pending.Id;
        transport.ItemId = pending.Items!.Single().ItemId;
        transport.RejectRevision = true;
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(_userId, language == "en" ? "Move it to an invalid time." : "Mude para um horário inválido.",
            History: [new("assistant", JsonSerializer.Serialize(first.Value, ChatStreamEvent.SerializerOptions))],
            ClientContext: clientContext, StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        transport.Requests.Should().Be(3);
        result.Value.Operations.Should().ContainSingle().Which.PolicyReason.Should().Be("invalid_revision");
        var expected = language == "en" ? "I couldn't complete that. Please try again." : "Não consegui concluir isso. Tente novamente.";
        result.Value.AiMessage.Should().Be(expected);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(expected);
        events.Where(item => item.Type == "delta").Should().OnlyContain(item => item.Text == expected);
        _pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments.GetRawText().Should().Be(before);
        _pendingOperationStore.IsAwaitingConfirmation(_userId, pending.Id).Should().BeTrue();
    }

    [Theory]
    [InlineData("en", false, false)]
    [InlineData("pt-BR", false, false)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", true, false)]
    [InlineData("en", false, true)]
    [InlineData("pt-BR", false, true)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", true, true)]
    public async Task FreshCreate_ProviderReplayRecoversMisroutedRevisionWithoutChangingOlderPreview(string language, bool composition, bool history)
    {
        _user.SetLanguage("en");
        if (composition)
            _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
                .Returns([FeatureFlagKeys.AstraBlockFirst]);
        ScriptToolCall(JsonSerializer.Serialize(new { title = language == "en" ? "Read 10 pages" : "Read 20 pages", frequency_unit = "Day", due_time = "21:00" }));
        var first = await CreateHandler().Handle(new(_userId, "Create a daily reading habit",
            ClientContext: new(Locale: "en", SupportsParts: true, SupportsPendingOperationChanges: true)), default);
        var old = first.Value.PendingOperations.Should().ContainSingle().Which;
        var before = _pendingOperationStore.GetExecution(_userId, old.Id)!.Arguments.GetRawText();
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "PendingRevisions", language + ".json")));
        var capture = fixture.RootElement;
        using var transport = new RevisionOpinionResponseHandler(capture.GetProperty("responses").Clone(), false, 2)
        { PendingId = old.Id, ItemId = old.Items!.Single().ItemId, RejectRevision = true, RecoverCreate = true };
        using var http = new HttpClient(transport);
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(Options.Create(new AiSettings { ApiKey = "test-key", MaxRetries = 0 }),
            NullLogger<AiCompletionClient>.Instance, usage, new HttpClientPipelineTransport(http));
        var budgeted = new BudgetedAiCompletionClient(client, Substitute.For<IAiDailyBudget>(), new HttpContextAccessor(),
            NullLogger<BudgetedAiCompletionClient>.Instance, Substitute.For<IAiRequestCostEstimator>());
        var adapter = new AiIntentService(budgeted, usage, NullLogger<AiIntentService>.Instance);
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(call => adapter.SendWithToolsAsync(
                call.Arg<AiToolRequest>(), null, call.Arg<CancellationToken>()));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call => adapter.ContinueWithToolResultsAsync(
                call.Arg<AiConversationContext>(), call.Arg<IReadOnlyList<AiToolCallResult>>(), null, call.Arg<CancellationToken>()));

        var result = await CreateHandler().Handle(new(_userId, capture.GetProperty("create_message").GetString()!,
            History: history ? [new("user", "Hello"), new("assistant", "Hello, how can I help?")] : null,
            ClientContext: new(Locale: language, SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Id.Should().NotBe(old.Id);
        pending.Items.Should().ContainSingle().Which.EntityName.Should().Be(language == "en" ? "Read 20 pages" : "Ler 20 páginas");
        _pendingOperationStore.GetExecution(_userId, old.Id)!.Arguments.GetRawText().Should().Be(before);
        _pendingOperationStore.GetAwaiting(_userId).Should().HaveCount(2);
        result.Value.Operations.Should().NotContain(operation => operation.Status == AgentOperationStatus.Failed);
        result.Value.Actions.Should().BeEmpty();
        transport.Requests.Should().Be(4);
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    private sealed class RevisionOpinionResponseHandler(JsonElement responses, bool question, int initialRequests = 0) : HttpMessageHandler
    {
        public Guid PendingId { get; set; }
        public string ItemId { get; set; } = string.Empty;
        public bool RejectRevision { get; set; }
        public bool RecoverCreate { get; set; }
        public int Requests { get; private set; } = initialRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Requests >= responses.GetArrayLength())
                throw new InvalidOperationException("The replay requested an unexpected model round.");
            var index = RecoverCreate && Requests == 3 ? 0 : Requests;
            Requests++;
            var response = responses[index].GetRawText()
                .Replace("{{pending_id}}", PendingId.ToString(), StringComparison.Ordinal)
                .Replace("{{item_id}}", ItemId, StringComparison.Ordinal);
            if (RejectRevision)
            {
                var envelope = System.Text.Json.Nodes.JsonNode.Parse(response)!;
                foreach (var output in envelope["output"]!.AsArray())
                    if (output?["name"]?.GetValue<string>() == "revise_pending_changes")
                    {
                        var arguments = System.Text.Json.Nodes.JsonNode.Parse(output["arguments"]!.GetValue<string>())!;
                        arguments["items"]![0]!["edits_json"] = "{\"due_time\":\"invalid\"}";
                        output["arguments"] = arguments.ToJsonString();
                    }
                response = envelope.ToJsonString();
            }
            if (!question)
                response = response.Replace("\\\"answer_question\\\":true", "\\\"answer_question\\\":false", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
