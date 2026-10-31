#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public sealed partial class ProviderToolControlOrderTests
{
    [Fact]
    public async Task ChatRequests_ShareStaticPrefixAcrossUsersOffersPreviewsAndHistory()
    {
        var plain = await CaptureChatRequestAsync(false);
        var offers = await CaptureChatRequestAsync(true);
        using var first = JsonDocument.Parse(plain);
        using var second = JsonDocument.Parse(offers);
        var firstSystem = first.RootElement.GetProperty("input")[0].GetRawText();
        var secondSystem = second.RootElement.GetProperty("input")[0].GetRawText();
        var firstTools = first.RootElement.GetProperty("tools").GetRawText();
        var secondTools = second.RootElement.GetProperty("tools").GetRawText();

        Encoding.UTF8.GetBytes(secondSystem).Should().Equal(Encoding.UTF8.GetBytes(firstSystem));
        Encoding.UTF8.GetBytes(secondTools).Should().Equal(Encoding.UTF8.GetBytes(firstTools));
        first.RootElement.GetProperty("prompt_cache_key").GetString().Should()
            .Be(second.RootElement.GetProperty("prompt_cache_key").GetString());
        first.RootElement.GetProperty("safety_identifier").GetString().Should()
            .NotBe(second.RootElement.GetProperty("safety_identifier").GetString());
        second.RootElement.GetProperty("input").GetRawText().Should().Contain("pending_operation_id", "previously_issued_offers", "Earlier conversation");
        first.RootElement.GetProperty("input").GetRawText().Should().NotContain("pending_operation_id", "previously_issued_offers");
        var prefixCharacters = firstSystem.Length + firstTools.Length;
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "orbit-1916-prefix-size.txt"),
            $"Serialized system and tools: {prefixCharacters} characters, approximately {prefixCharacters / 4} tokens (characters / 4).");
    }

    private static async Task<string> CaptureChatRequestAsync(bool offers)
    {
        var registry = new AiToolRegistry(StrictChatToolSchemaTests.AllTools());
        var http = new PrefixCaptureHandler();
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(new ResponsesClient(new System.ClientModel.ApiKeyCredential("test-key"),
            new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(http))
            }), NullLogger<AiCompletionClient>.Instance, usage);
        var intent = new AiIntentService(AiBudgetTestDouble.Wrap(client), usage, NullLogger<AiIntentService>.Instance);
        var user = User.Create("Person", "prefix@example.com").Value;
        user.SetLanguage(offers ? "pt-BR" : "en");
        var parent = Habit.Create(new(HabitSchedule.Recurring, user.Id, offers ? "Rotina" : "Routine", FrequencyUnit.Day, 1, new DateOnly(2026, 1, 1))).Value;
        var child = Habit.Create(new(HabitSchedule.Recurring, user.Id, offers ? "Rotina" : "Child", FrequencyUnit.Day, 1, new DateOnly(2026, 1, 1), ParentHabitId: parent.Id)).Value;
        var habits = offers ? new[] { parent, child } : Array.Empty<Habit>();
        var pending = Substitute.For<IPendingAgentOperationStore>();
        pending.GetAwaiting(user.Id).Returns(offers
            ? new[] { new PendingAgentOperationExecution(Guid.NewGuid(), "habits", "create_habit", JsonSerializer.SerializeToElement(new { title = "Draft" }), AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation) }
            : Array.Empty<PendingAgentOperationExecution>());
        var preview = Substitute.For<IPendingOperationChangePreviewer>();
        preview.PreviewAsync(user.Id, Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>(), Arg.Any<AgentClientContext?>())
            .Returns(Result.Success<PendingOperationChangePreview?>(new([], 1, [])));
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(Arg.Any<Guid>(), Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var execution = new ChatExecutionDependencies(Substitute.For<IUserDateService>(), null!, quota,
            Substitute.For<IUnitOfWork>(), null!, null!, null!, null!, null!, null!, null!, null!, null!, pending, preview);
        var handler = new ProcessUserChatCommandHandler(null!, new(intent, registry, new SystemPromptBuilder(), new AgentCatalogService()),
            execution, NullLogger<ProcessUserChatCommandHandler>.Instance, null!, null!);
        var handlerType = typeof(ProcessUserChatCommandHandler);
        var facts = Activator.CreateInstance(handlerType.Assembly.GetType("Orbit.Application.Chat.HabitTodaySnapshot", true)!,
            new object?[] { new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), null });
        var context = Activator.CreateInstance(handlerType.GetNestedType("ChatContext", BindingFlags.NonPublic)!,
            new object?[] { habits, habits.ToList(), habits.ToList(), false, user, user.Language, true,
                Array.Empty<Goal>(), Array.Empty<Tag>(), Array.Empty<string>(), offers ? new[] { FeatureFlagKeys.AstraBlockFirst } : Array.Empty<string>(),
                new DateOnly(2026, 1, 1), facts, 0L });
        var request = new ProcessUserChatCommand(user.Id, offers ? "Continue, por favor" : "Hello",
            History: offers ? [new("user", "Earlier conversation"), new("assistant", "Earlier reply")] : null,
            ClientContext: new(SupportsParts: offers, PartKinds: ["choice"], SupportsPendingOperationChanges: offers,
                SupportsProactiveOffers: offers, ConversationId: Guid.NewGuid()));
        var state = Activator.CreateInstance(handlerType.GetNestedType("ChatTurnState", BindingFlags.NonPublic)!, nonPublic: true);
        var result = await (Task<Result<AiResponse>>)handlerType.GetMethod("RequestInitialAiResponseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(handler, [request, context, null, new AiTurnTelemetry(), state, CancellationToken.None])!;
        result.IsSuccess.Should().BeTrue(result.Error);
        return http.Body!;
    }

    private sealed class PrefixCaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(ResponsesTestData.Text("Hello"), Encoding.UTF8, "application/json") };
        }
    }
}
