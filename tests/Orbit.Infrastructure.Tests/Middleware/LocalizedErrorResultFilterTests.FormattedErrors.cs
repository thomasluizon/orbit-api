using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Middleware;

public partial class LocalizedErrorResultFilterTests
{
    public static IEnumerable<object[]> FormattedErrors()
    {
        foreach (var field in new[] { typeof(ErrorMessages), typeof(DomainErrors) }
                     .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static)))
        {
            if (field.GetValue(null) is not AppError error)
                continue;
            var placeholders = Regex.Matches(error.Message, @"\{(\d+)(?:[^}]*)\}");
            if (placeholders.Count == 0)
                continue;
            var count = placeholders.Max(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
            var formatted = error.Format(Enumerable.Repeat<object?>(7, count).ToArray());
            foreach (var language in new[] { "en", "pt-BR" })
                foreach (var propagation in new[] { "direct", "generic", "untyped", "chained" })
                    yield return [field.Name, formatted, language, propagation];
        }
    }

    [Theory]
    [MemberData(nameof(FormattedErrors))]
    public async Task EveryFormattedError_AfterLocalization_HasNoUnfilledPlaceholder(
        string name, AppError error, string language, string propagation)
    {
        var source = Result.Failure(error);
        Result result = propagation switch
        {
            "generic" => source.PropagateError<int>(),
            "untyped" => source.PropagateError(),
            "chained" => source.PropagateError<int>().PropagateError().PropagateError<string>(),
            _ => source
        };

        var body = await Run(result.ToErrorResult(), language);

        body.Error.Should().NotMatchRegex(@"\{\d+(?:[^}]*)\}", $"{name} must retain its arguments through {propagation}");
        ErrorCopy.TryResolve(error.Code, language == "pt-BR", error.Args, out var expected).Should().BeTrue();
        body.Error.Should().Be(expected);
        body.ErrorCode.Should().Be(error.Code);
    }

    [Theory]
    [InlineData("en", "You've reached the 37 habit limit.")]
    [InlineData("pt-BR", "Você chegou ao limite de 37 hábitos.")]
    public async Task BulkCreate_AtConfiguredLimit_LocalizesTheRealGateFailure(string language, string expected)
    {
        var userId = Guid.NewGuid();
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var users = Substitute.For<IGenericRepository<User>>();
        var config = Substitute.For<IAppConfigService>();
        var dates = Substitute.For<IUserDateService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        users.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(User.Create("Reader", "reader@orbit.test").Value);
        config.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>()).Returns(37);
        habits.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(37);
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<BulkCreateResult>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<BulkCreateResult>>>>(0)(call.ArgAt<CancellationToken>(1)));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new BulkCreateHabitsCommandHandler(
            new(habits, Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>(), Substitute.For<IGenericRepository<Tag>>()),
            new PayGateService(habits, users, config, dates, Substitute.For<IFeatureFlagService>()), dates, unitOfWork, cache, config);

        var result = await handler.Handle(new(userId, [new("Read", null, FrequencyUnit.Day, 1)]), CancellationToken.None);
        var body = await Run(result.ToErrorResult(), language);

        body.Error.Should().NotMatchRegex(@"\{\d+(?:[^}]*)\}");
        body.Error.Should().Be(expected);
        body.ErrorCode.Should().Be(ErrorCodes.HabitLimitReached);
        await habits.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }
    [Theory]
    [InlineData("en", "You've reached the 2 habit limit.")]
    [InlineData("pt-BR", "Você chegou ao limite de 2 hábitos.")]
    public async Task BulkMove_AtConfiguredLimit_LocalizesFailedItemsAndKeepsSuccessfulItems(string language, string expected)
    {
        var userId = Guid.NewGuid();
        var root = Habit.Create(new(HabitSchedule.Recurring, userId, "Root", FrequencyUnit.Day, 1, new DateOnly(2026, 4, 3))).Value;
        var first = Habit.Create(new(HabitSchedule.Recurring, userId, "First", FrequencyUnit.Day, 1, new DateOnly(2026, 4, 3), ParentHabitId: root.Id)).Value;
        var second = Habit.Create(new(HabitSchedule.Recurring, userId, "Second", FrequencyUnit.Day, 1, new DateOnly(2026, 4, 3), ParentHabitId: root.Id)).Value;
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { root, first, second }.AsReadOnly());
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>()).Returns(2);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var handler = new BulkMoveHabitParentCommandHandler(habits, config, Substitute.For<IUserStreakService>(),
            unitOfWork, dates, cache);

        var result = await handler.Handle(new(userId, [first.Id, second.Id, root.Id], null), CancellationToken.None);
        var context = await RunContext(result.ToPayGateAwareResult(value => new OkObjectResult(value)), language);
        var response = ((ObjectResult)context.Result).Value.Should().BeOfType<BulkMoveHabitParentResult>().Subject;

        response.Results.Select(item => item.Status).Should().Equal(BulkItemStatus.Success, BulkItemStatus.Failed, BulkItemStatus.Success);
        var failed = response.Results[1];
        failed.Error.Should().Be(expected).And.NotMatchRegex(@"\{\d+(?:[^}]*)\}");
        failed.ErrorCode.Should().Be(ErrorCodes.HabitLimitReached);
        first.ParentHabitId.Should().BeNull();
        second.ParentHabitId.Should().Be(root.Id);
        response.Results[0].Error.Should().BeNull();
        JsonSerializer.Serialize(response).Should().NotContain("ErrorArgs");
    }

}
