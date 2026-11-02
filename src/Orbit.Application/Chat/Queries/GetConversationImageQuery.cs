using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Queries;

public sealed record GetConversationImageQuery(Guid UserId, Guid ConversationId, Guid TurnId) : IRequest<Result<StoredConversationImage>>;

public sealed class GetConversationImageQueryHandler(IConversationTurnRecorder store, IFeatureFlagService flags)
    : IRequestHandler<GetConversationImageQuery, Result<StoredConversationImage>>
{
    public async Task<Result<StoredConversationImage>> Handle(GetConversationImageQuery request, CancellationToken cancellationToken)
    {
        if (!(await flags.GetEnabledKeysForUserAsync(request.UserId, cancellationToken)).Contains(FeatureFlagKeys.AstraConversationRecording))
            return Result.Failure<StoredConversationImage>(ConversationErrors.NotFound);
        var image = await store.ReadImageAsync(request.UserId, request.ConversationId, request.TurnId, cancellationToken);
        return image is null ? Result.Failure<StoredConversationImage>(ConversationErrors.NotFound) : Result.Success(image);
    }
}
