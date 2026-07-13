using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetDailySummaryQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly ISummaryService _summaryService = Substitute.For<ISummaryService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GetDailySummaryQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);
    private static readonly string[] ExpectedHabitTitles = new[] { "Read" };

    public GetDailySummaryQueryHandlerTests()
    {
        _handler = new GetDailySummaryQueryHandler(
            _habitRepo, _userRepo, _habitLogRepo, _payGate, _summaryService, _cache);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_GeneratesNewSummary_WhenNotCached()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Test summary content", "Take a short walk after lunch")));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Summary.Should().Be("Test summary content");
        result.Value.Insight.Should().BeEmpty();
        result.Value.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ReturnsCachedSummary_WhenCached()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("First call summary", "First call insight")));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        await _handler.Handle(query, CancellationToken.None);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FromCache.Should().BeTrue();
        result.Value.Summary.Should().Be("First call summary");
        result.Value.Insight.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ExcludesHabitsSkippedInRequestedRange()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var active = Habit.Create(new HabitCreateParams(
            UserId, "Read", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var skipped = Habit.Create(new HabitCreateParams(
            UserId, "Flexible workout", FrequencyUnit.Week, 3, DueDate: Today, IsFlexible: true)).Value;
        skipped.SkipFlexible(Today);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { active, skipped }.AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Summary", "A small nudge")));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _summaryService.Received(1).GenerateSummaryAsync(
            Arg.Is<IEnumerable<Habit>>(habits =>
                habits.Select(h => h.Title).SequenceEqual(ExpectedHabitTitles)),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_BadHabitSlippedBeforeWindow_PassesLastSlipDateFromTargetedQuery()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId, "Skip Gym", FrequencyUnit.Day, 1, DueDate: Today, IsBadHabit: true)).Value;

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { badHabit }.AsReadOnly());

        var slipDate = Today.AddDays(-3);
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog> { HabitLog.Create(badHabit.Id, slipDate, 1) }.AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Summary", "A small nudge")));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _summaryService.Received(1).GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"
                && c.LastBadHabitSlipDates.ContainsKey(badHabit.Id) && c.LastBadHabitSlipDates[badHabit.Id] == slipDate),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_BadHabitSlipReadFilter_ExcludesLogsFromHabitsTheUserDoesNotOwn()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId, "Skip Gym", FrequencyUnit.Day, 1, DueDate: Today, IsBadHabit: true)).Value;
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { badHabit }.AsReadOnly());

        Expression<Func<HabitLog, bool>>? readFilter = null;
        _habitLogRepo.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                readFilter = call.Arg<Expression<Func<HabitLog, bool>>>();
                return (IReadOnlyList<HabitLog>)new List<HabitLog>();
            });

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Summary", "")));

        await _handler.Handle(new GetDailySummaryQuery(UserId, Today, Today, "en"), CancellationToken.None);

        readFilter.Should().NotBeNull();
        var matches = readFilter!.Compile();
        matches(HabitLog.Create(badHabit.Id, Today.AddDays(-1), 1)).Should().BeTrue("the user's own bad-habit slip is in scope");
        matches(HabitLog.Create(Guid.NewGuid(), Today.AddDays(-1), 1)).Should().BeFalse("a slip on a habit the user does not own must be excluded");
    }

    [Fact]
    public async Task Handle_NoBadHabits_SkipsSlipQueryAndPassesEmptyMap()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var goodHabit = Habit.Create(new HabitCreateParams(
            UserId, "Read", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { goodHabit }.AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Summary", "A small nudge")));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        await _handler.Handle(query, CancellationToken.None);

        await _habitLogRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>());
        await _summaryService.Received(1).GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"
                && c.LastBadHabitSlipDates.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_PayGateFails_ReturnsFailure()
    {
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("PAY_GATE", "PAY_GATE"));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_AiSummaryDisabled_ReturnsFailure()
    {
        var user = CreateTestUser();
        user.SetAiSummary(false);

        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("AI summary is disabled");
    }

    [Fact]
    public async Task Handle_SummaryServiceFails_ReturnsFailure()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DailySummaryContent>("AI service unavailable"));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("AI service unavailable");
    }

    [Fact]
    public async Task Handle_UsesUserProfileLanguage_WhenRequestLanguageDiffers()
    {
        var user = CreateTestUser();
        user.SetLanguage("pt-BR");
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "pt-BR"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Resumo em portugues", "Uma pequena dica")));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _summaryService.Received(1).GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "pt-BR"),
            Arg.Any<CancellationToken>());
        await _summaryService.DidNotReceive().GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FallsBackToRequestLanguage_WhenUserLanguageEmpty()
    {
        var user = CreateTestUser();        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "pt-BR"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Resumo", "Dica")));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "pt-BR");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _summaryService.Received(1).GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "pt-BR"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FallsBackToEnglish_WhenBothEmpty()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Summary", "A small nudge")));

        var query = new GetDailySummaryQuery(UserId, Today, Today, "");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _summaryService.Received(1).GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == Today && c.DateTo == Today && c.Language == "en"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CacheExpiry_UsesUserTimezoneEndOfDay()
    {
        var capturingCache = new CapturingCache();
        var handler = new GetDailySummaryQueryHandler(
            _habitRepo, _userRepo, _habitLogRepo, _payGate, _summaryService, capturingCache);

        var user = CreateTestUser();
        user.SetTimeZone("America/Sao_Paulo");
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Arg.Is<DailySummaryContext>(c => c.DateFrom == futureDate && c.DateTo == futureDate && c.Language == "en"),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryContent("Summary", "A small nudge")));

        var query = new GetDailySummaryQuery(UserId, futureDate, futureDate, "en");

        await handler.Handle(query, CancellationToken.None);

        var saoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var expectedExpiry = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(futureDate.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Unspecified),
                saoPaulo),
            TimeSpan.Zero);
        var naiveUtcExpiry = new DateTimeOffset(futureDate.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

        capturingCache.LastAbsoluteExpiration.Should().Be(expectedExpiry);
        capturingCache.LastAbsoluteExpiration.Should().BeAfter(naiveUtcExpiry);
    }

    private sealed class CapturingCache : IMemoryCache
    {
        public DateTimeOffset? LastAbsoluteExpiration { get; private set; }

        public ICacheEntry CreateEntry(object key) => new CapturingEntry(this);

        public bool TryGetValue(object key, out object? value)
        {
            value = null;
            return false;
        }

        public void Remove(object key) { }

        public void Dispose() { }

        private sealed class CapturingEntry(CapturingCache owner) : ICacheEntry
        {
            public object Key { get; } = new();
            public object? Value { get; set; }
            public DateTimeOffset? AbsoluteExpiration { get; set; }
            public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }
            public TimeSpan? SlidingExpiration { get; set; }
            public IList<IChangeToken> ExpirationTokens { get; } = [];
            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; } = [];
            public CacheItemPriority Priority { get; set; }
            public long? Size { get; set; }

            public void Dispose() => owner.LastAbsoluteExpiration = AbsoluteExpiration;
        }
    }
}
