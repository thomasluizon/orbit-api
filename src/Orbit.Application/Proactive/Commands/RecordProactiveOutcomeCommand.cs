using MediatR;
using Orbit.Application.Proactive.Services;
using Orbit.Domain.Common;

namespace Orbit.Application.Proactive.Commands;

public sealed record RecordProactiveOutcomeCommand(Guid UserId, Guid ItemId, bool Acted) : IRequest<Result>;

public sealed class RecordProactiveOutcomeCommandHandler(IProactiveEngine engine)
    : IRequestHandler<RecordProactiveOutcomeCommand, Result>
{
    public Task<Result> Handle(RecordProactiveOutcomeCommand request, CancellationToken cancellationToken) =>
        engine.RecordOutcomeAsync(request.UserId, request.ItemId, request.Acted, cancellationToken);
}
