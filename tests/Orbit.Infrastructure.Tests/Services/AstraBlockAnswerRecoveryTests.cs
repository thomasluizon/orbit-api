using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class AstraBlockAnswerRecoveryTests
{
    [Fact]
    public async Task FailureCompensatesBothClaimAndQuota_ThenSuccessIsPersistedAndReplayed()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Alex", "retry@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var block = IssuedAstraBlock.Create(user.Id, new("choice", "When?", [new("a", "Morning"), new("b", "Evening")], []));
        var store = new IssuedAstraBlockStore(db);
        await store.AddAsync(block, default);
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var date = new DateOnly(2026, 4, 3);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(date);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.FreeAiMessagesPerDay, AppConstants.DefaultFreeAiMessages, Arg.Any<CancellationToken>()).Returns(5);
        config.GetAsync(AppConfigKeys.ProAiMessagesPerDay, AppConstants.DefaultProAiMessages, Arg.Any<CancellationToken>()).Returns(50);
        var users = new GenericRepository<User>(db);
        var quota = new PayGateService(new GenericRepository<Habit>(db), users, config, dates);
        var service = new AstraBlockAnswerService(store, work, quota, Substitute.For<IAgentOperationExecutor>(),
            users, Substitute.For<IProductAnalytics>(), Substitute.For<IFeatureFlagService>(), Substitute.For<IPendingAgentOperationStore>());
        var request = new ProcessUserChatCommand(user.Id, "Evening", BlockAnswer: new(block.Id, ["b"]));

        var failure = await service.ExecuteAsync(request,
            _ => Task.FromResult(Result.Failure<ChatResponse>(ErrorMessages.AiUnavailable)), default);
        failure.IsFailure.Should().BeTrue();
        (await store.GetAsync(block.Id, user.Id, default))!.AnsweredAtUtc.Should().BeNull();
        (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id)).GetAiMessagesUsedToday(date).Should().Be(0);

        var reply = new ChatResponse("Evening works.", [], Parts: [new ChatTextPart("Evening works.")]);
        var success = await service.ExecuteAsync(request, _ => Task.FromResult(Result.Success(reply)), default);
        success.IsSuccess.Should().BeTrue();
        var stored = await store.GetAsync(block.Id, user.Id, default);
        stored!.MatchesAnswer(request.BlockAnswer!).Should().BeTrue();
        stored.ResponseJson.Should().NotBeNull();
        stored.ReservedAllowanceDate.Should().Be(date);
        var replay = await service.ExecuteAsync(request, _ => throw new InvalidOperationException("Replay must not call AI"), default);
        replay.Value.Should().BeEquivalentTo(reply);
        (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id)).GetAiMessagesUsedToday(date).Should().Be(1);
    }
}
