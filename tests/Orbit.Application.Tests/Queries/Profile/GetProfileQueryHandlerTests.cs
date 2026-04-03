using FluentAssertions;
using NSubstitute;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Profile;

public class GetProfileQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly GetProfileQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetProfileQueryHandlerTests()
    {
        _handler = new GetProfileQueryHandler(_userRepo, _streakFreezeRepo, _userDateService, _payGate);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static User CreateTestUser(string name = "Test User")
    {
        return User.Create(name, "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsProfile()
    {
        var user = CreateTestUser("John Doe");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("John Doe");
        result.Value.Email.Should().Be("test@example.com");
        result.Value.AiMessagesLimit.Should().Be(20);
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_FreeUser_ReturnsFreeplan()
    {
        var user = CreateTestUser();
        // Start trial in the past so user is on free plan
        user.StartTrial(DateTime.UtcNow.AddDays(-1));

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().Be("free");
        result.Value.HasProAccess.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ProUser_ReturnsProPlan()
    {
        var user = CreateTestUser();
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddYears(1));

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(500);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
        result.Value.HasProAccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NoRecentFreezes_ReturnsMaxAvailable()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.StreakFreezesAvailable.Should().Be(2);
    }

    [Fact]
    public async Task Handle_AllFreezesUsed_ReturnsZeroAvailable()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(UserId, Arg.Any<CancellationToken>()).Returns(20);

        var recentFreezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today.AddDays(-1)),
            StreakFreeze.Create(UserId, Today.AddDays(-5))
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(recentFreezes.AsReadOnly());

        var query = new GetProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.StreakFreezesAvailable.Should().Be(0);
    }
}
