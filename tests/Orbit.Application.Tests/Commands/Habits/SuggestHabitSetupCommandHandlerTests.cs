using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Habits;

public class SuggestHabitSetupCommandHandlerTests
{
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IHabitSuggestionService _suggestionService = Substitute.For<IHabitSuggestionService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly SuggestHabitSetupCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly DayOfWeek[] SuggestionDays = new[] { DayOfWeek.Monday };
    private static readonly string[] SubHabits = new[] { "Warm up" };

    public SuggestHabitSetupCommandHandlerTests()
    {
        _handler = new SuggestHabitSetupCommandHandler(
            _payGate, _suggestionService, _unitOfWork, _cache);
    }

    private static HabitSetupSuggestion SampleSuggestion() =>
        new("R", FrequencyUnit.Day, 1, SuggestionDays,
            IsFlexible: false, FlexibleTarget: null, DueTime: null,
            SubHabits: SubHabits, ChecklistItems: Array.Empty<string>());

    [Fact]
    public async Task Handle_ReservationFails_ReturnsFailureWithoutCallingProvider()
    {
        _payGate.TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Monthly AI message limit reached"));

        var result = await _handler.Handle(
            new SuggestHabitSetupCommand(UserId, "Run", "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _suggestionService.DidNotReceive()
            .SuggestSetupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1)
            .TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Success_ReservesBeforeProviderAndReturnsSuggestion()
    {
        var calls = new List<string>();
        _payGate.TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("reserve");
                return Result.Success();
            });
        _suggestionService.SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("provider");
                return Result.Success(SampleSuggestion());
            });

        var result = await _handler.Handle(
            new SuggestHabitSetupCommand(UserId, "Run", "en"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Emoji.Should().Be("R");
        calls.Should().Equal("reserve", "provider");
        await _suggestionService.Received(1).SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SuggestionServiceFails_PropagatesFailureAfterReservation()
    {
        _payGate.TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _suggestionService.SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitSetupSuggestion>("AI service temporarily unavailable"));

        var result = await _handler.Handle(
            new SuggestHabitSetupCommand(UserId, "Run", "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _payGate.Received(1)
            .TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SecondCallSameTitle_ServedFromCacheWithoutSecondProviderCallOrReservation()
    {
        _payGate.TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _suggestionService.SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success(SampleSuggestion()));

        var command = new SuggestHabitSetupCommand(UserId, "Run", "en");
        await _handler.Handle(command, CancellationToken.None);
        var second = await _handler.Handle(command, CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value.Emoji.Should().Be("R");
        await _suggestionService.Received(1).SuggestSetupAsync("Run", "en", Arg.Any<CancellationToken>());
        await _payGate.Received(1)
            .TryConsumeAiMessage(UserId, _unitOfWork, Arg.Any<CancellationToken>());
    }
}
