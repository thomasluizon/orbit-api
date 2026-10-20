using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Commands;

public sealed record InterpretDateCommand(Guid UserId, string Phrase, string Language)
    : IRequest<Result<DateInterpretation>>;

public sealed class InterpretDateCommandHandler(IUserDateService dates, IPayGateService payGate,
    IUnitOfWork unitOfWork, IDateInterpretationService service)
    : IRequestHandler<InterpretDateCommand, Result<DateInterpretation>>
{
    public async Task<Result<DateInterpretation>> Handle(InterpretDateCommand request, CancellationToken cancellationToken)
    {
        var input = DateInterpretationInput.Create(request.Phrase, request.Language);
        if (input.IsFailure)
            return input.PropagateError<DateInterpretation>();
        var timeZone = await dates.GetUserTimeZoneAsync(request.UserId, cancellationToken);
        var referenceDate = await dates.GetUserTodayAsync(timeZone.Id, request.UserId, cancellationToken);
        if (input.Value.IsUnresolvable())
            return Result.Success(new DateInterpretation(null, referenceDate));

        var reservation = await payGate.TryConsumeAiMessage(request.UserId, unitOfWork, cancellationToken);
        if (reservation.IsFailure)
            return reservation.PropagateError<DateInterpretation>();
        var result = await service.InterpretAsync(request.UserId, input.Value, referenceDate, timeZone, cancellationToken);
        return result.IsFailure
            ? result.PropagateError<DateInterpretation>()
            : Result.Success(new DateInterpretation(result.Value, referenceDate));
    }
}
