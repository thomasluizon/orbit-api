using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Habits;

public class SuggestHabitSetupCommandHandlerTests
{
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IHabitSuggestionService _suggestionService = Substitute.For<IHabitSuggestionService>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly ILogger<SuggestHabitSetupCommandHandler> _logger =
        Substitute.For<ILogger<SuggestHabitSetupCommandHandler>>();
    private readonly SuggestHabitSetupCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SuggestHabitSetupCommandHandlerTests()
    {
        _handler = new SuggestHabitSetupCommandHandler(
            _payGate, _suggestionService, _userRepo, _unitOfWork, _cache, _logger);
    }

    private static HabitSetupSuggestion SampleSuggestion() =>
        new("R", FrequencyUnit.Day, 1, new[] { DayOfWeek.Monday }, new[] { "Warm up" });

    private void SetupTrackedUser()
    {
        var user = User.Create("Test", "test@example.com").Value;
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    [Fact]
    public async Task Handle_PayGateFails_ReturnsFailure_WithoutCallingServiceOrIncrementing()
    {
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Monthly AI message limit reached"));

        var result = await _handler.Handle(
            new SuggestHabitSetupCommand(UserId, "Run", "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _suggestionService.DidNotReceive()
            .SuggestSetupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Success_CallsService_IncrementsCounter_ReturnsSuggestion()
    {
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _suggestionService.SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success(SampleSuggestion()));
        SetupTrackedUser();

        var result = await _handler.Handle(
            new SuggestHabitSetupCommand(UserId, "Run", "en"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Emoji.Should().Be("R");
        await _suggestionService.Received(1).SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SuggestionServiceFails_PropagatesFailure_WithoutIncrementing()
    {
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _suggestionService.SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitSetupSuggestion>("AI service temporarily unavailable"));

        var result = await _handler.Handle(
            new SuggestHabitSetupCommand(UserId, "Run", "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SecondCallSameTitle_ServedFromCache_WithoutSecondServiceCallOrIncrement()
    {
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _suggestionService.SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success(SampleSuggestion()));
        SetupTrackedUser();

        var command = new SuggestHabitSetupCommand(UserId, "Run", "en");
        await _handler.Handle(command, CancellationToken.None);
        var second = await _handler.Handle(command, CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value.Emoji.Should().Be("R");
        await _suggestionService.Received(1).SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
