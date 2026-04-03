using FluentAssertions;
using NSubstitute;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Subscriptions;

public class GetSubscriptionStatusQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly GetSubscriptionStatusQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetSubscriptionStatusQueryHandlerTests()
    {
        _handler = new GetSubscriptionStatusQueryHandler(_userRepo, _payGate);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsStatus()
    {
        // User.Create sets a 7-day trial, so freshly created user has pro access via trial
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(500);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.IsTrialActive.Should().BeTrue();
        result.Value.AiMessagesUsed.Should().Be(0);
        result.Value.AiMessagesLimit.Should().Be(500);
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_TrialUser_ReturnsTrialActive()
    {
        var user = CreateTestUser();
        // User.Create already sets trial, so IsTrialActive should be true
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(500);

        var query = new GetSubscriptionStatusQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsTrialActive.Should().BeTrue();
        result.Value.HasProAccess.Should().BeTrue();
        result.Value.Plan.Should().Be("pro");
    }
}
