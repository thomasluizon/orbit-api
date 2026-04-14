using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Calendar;

public class DismissCalendarSuggestionCommandHandlerTests
{
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo =
        Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DismissCalendarSuggestionCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid SuggestionId = Guid.NewGuid();

    public DismissCalendarSuggestionCommandHandlerTests()
    {
        _payGate.CanManageCalendar(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _handler = new DismissCalendarSuggestionCommandHandler(_suggestionRepo, _payGate, _unitOfWork);
    }

    [Fact]
    public async Task Handle_SuggestionFound_MarksDismissedAndSaves()
    {
        var suggestion = GoogleCalendarSyncSuggestion.Create(
            UserId, "gcal-event-1", "Morning Yoga",
            DateTime.UtcNow, "{}", DateTime.UtcNow);

        _suggestionRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<Func<IQueryable<GoogleCalendarSyncSuggestion>, IQueryable<GoogleCalendarSyncSuggestion>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(suggestion);

        var result = await _handler.Handle(new DismissCalendarSuggestionCommand(UserId, SuggestionId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        suggestion.DismissedAtUtc.Should().NotBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SuggestionNotFound_ReturnsFailure()
    {
        _suggestionRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<Func<IQueryable<GoogleCalendarSyncSuggestion>, IQueryable<GoogleCalendarSyncSuggestion>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((GoogleCalendarSyncSuggestion?)null);

        var result = await _handler.Handle(new DismissCalendarSuggestionCommand(UserId, SuggestionId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Suggestion not found");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
