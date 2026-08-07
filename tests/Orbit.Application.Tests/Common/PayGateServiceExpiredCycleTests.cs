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
    public async Task TryConsumeAiMessage_AtExpiredCycleLimit_ResetsCycleAndConsumesFirstMessage()
    {
        var userId = Guid.NewGuid();
        var user = User.Create("Test User", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        var previousCycleNow = DateTime.UtcNow.AddDays(-31);
        for (var i = 0; i < 20; i++)
            user.IncrementAiMessageCount(previousCycleNow);

        var userRepository = Substitute.For<IGenericRepository<User>>();
        userRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(user);
        var appConfig = Substitute.For<IAppConfigService>();
        appConfig.GetAsync("FreeAiMessagesPerMonth", 20, Arg.Any<CancellationToken>()).Returns(20);
        appConfig.GetAsync("ProAiMessagesPerMonth", 500, Arg.Any<CancellationToken>()).Returns(500);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var sut = new PayGateService(
            Substitute.For<IGenericRepository<Habit>>(),
            userRepository,
            appConfig);

        var result = await sut.TryConsumeAiMessage(userId, unitOfWork);

        result.IsSuccess.Should().BeTrue();
        user.AiMessagesUsedThisMonth.Should().Be(1);
        user.AiMessagesResetAt.Should().BeAfter(DateTime.UtcNow);
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
