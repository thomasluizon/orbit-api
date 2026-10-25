using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Middleware;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    public static TheoryData<string, bool?, bool, bool> ModeratedClientCases
    {
        get
        {
            var cases = new TheoryData<string, bool?, bool, bool>();
            foreach (var language in new[] { "en", "pt-BR" })
                foreach (var supportsAllowance in new bool?[] { null, false, true })
                    foreach (var parts in new[] { false, true })
                        foreach (var crisis in new[] { false, true })
                            cases.Add(language, supportsAllowance, parts, crisis);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ModeratedClientCases))]
    public async Task ProcessChat_FlaggedInput_RealHandlerNegotiatesResponse(
        string language, bool? supportsAllowance, bool parts, bool crisis)
    {
        PrepareModeratedChatHandler(language, crisis);

        var result = await _controller.ProcessChat("screen this input", null, null, default,
            clientContext: ModeratedClientContext(supportsAllowance, parts));

        if (supportsAllowance != true && !crisis)
        {
            var error = result.Should().BeOfType<ObjectResult>().Subject;
            error.StatusCode.Should().Be(400);
            var body = error.Value.Should().BeOfType<ErrorResponse>().Subject;
            body.ErrorCode.Should().Be(ErrorCodes.ContentModerationRefused);
            body.Error.Should().Be(ModeratedReply(language, false));
            return;
        }
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var response = ok.Value.Should().BeOfType<ChatResponse>().Subject;
        response.AiMessage.Should().Be(ModeratedReply(language, crisis));
        response.MessageAllowanceConsumed.Should().Be(supportsAllowance == true ? false : null);
        response.Actions.Should().BeEmpty();
        var wire = JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        wire.TryGetProperty("messageAllowanceConsumed", out _).Should().Be(supportsAllowance == true);
        if (parts && !crisis)
            response.Parts.Should().Equal(new ChatTextPart(response.AiMessage!));
        else
            response.Parts.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(ModeratedClientCases))]
    public async Task ProcessChatStream_FlaggedInput_RealHandlerNegotiatesResponse(
        string language, bool? supportsAllowance, bool parts, bool crisis)
    {
        PrepareModeratedChatHandler(language, crisis);
        using var body = new MemoryStream();
        _controller.Response.Body = body;

        var result = await _controller.ProcessChatStream("screen this input", null, null, default,
            clientContext: ModeratedClientContext(supportsAllowance, parts));

        result.Should().BeOfType<EmptyResult>();
        _controller.Response.StatusCode.Should().Be(200);
        var events = System.Text.Encoding.UTF8.GetString(body.ToArray())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line["data: ".Length..])).ToArray();
        if (supportsAllowance != true && !crisis)
        {
            events.Select(item => item.GetProperty("type").GetString()).Should().Equal("started", "error");
            var error = events[1];
            error.GetProperty("status").GetInt32().Should().Be(400);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.ContentModerationRefused);
            error.GetProperty("error").GetString().Should().Be(ModeratedReply(language, false));
            error.TryGetProperty("response", out _).Should().BeFalse();
            return;
        }
        events.Select(item => item.GetProperty("type").GetString()).Should().Equal("started", "reset", "delta", "final");
        events[2].GetProperty("text").GetString().Should().Be(ModeratedReply(language, crisis));
        var response = events[3].GetProperty("response");
        response.GetProperty("aiMessage").GetString().Should().Be(ModeratedReply(language, crisis));
        response.TryGetProperty("messageAllowanceConsumed", out var consumed).Should().Be(supportsAllowance == true);
        if (supportsAllowance == true)
            consumed.GetBoolean().Should().BeFalse();
    }

    private static string? ModeratedClientContext(bool? supportsAllowance, bool parts) =>
        supportsAllowance is null && !parts ? null : JsonSerializer.Serialize(new
        {
            supportsMessageAllowance = supportsAllowance,
            supportsParts = parts
        });

    private static string ModeratedReply(string language, bool crisis)
    {
        if (!crisis)
            return language == "pt-BR"
                ? "Não posso ajudar com isso, mas posso ajudar com seus hábitos e metas."
                : "I can't help with that, but I can help with your habits and goals.";
        var locales = language == "pt-BR" ? CrisisLocales.Portuguese : CrisisLocales.English;
        return CrisisSupportGuard.EnsureResources(CrisisSupportGuard.FallbackMessage(locales), locales, null);
    }

    private void PrepareModeratedChatHandler(string language, bool crisis)
    {
        var user = User.Create("Person", "moderation@example.com").Value;
        user.SetLanguage(language);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(true, false, crisis ? ["self-harm/intent"] : ["violence"]));
        var handler = new ProcessUserChatCommandHandler(
            new ChatDataDependencies(Substitute.For<IGenericRepository<Habit>>(), Substitute.For<IGenericRepository<Goal>>(),
                users, Substitute.For<IGenericRepository<Tag>>(), Substitute.For<IGenericRepository<ChecklistTemplate>>(),
                Substitute.For<IFeatureFlagService>()),
            new ChatAiDependencies(Substitute.For<IAiIntentService>(), new AiToolRegistry([]),
                Substitute.For<ISystemPromptBuilder>(), Substitute.For<IAgentCatalogService>()),
            new ChatExecutionDependencies(Substitute.For<IUserDateService>(), Substitute.For<IUserStreakService>(),
                Substitute.For<IPayGateService>(), Substitute.For<IUnitOfWork>(), Substitute.For<IServiceScopeFactory>(),
                Substitute.For<IAgentOperationExecutor>(), Substitute.For<IPendingClarificationStore>(),
                Substitute.For<IGoalProgressReadSyncer>(), Substitute.For<IGamificationService>(), _mediator,
                Substitute.For<IProductAnalytics>(), Substitute.For<IHabitScheduleLogReader>(),
                Substitute.For<IIssuedAstraBlockStore>(), Substitute.For<IPendingAgentOperationStore>()),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), moderation);
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<ProcessUserChatCommand>(), call.Arg<CancellationToken>()));
    }
}
