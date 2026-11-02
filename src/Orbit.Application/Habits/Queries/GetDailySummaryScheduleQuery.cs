using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record GetDailySummaryScheduleQuery(Guid UserId, DateOnly DateFrom, DateOnly DateTo,
    IReadOnlyList<Habit> Habits) : IRequest<Result<PaginatedResponse<HabitScheduleItem>>>;

public sealed class GetDailySummaryScheduleQueryHandler(
    IGenericRepository<Habit> habits,
    IHabitScheduleLogReader logs,
    IHabitSchedulePageLoader pages,
    IUserDateService dates,
    IUnitOfWork unitOfWork,
    IRepeatReminderReader reminders) : IRequestHandler<GetDailySummaryScheduleQuery, Result<PaginatedResponse<HabitScheduleItem>>>
{
    public Task<Result<PaginatedResponse<HabitScheduleItem>>> Handle(GetDailySummaryScheduleQuery request, CancellationToken cancellationToken) =>
        new GetHabitScheduleQueryHandler(habits, logs, pages, dates, unitOfWork, reminders).ReadForSummaryAsync(request, cancellationToken);
}
