using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Notifications.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Notifications;

public class GetPushSubscriptionsQueryHandlerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Handle_ReturnsOnlyCallerSubscriptionsWithoutSecrets(int count)
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var repository = Substitute.For<IGenericRepository<PushSubscription>>();
        var ownSubscriptions = Enumerable.Range(0, count)
            .Select(index => PushSubscription.Create(
                userId,
                $"https://push.example.com/device/{index}",
                index == 0 ? PushSubscription.FcmSentinel : "private-p256dh",
                "private-auth").Value)
            .ToList();
        var otherSubscription = PushSubscription.Create(
            otherUserId, "https://push.example.com/other", "other-p256dh", "other-auth").Value;
        var allSubscriptions = ownSubscriptions.Append(otherSubscription).ToList();

        repository.FindAsync(
                Arg.Any<Expression<Func<PushSubscription, bool>>>(),
                Arg.Any<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.ArgAt<Expression<Func<PushSubscription, bool>>>(0).Compile();
                var order = call.ArgAt<Func<IQueryable<PushSubscription>, IQueryable<PushSubscription>>>(1);
                return Task.FromResult<IReadOnlyList<PushSubscription>>(
                    order(allSubscriptions.Where(predicate).AsQueryable()).ToList());
            });

        var result = await new GetPushSubscriptionsQueryHandler(repository)
            .Handle(new GetPushSubscriptionsQuery(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Max.Should().Be(5);
        result.Value.Items.Should().HaveCount(count);
        result.Value.Items.Select(item => item.Id).Should().BeEquivalentTo(ownSubscriptions.Select(item => item.Id));
        result.Value.Items.Select(item => item.CreatedAtUtc).Should().BeInDescendingOrder();

        if (count > 0)
        {
            var first = ownSubscriptions[0];
            var item = result.Value.Items.Single(item => item.Id == first.Id);
            item.Transport.Should().Be("native");
            item.EndpointHash.Should().Be(Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(first.Endpoint))));
        }

        if (count > 1)
            result.Value.Items.Should().Contain(item => item.Transport == "web");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, JsonSerializerOptions.Web));
        var root = json.RootElement;
        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("items", "max");
        foreach (var item in root.GetProperty("items").EnumerateArray())
            item.EnumerateObject().Select(property => property.Name)
                .Should().BeEquivalentTo("id", "transport", "createdAtUtc", "endpointHash");

        var payload = root.GetRawText();
        payload.Should().NotContain("push.example.com").And.NotContain("private-p256dh")
            .And.NotContain("private-auth").And.NotContain("other-p256dh")
            .And.NotContain("other-auth");
    }

    [Fact]
    public void Validator_RejectsMissingUserId()
    {
        var validator = new GetPushSubscriptionsQueryValidator();

        validator.Validate(new GetPushSubscriptionsQuery(Guid.Empty)).IsValid.Should().BeFalse();
        validator.Validate(new GetPushSubscriptionsQuery(Guid.NewGuid())).IsValid.Should().BeTrue();
    }
}
