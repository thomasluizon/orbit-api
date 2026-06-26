using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Tags;

public class SuggestTagsQueryHandlerTests
{
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly ITagSuggestionService _suggestionService = Substitute.For<ITagSuggestionService>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SuggestTagsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SuggestTagsQueryHandlerTests()
    {
        _handler = new SuggestTagsQueryHandler(_payGate, _suggestionService, _tagRepo, _userRepo, _unitOfWork);
    }

    private void GivenPayGateAllows() =>
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

    private void GivenExistingTags(params Tag[] tags) =>
        _tagRepo.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(tags.ToList().AsReadOnly());

    private void GivenAiSuggests(params string[] names) =>
        _suggestionService.SuggestTagsAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<string>>(names.ToList()));

    private User GivenTrackedUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        _userRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(user);
        return user;
    }

    private static SuggestTagsQuery Query() => new(UserId, "Morning run", "Jog around the park", "en");

    [Fact]
    public async Task Handle_PayGateFails_ReturnsFailureWithoutCallingAi()
    {
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("You've reached your monthly AI message limit (20)."));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _suggestionService.DidNotReceive().SuggestTagsAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AiServiceFails_ReturnsFailureWithoutMetering()
    {
        GivenPayGateAllows();
        GivenExistingTags();
        _suggestionService.SuggestTagsAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<string>>("AI tag suggestion temporarily unavailable"));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NewSuggestion_ReturnsNewTagAndMetersOneMessage()
    {
        GivenPayGateAllows();
        GivenExistingTags();
        GivenAiSuggests("fitness");
        var user = GivenTrackedUser();

        var result = await _handler.Handle(Query(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Tags.Should().ContainSingle();
        var suggestion = result.Value.Tags[0];
        suggestion.Name.Should().Be("Fitness");
        suggestion.Color.Should().Be("#7c3aed");
        suggestion.IsExisting.Should().BeFalse();
        suggestion.Id.Should().BeNull();

        user.AiMessagesUsedThisMonth.Should().Be(1);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingNameMatch_ReusesExistingTagOverCreatingDuplicate()
    {
        GivenPayGateAllows();
        var existing = Tag.Create(UserId, "Health", "#10b981").Value;
        GivenExistingTags(existing);
        GivenAiSuggests("health", "reading");
        GivenTrackedUser();

        var result = await _handler.Handle(Query(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Tags.Should().HaveCount(2);

        var reused = result.Value.Tags[0];
        reused.IsExisting.Should().BeTrue();
        reused.Id.Should().Be(existing.Id);
        reused.Name.Should().Be("Health");
        reused.Color.Should().Be("#10b981");

        var created = result.Value.Tags[1];
        created.IsExisting.Should().BeFalse();
        created.Id.Should().BeNull();
        created.Name.Should().Be("Reading");
        created.Color.Should().Be("#7c3aed");
    }

    [Fact]
    public async Task Handle_DedupesCaseInsensitiveAndCapsAtMaxTags()
    {
        GivenPayGateAllows();
        GivenExistingTags();
        GivenAiSuggests("Fit", "fit", "FIT", "Run", "Walk", "Swim", "Bike", "Yoga");
        GivenTrackedUser();

        var result = await _handler.Handle(Query(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Tags.Should().HaveCount(5);
        result.Value.Tags.Select(t => t.Name).Should().OnlyHaveUniqueItems();
        result.Value.Tags.Select(t => t.Name).Should().ContainSingle(name => name == "Fit");
    }
}
