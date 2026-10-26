using MediatR;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Queries;

public sealed record GetHabitMonthQuery(Guid UserId, DateOnly? DateFrom = null, DateOnly? DateTo = null,
    string Language = "en") : IRequest<Result<HabitMonthCard>>;

public sealed class GetHabitMonthQueryHandler(IMediator mediator, IUserDateService dates)
    : IRequestHandler<GetHabitMonthQuery, Result<HabitMonthCard>>
{
    public async Task<Result<HabitMonthCard>> Handle(GetHabitMonthQuery request, CancellationToken ct)
    {
        var today = await dates.GetUserTodayAsync(request.UserId, ct);
        var window = HabitCalendarWindow.Resolve(request.DateFrom, request.DateTo, today);
        if (window.IsFailure) return Result.Failure<HabitMonthCard>(DomainErrors.HabitCalendarRangeInvalid);
        var source = await mediator.Send(new GetCalendarMonthQuery(request.UserId, window.Value.From, window.Value.To), ct);
        if (source.IsFailure) return source.PropagateError<HabitMonthCard>();
        var weekStartDay = await dates.GetUserWeekStartDayAsync(request.UserId, ct);
        return Result.Success(new HabitMonthCard(window.Value.From, window.Value.To, today, weekStartDay,
            source.Value, request.Language));
    }
}
