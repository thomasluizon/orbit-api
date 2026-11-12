using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Habits;

public class ReadHabitPhraseCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 12);

    private readonly IHabitSuggestionService _suggestions = Substitute.For<IHabitSuggestionService>();
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IAppConfigService _config = Substitute.For<IAppConfigService>();
    private readonly IFeatureFlagService _flags = Substitute.For<IFeatureFlagService>();
    private readonly IProductAnalytics _analytics = Substitute.For<IProductAnalytics>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly User _user = User.Create("Test User", "test@example.com").Value;
    private readonly ReadHabitPhraseCommandHandler _handler;

    public ReadHabitPhraseCommandHandlerTests()
    {
        _users.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>()).Returns(_user);
        _dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _config.GetAsync(AppConfigKeys.PhraseReadsPerDay, AppConstants.DefaultPhraseReadsPerDay, Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultPhraseReadsPerDay);
        _flags.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(Array.Empty<string>());
        _suggestions.SuggestSetupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result.Success(Suggestion()));
        _handler = new ReadHabitPhraseCommandHandler(
            _suggestions, _users, _unitOfWork, _dates, _config, _flags, _analytics, _cache,
            NullLogger<ReadHabitPhraseCommandHandler>.Instance);
    }

    private static HabitSetupSuggestion Suggestion() =>
        new("💊", FrequencyUnit.Day, 1, [], false, null, "08:00", [], []);

    private static ReadHabitPhraseCommand Command(string phrase = "tomar remédio todo dia às 8") =>
        new(UserId, phrase, "pt-BR");

    private int ProviderCalls() =>
        _suggestions.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IHabitSuggestionService.SuggestSetupAsync));

    private void ExpectOutcome(string outcome) =>
        _analytics.Received().CaptureUserEvent(UserId, "phrase_read", null,
            Arg.Is<IReadOnlyDictionary<string, object>>(p => (string)p["outcome"] == outcome));

    [Fact]
    public async Task Handle_Read_ReturnsTheSuggestionAndSpendsNoMessage()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Read.Should().BeTrue();
        result.Value.Emoji.Should().Be("💊");
        result.Value.DueTime.Should().Be("08:00");
        _user.AiMessagesUsedToday.Should().Be(0);
        ExpectOutcome("ai");
    }

    [Fact]
    public async Task Handle_AccountAtItsMessageLimit_StillGetsARead()
    {
        for (var message = 0; message < 5; message++) _user.IncrementAiMessageCount(Today);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Read.Should().BeTrue();
        _user.AiMessagesUsedToday.Should().Be(5);
    }

    [Fact]
    public async Task Handle_ThirtyReadsPass_TheThirtyFirstReturnsUnreadWithNoProviderCall()
    {
        for (var read = 0; read < 30; read++)
            (await _handler.Handle(Command($"phrase {read}"), CancellationToken.None)).Value.Read.Should().BeTrue();

        var result = await _handler.Handle(Command("phrase 31"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(HabitPhraseReading.Unread);
        ProviderCalls().Should().Be(30);
        ExpectOutcome("cap");
    }

    [Fact]
    public async Task Handle_TheCountStartsAtZeroOnTheNextAccountDay()
    {
        var tomorrow = Today.AddDays(1);
        _dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today, Today, tomorrow);
        _config.GetAsync(AppConfigKeys.PhraseReadsPerDay, AppConstants.DefaultPhraseReadsPerDay, Arg.Any<CancellationToken>()).Returns(1);

        (await _handler.Handle(Command("first"), CancellationToken.None)).Value.Read.Should().BeTrue();
        (await _handler.Handle(Command("second"), CancellationToken.None)).Value.Read.Should().BeFalse();
        var nextDay = await _handler.Handle(Command("third"), CancellationToken.None);

        nextDay.Value.Read.Should().BeTrue("the account day moved on");
        _user.PhraseReadsLocalDate.Should().Be(tomorrow);
    }

    [Fact]
    public async Task Handle_CacheHit_MakesNoProviderCallAndDoesNotRaiseTheCount()
    {
        await _handler.Handle(Command(), CancellationToken.None);

        var second = await _handler.Handle(Command(), CancellationToken.None);

        second.Value.Read.Should().BeTrue();
        ProviderCalls().Should().Be(1);
        _user.PhraseReadsToday.Should().Be(1);
        ExpectOutcome("cache");
    }

    [Fact]
    public async Task Handle_ASuggestSetupReadIsAFreeCacheHit()
    {
        _cache.Set(SuggestHabitSetupCommandHandler.BuildCacheKey(UserId, "Tomar Remédio", "pt-BR"), Suggestion());

        var result = await _handler.Handle(Command("  tomar remédio "), CancellationToken.None);

        result.Value.Read.Should().BeTrue();
        ProviderCalls().Should().Be(0);
    }

    [Fact]
    public async Task Handle_KillSwitchOn_MakesNoProviderCall()
    {
        _flags.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns([FeatureFlagKeys.PhraseReaderAiDisabled]);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Value.Read.Should().BeFalse();
        ProviderCalls().Should().Be(0);
        _user.PhraseReadsToday.Should().Be(0);
        ExpectOutcome("disabled");
    }

    [Theory]
    [InlineData("ai_unavailable")]
    [InlineData("ai_budget_exceeded")]
    [InlineData("ai_provider_quota_exhausted")]
    public async Task Handle_ProviderFailure_ReturnsUnreadAndStillCounts(string code)
    {
        _suggestions.SuggestSetupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitSetupSuggestion>(new AppError(code, "failure")));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(HabitPhraseReading.Unread);
        _user.PhraseReadsToday.Should().Be(1);
        ExpectOutcome("failed");
    }

    [Fact]
    public async Task Handle_TwoCallsAtCountTwentyNine_OnePassesAndOneReturnsUnread()
    {
        for (var read = 0; read < 29; read++) _user.TryReservePhraseRead(Today, 30);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new DbUpdateConcurrencyException()));
        _users.When(repository => repository.ReloadAsync(_user, Arg.Any<CancellationToken>())).Do(_ =>
        {
            _user.TryReservePhraseRead(Today, 30);
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);
        });

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Value.Read.Should().BeFalse();
        ProviderCalls().Should().Be(0);
        _user.PhraseReadsToday.Should().Be(30);
    }

    [Fact]
    public async Task Handle_UnknownUser_ReturnsTheNotFoundFailure()
    {
        _users.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        ProviderCalls().Should().Be(0);
    }

    [Fact]
    public async Task Handle_AnalyticsFailure_DoesNotFailTheRead()
    {
        _analytics.When(a => a.CaptureUserEvent(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>())).Do(_ => throw new InvalidOperationException("capture"));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Value.Read.Should().BeTrue();
    }
}
