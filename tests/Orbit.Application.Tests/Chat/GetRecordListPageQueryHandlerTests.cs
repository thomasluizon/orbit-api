using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat;

public class GetRecordListPageQueryHandlerTests
{
    [Fact]
    public async Task Notifications_ThirtySevenRows_PageThroughToTheEnd()
    {
        var userId = Guid.NewGuid();
        var start = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var items = Enumerable.Range(0, 37)
            .Select(index => new NotificationItemDto(Guid.NewGuid(), $"Notice {index}", "Body", null, null, false,
                start.AddMinutes(-index))).ToList();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<GetNotificationsQuery>();
                return Task.FromResult(Result.Success(new GetNotificationsResponse(
                    items.Skip(query.Offset).Take(query.Limit).ToList(), 0, items.Count)));
            });
        var handler = new GetRecordListPageQueryHandler(mediator);
        var card = RecordListCardBuilder.BuildNotifications(new GetNotificationsResponse(items, 0, 37), userId);

        card.Items.Select(item => item.Title).Should().Equal(items.Take(10).Select(item => item.Title));
        card.TotalCount.Should().Be(37);
        for (var offset = 10; offset < 37; offset += 10)
        {
            card.NextCursor.Should().NotBeNull();
            var result = await handler.Handle(new GetRecordListPageQuery(userId, "notifications", card.NextCursor!), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            card = result.Value;
            card.TotalCount.Should().Be(37);
            card.Items.Select(item => item.Title).Should().Equal(items.Skip(offset).Take(10).Select(item => item.Title));
        }

        card.Items.Should().HaveCount(7);
        card.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task CursorFromAnotherAccountOrKind_ReturnsFailureWithoutReadingRecords()
    {
        var mediator = Substitute.For<IMediator>();
        var handler = new GetRecordListPageQueryHandler(mediator);
        var cursor = RecordListCursor.Create(Guid.NewGuid(), "notifications", 10);

        var accountResult = await handler.Handle(new GetRecordListPageQuery(Guid.NewGuid(), "notifications", cursor), CancellationToken.None);
        var kindResult = await handler.Handle(new GetRecordListPageQuery(Guid.NewGuid(), "tags", cursor), CancellationToken.None);

        accountResult.IsFailure.Should().BeTrue();
        kindResult.IsFailure.Should().BeTrue();
        await mediator.DidNotReceiveWithAnyArgs().Send(default(GetNotificationsQuery)!, default);
    }
}
