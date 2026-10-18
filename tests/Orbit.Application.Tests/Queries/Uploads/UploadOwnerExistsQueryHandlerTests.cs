using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Uploads.Queries;
using Orbit.Application.Uploads.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Uploads;

public class UploadOwnerExistsQueryHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_ChecksExactOwnerIncludingDeactivatedAccounts(bool exists)
    {
        var owner = User.Create("Owner", "owner@example.com").Value;
        owner.Deactivate(DateTime.UtcNow.AddDays(-1));
        var other = User.Create("Other", "other@example.com").Value;
        var users = Substitute.For<IGenericRepository<User>>();
        using var cancellation = new CancellationTokenSource();
        users.AnyIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), cancellation.Token).Returns(exists);

        var result = await new UploadOwnerExistsQueryHandler(users)
            .Handle(new UploadOwnerExistsQuery(owner.Id), cancellation.Token);

        result.Should().Be(exists);
        await users.Received(1).AnyIgnoringFiltersAsync(
            Arg.Is<Expression<Func<User, bool>>>(predicate => predicate.Compile()(owner) && !predicate.Compile()(other)),
            cancellation.Token);
    }

    [Fact]
    public void Validator_RejectsEmptyOwnerAndAcceptsValidOwner()
    {
        var validator = new UploadOwnerExistsQueryValidator();

        validator.Validate(new UploadOwnerExistsQuery(Guid.Empty)).IsValid.Should().BeFalse();
        validator.Validate(new UploadOwnerExistsQuery(Guid.NewGuid())).IsValid.Should().BeTrue();
    }
}
