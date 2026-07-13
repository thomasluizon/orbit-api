using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Calendar;

public class SetSelectedCalendarsCommandHandlerTests
{
    private static readonly string[] SingleCalendarSelection = new[] { "cal_a" };
    private static readonly string[] TwoCalendarSelection = new[] { "cal_a", "cal_b" };
    private static readonly string[] SelectionWithEmptyId = new[] { "cal_a", "" };

    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetSelectedCalendarsCommandHandler _handler;

    public SetSelectedCalendarsCommandHandlerTests()
    {
        _handler = new SetSelectedCalendarsCommandHandler(_userRepo, _unitOfWork);
    }

    private static User CreateUser() => User.Create("Test", "test@example.com").Value;

    private void StubUser(User? user) =>
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        StubUser(null);

        var result = await _handler.Handle(
            new SetSelectedCalendarsCommand(Guid.NewGuid(), SingleCalendarSelection), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SetsSelectionAndPersists()
    {
        var user = CreateUser();
        StubUser(user);

        var result = await _handler.Handle(
            new SetSelectedCalendarsCommand(user.Id, TwoCalendarSelection), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.GetSelectedCalendarIds().Should().BeEquivalentTo(TwoCalendarSelection);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyList_ClearsSelectionToDefault()
    {
        var user = CreateUser();
        user.SetSelectedCalendars(SingleCalendarSelection);
        StubUser(user);

        var result = await _handler.Handle(
            new SetSelectedCalendarsCommand(user.Id, Array.Empty<string>()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.GoogleCalendarSelectedIds.Should().BeNull();
        user.GetSelectedCalendarIds().Should().BeNull();
    }

    [Fact]
    public void Validator_RejectsEmptyId()
    {
        var validator = new SetSelectedCalendarsCommandValidator();

        var result = validator.Validate(
            new SetSelectedCalendarsCommand(Guid.NewGuid(), SelectionWithEmptyId));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_RejectsOversizedList()
    {
        var validator = new SetSelectedCalendarsCommandValidator();
        var tooMany = Enumerable.Range(0, 51).Select(i => $"cal_{i}").ToArray();

        var result = validator.Validate(
            new SetSelectedCalendarsCommand(Guid.NewGuid(), tooMany));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_AcceptsValidSelection()
    {
        var validator = new SetSelectedCalendarsCommandValidator();

        var result = validator.Validate(
            new SetSelectedCalendarsCommand(Guid.NewGuid(), TwoCalendarSelection));

        result.IsValid.Should().BeTrue();
    }
}
