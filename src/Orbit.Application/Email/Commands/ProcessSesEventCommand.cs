using MediatR;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Email.Commands;

public sealed record ProcessSesEventCommand(string Payload) : IRequest<bool>;

public sealed class ProcessSesEventCommandHandler(ISesEventProcessor processor) : IRequestHandler<ProcessSesEventCommand, bool>
{
    public Task<bool> Handle(ProcessSesEventCommand request, CancellationToken cancellationToken) =>
        processor.ProcessAsync(request.Payload, cancellationToken);
}
