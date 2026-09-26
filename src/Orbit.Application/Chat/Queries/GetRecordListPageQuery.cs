using MediatR;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Queries;

public record GetRecordListPageQuery(Guid UserId, string Kind, string Cursor) : IRequest<Result<RecordListCard>>;

public class GetRecordListPageQueryHandler(IMediator mediator) : IRequestHandler<GetRecordListPageQuery, Result<RecordListCard>>
{
    public async Task<Result<RecordListCard>> Handle(GetRecordListPageQuery request, CancellationToken cancellationToken)
    {
        if (!RecordListCursor.TryRead(request.Cursor, request.UserId, request.Kind, out var offset))
            return Result.Failure<RecordListCard>("Record page not found.");

        switch (request.Kind)
        {
            case "notifications":
            {
                var result = await mediator.Send(new GetNotificationsQuery(request.UserId, offset, RecordListCardBuilder.PageSize), cancellationToken);
                if (result.IsFailure)
                    return result.PropagateError<RecordListCard>();
                return offset >= result.Value.TotalCount || result.Value.Items.Count == 0
                    ? Result.Failure<RecordListCard>("Record page not found.")
                    : Result.Success(RecordListCardBuilder.BuildNotificationPage(result.Value, request.UserId, offset));
            }
            case "tags":
            {
                var result = await mediator.Send(new GetTagsQuery(request.UserId), cancellationToken);
                if (result.IsFailure)
                    return result.PropagateError<RecordListCard>();
                return offset >= result.Value.Count
                    ? Result.Failure<RecordListCard>("Record page not found.")
                    : Result.Success(RecordListCardBuilder.BuildTags(result.Value, request.UserId, offset));
            }
            case "templates":
            {
                var result = await mediator.Send(new GetChecklistTemplatesQuery(request.UserId), cancellationToken);
                if (result.IsFailure)
                    return result.PropagateError<RecordListCard>();
                return offset >= result.Value.Count
                    ? Result.Failure<RecordListCard>("Record page not found.")
                    : Result.Success(RecordListCardBuilder.BuildTemplates(result.Value, request.UserId, offset));
            }
            case "keys":
            {
                var result = await mediator.Send(new GetApiKeysQuery(request.UserId), cancellationToken);
                if (result.IsFailure)
                    return result.PropagateError<RecordListCard>();
                return offset >= result.Value.Count
                    ? Result.Failure<RecordListCard>("Record page not found.")
                    : Result.Success(RecordListCardBuilder.BuildKeys(result.Value, TimeProvider.System.GetUtcNow().UtcDateTime, request.UserId, offset));
            }
            default:
                return Result.Failure<RecordListCard>("Record page not found.");
        }
    }
}
