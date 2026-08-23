using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

[Collection("ProcessEnvironment")]
public class PayGateServiceExpiredCycleTests
{
    [Fact]
    public async Task TryConsumeAiMessage_BackwardLocalDateAfterTimezoneChange_RemainsBounded()
    {
        var userId = Guid.NewGuid();
        var today = new DateOnly(2026, 8, 5);
        var user = User.Create("Test User", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (var i = 0; i < 5; i++)
            user.IncrementAiMessageCount(today.AddDays(1));

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

        var results = new List<Result>();
        for (var i = 0; i < 6; i++)
            results.Add(await sut.TryConsumeAiMessage(userId, unitOfWork));

        results.Count(result => result.IsSuccess).Should().Be(0);
        results.Count(result => result.IsFailure && result.ErrorCode == "PAY_GATE").Should().Be(6);
        user.AiMessagesUsedToday.Should().Be(5);
        user.AiMessagesLocalDate.Should().Be(today.AddDays(1));
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryConsumeAiMessage_AlternatingLocalDates_DoesNotMintThirdAllowance()
    {
        var userId = Guid.NewGuid();
        var firstDate = new DateOnly(2026, 8, 5);
        var secondDate = firstDate.AddDays(1);
        var user = User.Create("Test User", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        for (var i = 0; i < 5; i++)
            user.IncrementAiMessageCount(firstDate);

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
            .Returns(secondDate, secondDate, secondDate, secondDate, secondDate, firstDate);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var sut = new PayGateService(
            Substitute.For<IGenericRepository<Habit>>(),
            userRepository,
            appConfig,
            userDateService);

        var secondDateResults = new List<Result>();
        for (var i = 0; i < 5; i++)
            secondDateResults.Add(await sut.TryConsumeAiMessage(userId, unitOfWork));
        var returnToFirstDate = await sut.TryConsumeAiMessage(userId, unitOfWork);

        secondDateResults.Should().OnlyContain(result => result.IsSuccess);
        returnToFirstDate.IsFailure.Should().BeTrue();
        returnToFirstDate.ErrorCode.Should().Be("PAY_GATE");
        user.AiMessagesUsedToday.Should().Be(5);
        user.AiMessagesLocalDate.Should().Be(secondDate);
        await unitOfWork.Received(5).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

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
