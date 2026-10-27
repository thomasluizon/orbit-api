using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

public class AstraEvalAllowanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SpentMessages_OnlyResolvedEvalFlagAllowsConsumptionAndKeepsCounting(bool pro, bool exempt)
    {
        var today = new DateOnly(2026, 1, 2);
        var user = User.Create("Eval", "eval@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        if (pro)
            user.SetStripeSubscription("sub_eval", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);
        var limit = pro ? AppConstants.DefaultProAiMessages : AppConstants.DefaultFreeAiMessages;
        for (var index = 0; index < limit; index++)
            user.IncrementAiMessageCount(today);
        var users = Substitute.For<IGenericRepository<User>>();
        users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(user);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(1));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(exempt ? [FeatureFlagKeys.AstraEvalUnmetered] : []);
        using var services = new ServiceCollection()
            .AddSingleton(users).AddSingleton(Substitute.For<IGenericRepository<Habit>>())
            .AddSingleton(config).AddSingleton(dates).AddSingleton(flags)
            .AddTransient<PayGateService>().BuildServiceProvider();
        var gate = services.GetRequiredService<PayGateService>();
        var work = Substitute.For<IUnitOfWork>();

        for (var index = 0; index < 2; index++)
        {
            var result = await gate.TryConsumeAiMessage(user.Id, work);
            result.IsSuccess.Should().Be(exempt);
            if (!exempt)
                result.ErrorCode.Should().Be("PAY_GATE");
        }

        user.GetAiMessagesUsedForQuota(today).Should().Be(limit + (exempt ? 2 : 0));
        await work.Received(exempt ? 2 : 0).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
