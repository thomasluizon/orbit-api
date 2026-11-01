using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Queries;

public sealed record GetLatestConversationQuery(Guid UserId, Guid? ConversationId = null, long? BeforeOrder = null, int PageSize = 40)
    : IRequest<Result<ConversationPage>>;

public sealed class GetLatestConversationQueryHandler(IConversationTurnRecorder store, IFeatureFlagService flags)
    : IRequestHandler<GetLatestConversationQuery, Result<ConversationPage>>
{
    public async Task<Result<ConversationPage>> Handle(GetLatestConversationQuery request, CancellationToken cancellationToken)
    {
        if (!(await flags.GetEnabledKeysForUserAsync(request.UserId, cancellationToken)).Contains(FeatureFlagKeys.AstraConversationRecording))
            return Result.Failure<ConversationPage>(ConversationErrors.NotFound);
        var conversation = await store.GetLatestAsync(request.UserId, request.ConversationId, request.BeforeOrder, request.PageSize, cancellationToken);
        return conversation is null ? Result.Failure<ConversationPage>(ConversationErrors.NotFound) : Result.Success(conversation);
    }
}
