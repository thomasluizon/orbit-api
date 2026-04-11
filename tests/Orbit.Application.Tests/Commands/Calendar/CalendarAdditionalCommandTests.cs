using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Calendar;

public class DismissCalendarSuggestionCommandHandlerTests
{
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo = Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DismissCalendarSuggestionCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public DismissCalendarSuggestionCommandHandlerTests()
    {
        _handler = new DismissCalendarSuggestionCommandHandler(_suggestionRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_SuggestionFound_DismissesIt()
    {
        var suggestion = GoogleCalendarSyncSuggestion.Create(UserId, "event_123", "Meeting", DateTime.UtcNow, "{}",  DateTime.UtcNow);
        _suggestionRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<Func<IQueryable<GoogleCalendarSyncSuggestion>, IQueryable<GoogleCalendarSyncSuggestion>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(suggestion);

        var command = new DismissCalendarSuggestionCommand(UserId, suggestion.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

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

        var command = new DismissCalendarSuggestionCommand(UserId, Guid.NewGuid());
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Suggestion not found");
    }
}
