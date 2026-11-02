using MediatR;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Commands;

public sealed record DeleteConversationCommand(Guid UserId, Guid ConversationId) : IRequest<Result>;

public sealed class DeleteConversationCommandHandler(IConversationTurnRecorder store) : IRequestHandler<DeleteConversationCommand, Result>
{
    public async Task<Result> Handle(DeleteConversationCommand request, CancellationToken cancellationToken) =>
        await store.DeleteAsync(request.UserId, request.ConversationId, cancellationToken)
            ? Result.Success() : Result.Failure(ConversationErrors.NotFound);
}
