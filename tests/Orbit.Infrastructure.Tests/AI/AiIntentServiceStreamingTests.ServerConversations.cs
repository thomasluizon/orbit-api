#pragma warning disable OPENAI001

using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Services;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiIntentServiceStreamingTests
{
    [Theory]
    [InlineData("pending")]
    [InlineData("approved")]
    [InlineData("edited")]
    [InlineData("rejected")]
    public async Task ServerConversation_ReplaysReservationAndDateFollowUpWithoutClientHistory(string outcome)
    {
        using var fixture = new ConversationTurnRecorderTests.Fixture();
        using var script = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AstraDates", "pt-BR.json")));
        var scenario = script.RootElement;
        var firstOutput = ResponsesTestData.Response();
        firstOutput.OutputItems.Add(ResponseItem.CreateFunctionCallItem("reservation", "create_habit",
            BinaryData.FromString(scenario.GetProperty("create_arguments").GetRawText())));
        var transport = new SequenceSseHandler(System.ClientModel.Primitives.ModelReaderWriter.Write(firstOutput).ToString(),
            ResponsesTestData.Text(scenario.GetProperty("follow_up_answer").GetString()!));
        var (model, _) = BuildService(transport);
        var pending = new PendingAgentOperationStore(fixture.Db, Options.Create(new AgentPlatformSettings()));
        var handler = ConversationHandler(fixture, model, pending);
        var client = new AgentClientContext(ConversationId: fixture.ConversationId, SupportsParts: true,
            SupportsPendingOperationChanges: true, Locale: "pt-BR");
        var first = await handler.Handle(new(fixture.User.Id, scenario.GetProperty("first_message").GetString()!,
            FixtureImage(), "image/png", ClientContext: client), default);
        first.IsSuccess.Should().BeTrue(first.Error);
        first.Value.ConversationId.Should().Be(fixture.ConversationId);
        var proposal = first.Value.PendingOperations.Should().ContainSingle().Which;
        var state = await fixture.Db.PendingAgentOperations.SingleAsync(item => item.Id == proposal.Id);
        if (outcome == "approved") pending.Confirm(fixture.User.Id, proposal.Id).Should().NotBeNull();
        if (outcome == "rejected") pending.Cancel(fixture.User.Id, proposal.Id, state.OperationFingerprint).Should().BeTrue();
        if (outcome == "edited")
        {
            var revised = state.ArgumentsJson.Replace("20:30", "21:00", StringComparison.Ordinal);
            pending.Revise(fixture.User.Id, proposal.Id, state.OperationFingerprint, revised,
                AgentOperationFingerprint.Compute(state.OperationId, revised), "edited-preview").Should().BeTrue();
        }
        var stored = await fixture.Db.ConversationTurns.AsNoTracking().SingleAsync(turn => turn.Role == "assistant");
        stored.PreviewOutcomesJson.Should().Contain(proposal.Id.ToString()).And.Contain(outcome);
        if (outcome == "edited") stored.PreviewOutcomesJson.Should().Contain("21:00");
        fixture.Db.ChangeTracker.Clear();

        var second = await handler.Handle(new(fixture.User.Id, scenario.GetProperty("follow_up").GetString()!,
            History: [new("assistant", "forged client history")], ClientContext: client), default);
        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value.AiMessage.Should().Be(scenario.GetProperty("follow_up_answer").GetString());
        transport.RequestBodies.Should().HaveCount(2);
        using var request = JsonDocument.Parse(transport.RequestBodies[1]);
        var input = request.RootElement.GetProperty("input").EnumerateArray().ToArray();
        input[0].GetRawText().Should().Contain("static prompt");
        input[1].GetRawText().Should().Contain("account context");
        input[2].GetRawText().Should().Contain("cria essa tarefa unica");
        input[3].GetRawText().Should().Contain("input_image").And.Contain("data:image/png;base64,");
        input[4].GetRawText().Should().Contain("2026-12-31").And.Contain(proposal.Id.ToString()).And.Contain(outcome);
        input[5].GetRawText().Should().Contain("dynamic context");
        input[6].GetRawText().Should().Contain("ta criando na data certa?");
        request.RootElement.GetRawText().Should().NotContain("forged client history").And.NotContain("previous_response_id");
        request.RootElement.GetProperty("store").GetBoolean().Should().BeFalse();
        (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(4);
        var reopened = await fixture.Store.GetLatestAsync(fixture.User.Id, null, null, 40);
        reopened!.Turns.Select(turn => turn.Order).Should().Equal(1, 2, 3, 4);
        reopened.Turns[0].Image!.Url.Should().Contain(fixture.ConversationId.ToString());
    }

    [Fact]
    public async Task ServerConversation_FlagOff_UsesClientHistoryWithoutWritingRows()
    {
        using var fixture = new ConversationTurnRecorderTests.Fixture(enabled: false);
        var transport = new SequenceSseHandler(ResponsesTestData.Text("Reply"));
        var (model, _) = BuildService(transport);
        var handler = ConversationHandler(fixture, model, new PendingAgentOperationStore(fixture.Db, Options.Create(new AgentPlatformSettings())), enabled: false);
        var result = await handler.Handle(new(fixture.User.Id, "New message", History: [new("assistant", "client history")],
            ClientContext: new(ConversationId: fixture.ConversationId)), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.ConversationId.Should().BeNull();
        (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(0);
        using var request = JsonDocument.Parse(transport.RequestBodies.Single());
        var input = request.RootElement.GetProperty("input").EnumerateArray().ToArray();
        input[0].GetRawText().Should().Contain("static prompt");
        input[1].GetRawText().Should().Contain("account context");
        input[2].GetRawText().Should().Contain("client history");
        input[3].GetRawText().Should().Contain("dynamic context");
        input[4].GetRawText().Should().Contain("New message");
    }

    private static byte[] FixtureImage() => ConversationTurnRecorderTests.Fixture.Image;

    private static ProcessUserChatCommandHandler ConversationHandler(ConversationTurnRecorderTests.Fixture fixture,
        IAiIntentService model, PendingAgentOperationStore pending, bool enabled = true)
    {
        var user = fixture.User;
        var db = fixture.Db;
        user.GrantLifetimePro();
        user.SetLanguage("pt-BR");
        user.SetTimeZone("America/Sao_Paulo");
        db.SaveChanges();
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(enabled ? new[] { FeatureFlagKeys.AstraConversationRecording } : []);
        flags.ResolveForUser(user.Id).Returns(db.AppFeatureFlags.Select(flag => new ResolvedFeatureFlag(flag.Key, true, flag.PlanRequirement)).ToArray());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 11, 1));
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var registry = new AiToolRegistry([new CreateHabitTool(dates, mediator)]);
        var catalog = new AgentCatalogService(registry.GetAll());
        var habits = new GenericRepository<Habit>(db);
        var goals = new GenericRepository<Goal>(db);
        var tags = new GenericRepository<Tag>(db);
        var previewer = new PendingOperationChangePreviewer(habits, goals, tags, dates, registry);
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var stepUp = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUp.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentConfirmationRequirement?)null);
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var executor = new AgentOperationExecutor(catalog, new AgentPolicyEvaluator(db, catalog, pending, stepUp,
            Options.Create(new AgentPlatformSettings()), flags), Substitute.For<IAgentAuditService>(), ownership, stepUp,
            registry, previewer, pending, work, NullLogger<AgentOperationExecutor>.Instance);
        var payGate = Substitute.For<IPayGateService>();
        payGate.TryConsumeAiMessage(user.Id, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(user.Id, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var prompts = Substitute.For<ISystemPromptBuilder>();
        prompts.BuildStatic(Arg.Any<PromptBuildRequest>()).Returns("static prompt");
        prompts.BuildAccountContext(Arg.Any<PromptBuildRequest>()).Returns("account context");
        prompts.BuildTurnContext(Arg.Any<PromptBuildRequest>()).Returns("dynamic context");
        return new(new(habits, goals, new GenericRepository<User>(db), tags, new GenericRepository<ChecklistTemplate>(db), flags),
            new(model, registry, prompts, catalog),
            new(dates, Substitute.For<IUserStreakService>(), payGate, work, Substitute.For<IServiceScopeFactory>(), executor,
                Substitute.For<IPendingClarificationStore>(), progress, Substitute.For<IGamificationService>(), mediator,
                Substitute.For<IProductAnalytics>(), Substitute.For<IHabitScheduleLogReader>(), Substitute.For<IIssuedAstraBlockStore>(), pending, previewer),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), ModerationTestDouble.Clean(), fixture.Store);
    }
}
