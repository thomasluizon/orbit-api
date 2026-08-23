using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

[Collection("ProcessEnvironment")]
public class PayGateServiceExpiredCycleTests
{
    [Fact]
    public async Task TryConsumeAiMessage_FifthAllowedSixthRefusedThenNextLocalDateAllowed()
    {
        var userId = Guid.NewGuid();
        var today = new DateOnly(2026, 8, 5);
        var user = User.Create("Test User", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (var i = 0; i < 4; i++)
            user.IncrementAiMessageCount(today);

        var userRepository = Substitute.For<IGenericRepository<User>>();
        userRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(user);
        var appConfig = Substitute.For<IAppConfigService>();
        appConfig.GetAsync("FreeAiMessagesPerDay", 5, Arg.Any<CancellationToken>()).Returns(5);
        appConfig.GetAsync("ProAiMessagesPerDay", 50, Arg.Any<CancellationToken>()).Returns(50);
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>())
            .Returns(today, today, today.AddDays(1));
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var sut = new PayGateService(
            Substitute.For<IGenericRepository<Habit>>(),
            userRepository,
            appConfig,
            userDateService);

        var fifth = await sut.TryConsumeAiMessage(userId, unitOfWork);
        var sixth = await sut.TryConsumeAiMessage(userId, unitOfWork);
        var nextDay = await sut.TryConsumeAiMessage(userId, unitOfWork);

        fifth.IsSuccess.Should().BeTrue();
        sixth.IsFailure.Should().BeTrue();
        sixth.ErrorCode.Should().Be("PAY_GATE");
        nextDay.IsSuccess.Should().BeTrue();
        user.AiMessagesUsedToday.Should().Be(1);
        user.AiMessagesLocalDate.Should().Be(today.AddDays(1));
        await unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryConsumeAiMessage_OnNextLocalDate_ResetsCounterAndConsumesFirstMessage()
    {
        var userId = Guid.NewGuid();
        var user = User.Create("Test User", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        var today = new DateOnly(2026, 8, 5);
        for (var i = 0; i < 5; i++)
            user.IncrementAiMessageCount(today.AddDays(-1));

        var userRepository = Substitute.For<IGenericRepository<User>>();
        userRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(user);
        var appConfig = Substitute.For<IAppConfigService>();
        appConfig.GetAsync("FreeAiMessagesPerDay", 5, Arg.Any<CancellationToken>()).Returns(5);
        appConfig.GetAsync("ProAiMessagesPerDay", 50, Arg.Any<CancellationToken>()).Returns(50);
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var sut = new PayGateService(
            Substitute.For<IGenericRepository<Habit>>(),
            userRepository,
            appConfig,
            userDateService);

        var result = await sut.TryConsumeAiMessage(userId, unitOfWork);

        result.IsSuccess.Should().BeTrue();
        user.AiMessagesUsedToday.Should().Be(1);
        user.AiMessagesLocalDate.Should().Be(today);
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
