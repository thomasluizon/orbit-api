using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.Middleware;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Middleware;

public partial class LocalizedErrorResultFilterTests
{
    [Theory]
    [InlineData(false, "en", "You've reached your daily AI message limit (5).")]
    [InlineData(true, "en", "You've reached your weekly AI message limit (250).")]
    [InlineData(false, "pt-BR", "Você atingiu seu limite diário de mensagens de IA (5).")]
    [InlineData(true, "pt-BR", "Você atingiu seu limite semanal de mensagens de IA (250).")]
    public async Task ExhaustedAstraAllowance_LocalizesTheQuotaReasonThroughHttpAndStream(bool pro, string locale, string expected)
    {
        var today = new DateOnly(2026, 10, 27);
        var user = User.Create("Reader", "quota@example.test").Value;
        user.SetLanguage(locale);
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        if (pro) user.SetStripeSubscription("sub_test", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);
        var limit = pro ? 250 : 5;
        for (var index = 0; index < limit; index++) user.IncrementAiMessageCount(today);
        var users = Substitute.For<IGenericRepository<User>>();
        users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(user);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.FreeAiMessagesPerDay, 5, Arg.Any<CancellationToken>()).Returns(5);
        config.GetAsync(AppConfigKeys.ProAiMessagesPerWeek, 250, Arg.Any<CancellationToken>()).Returns(250);
        var work = Substitute.For<IUnitOfWork>();
        var gate = new PayGateService(Substitute.For<IGenericRepository<Habit>>(), users, config, dates,
            Substitute.For<IFeatureFlagService>());

        var failure = await gate.TryConsumeAiMessage(user.Id, work);
        failure.ErrorCode.Should().Be(ErrorCodes.PayGate);
        failure.Error.Should().Be(expected);
        var context = await RunContext(failure.ToErrorResult(), locale);

        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(403);
        var body = result.Value.Should().BeOfType<ErrorResponse>().Subject;
        body.ErrorCode.Should().Be(ErrorCodes.PayGate);
        body.Error.Should().Be(expected);
        ChatErrorLocalization.SetLocale(context.HttpContext, locale);
        (await ChatErrorLocalization.MessageAsync(context.HttpContext, failure.ErrorCode, failure.Error, failure.ErrorArgs))
            .Should().Be(expected);
        user.AiMessagesUsedToday.Should().Be(limit);
        await work.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }
}
