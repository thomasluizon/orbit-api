using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ConversationOfferSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionResolvesAnExactKeyOutsideBoundedHistory(bool reissue)
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.IsConfigured.Returns(true);
        encryption.Encrypt(Arg.Any<string>()).Returns(call => "cipher:" + call.Arg<string>());
        encryption.Decrypt(Arg.Any<string>()).Returns(call => call.Arg<string>()[7..]);
        using var factory = new SqliteOrbitDbContextFactory(encryption);
        var user = User.Create("Offer User", "selection@example.com").Value;
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var conversation = Guid.NewGuid();
        var today = new DateOnly(2026, 10, 30);
        var source = new ReadBlockMetadata(Guid.NewGuid(), "query_habits", JsonSerializer.SerializeToElement(new { date = "today" }),
            "revision", "complete", "refresh-token");
        var store = new ConversationOfferStore(factory.Context, encryption);
        var facts = Enumerable.Range(0, 200).Select(index =>
        {
            var key = "smallStep:" + index;
            return new AstraOfferFact(key, "smallStep", "Empty day", new(key, "continueChat", "Choose one small step",
                Prompt: "Help me choose one small step I can take today."), source.QueryId, source.Revision, today)
            { Source = source, SourceKind = "habitList" };
        }).ToArray();
        foreach (var fact in facts) await store.IssueAsync(user.Id, conversation, fact, default);
        (await factory.Context.ConversationOffers.CountAsync()).Should().Be(200);
        var history = await store.IssuedAsync(user.Id, conversation, default);
        history.Should().HaveCount(100);
        var selected = facts.First(fact => history.All(issued => issued.Action.OfferKey != fact.Action.OfferKey));
        if (reissue) await store.IssueAsync(user.Id, conversation, selected, default);
        (await store.IssuedAsync(user.Id, conversation, default)).Should().NotContain(fact => fact.Action.OfferKey == selected.Action.OfferKey);
        (await store.DismissedAsync(user.Id, conversation, default)).Should().BeEmpty();

        using var fresh = factory.CreateContext();
        var reloaded = new ConversationOfferStore(fresh, encryption);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<RefreshReadBlockQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new QueryReadBlock("habitList", new HabitListCard("today", []), source)));
        var intent = Substitute.For<IAiIntentService>();
        intent.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Ready." }));
        var handler = Handler(fresh, reloaded, user.Id, today, mediator, intent);
        var client = new AgentClientContext(SupportsParts: true, SupportsPendingOperationChanges: true,
            SupportsProactiveOffers: true, ConversationId: conversation, SelectedOfferKey: selected.Action.OfferKey);
        var result = await handler.Handle(new(user.Id, "Select this action.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await mediator.Received(1).Send(Arg.Is<RefreshReadBlockQuery>(query => query.UserId == user.Id
            && query.Token == source.RefreshToken), Arg.Any<CancellationToken>());
        intent.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "SendWithToolsAsync"
            && ((AiToolRequest)call.GetArguments()[0]!).UserMessage == selected.Action.Prompt);
        (await reloaded.DismissedAsync(user.Id, conversation, default)).Should().Contain(selected.Action.OfferKey);
        await reloaded.IssueAsync(user.Id, conversation, selected, default);
        (await handler.Handle(new(user.Id, "Select this action.", ClientContext: client), default)).Error.Should().Be("stale_offer");
    }

    private static ProcessUserChatCommandHandler Handler(OrbitDbContext db, IConversationOfferStore store, Guid userId,
        DateOnly today, IMediator mediator, IAiIntentService intent)
    {
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        var progress = Substitute.For<IGoalProgressReadSyncer>();
        progress.ComputeFreshValuesAsync(userId, today, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        var payGate = Substitute.For<IPayGateService>();
        payGate.TryConsumeAiMessage(userId, work, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ModerationResult(false, false, []));
        return new(
            new(new GenericRepository<Habit>(db), new GenericRepository<Goal>(db), new GenericRepository<User>(db),
                new GenericRepository<Tag>(db), new GenericRepository<ChecklistTemplate>(db), Substitute.For<IFeatureFlagService>()),
            new(intent, new AiToolRegistry([]), Substitute.For<ISystemPromptBuilder>(), Substitute.For<IAgentCatalogService>()),
            new(dates, Substitute.For<IUserStreakService>(), payGate, work, Substitute.For<IServiceScopeFactory>(),
                Substitute.For<IAgentOperationExecutor>(), Substitute.For<IPendingClarificationStore>(), progress,
                Substitute.For<IGamificationService>(), mediator, Substitute.For<IProductAnalytics>(),
                Substitute.For<IHabitScheduleLogReader>(), Substitute.For<IIssuedAstraBlockStore>(), Substitute.For<IPendingAgentOperationStore>()),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), moderation, offerStore: store);
    }
}
