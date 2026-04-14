using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Calendar;

public class SetCalendarAutoSyncCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetCalendarAutoSyncCommandHandler _handler;

    public SetCalendarAutoSyncCommandHandlerTests()
    {
        _payGate.CanManageCalendar(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _handler = new SetCalendarAutoSyncCommandHandler(_userRepo, _payGate, _unitOfWork);
    }

    private static User CreateUser(bool pro = true, bool hasGoogleToken = true)
    {
        var user = User.Create("Test", "test@example.com").Value;
        if (pro)
            user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30));
        if (hasGoogleToken)
            user.SetGoogleTokens("access_token", "refresh_token");
        return user;
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(new SetCalendarAutoSyncCommand(Guid.NewGuid(), true), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_EnableForNonProUser_ReturnsProRequiredError()
    {
        _payGate.CanManageCalendar(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure("Calendar integration is a Pro feature. Upgrade to unlock!", "calendar.autoSync.proRequired")));

        var result = await _handler.Handle(new SetCalendarAutoSyncCommand(Guid.NewGuid(), true), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("calendar.autoSync.proRequired");
    }

    [Fact]
    public async Task Handle_EnableWithoutGoogleConnection_ReturnsNotConnectedError()
    {
        var user = CreateUser(pro: true, hasGoogleToken: false);

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new SetCalendarAutoSyncCommand(user.Id, true), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("calendar.autoSync.notConnected");
    }

    [Fact]
    public async Task Handle_EnableProUserWithConnection_Succeeds()
    {
        var user = CreateUser();

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new SetCalendarAutoSyncCommand(user.Id, true), default);

        result.IsSuccess.Should().BeTrue();
        user.GoogleCalendarAutoSyncEnabled.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Disable_ClearsFlag()
    {
        var user = CreateUser();
        user.EnableCalendarAutoSync();

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new SetCalendarAutoSyncCommand(user.Id, false), default);

        result.IsSuccess.Should().BeTrue();
        user.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
    }
}
