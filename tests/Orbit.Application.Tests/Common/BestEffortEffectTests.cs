using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

public class BestEffortEffectTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<Exception> _effectFailures = [];

    public BestEffortEffectTests()
    {
        _unitOfWork.ExecuteInSavepointAsync(Arg.Any<Func<CancellationToken, Task<int>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<int>>>(0)(call.ArgAt<CancellationToken>(1)));
    }

    [Fact]
    public async Task InsideATransaction_AFailedSaveOfTheCommandChanges_IsNotAnEffectFailure()
    {
        _unitOfWork.HasActiveTransaction.Returns(true);
        var saveFailure = new InvalidOperationException("The save of the command changes failed");
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(saveFailure);
        var effectRan = false;

        var run = () => BestEffortEffect.RunAsync(_unitOfWork, _ =>
        {
            effectRan = true;
            return Task.FromResult(1);
        }, _effectFailures.Add, CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(saveFailure);
        _effectFailures.Should().BeEmpty("the save belongs to the command, not to the effect");
        effectRan.Should().BeFalse();
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInSavepointAsync<int>(default!, default);
    }

    [Fact]
    public async Task InsideATransaction_AFailedEffect_IsReportedAndTheCommandContinues()
    {
        _unitOfWork.HasActiveTransaction.Returns(true);
        var effectFailure = new InvalidOperationException("The effect failed");

        var result = await BestEffortEffect.RunAsync<int>(_unitOfWork, _ => throw effectFailure, _effectFailures.Add,
            CancellationToken.None);

        result.Should().Be(0);
        _effectFailures.Should().ContainSingle().Which.Should().BeSameAs(effectFailure);
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            _unitOfWork.ExecuteInSavepointAsync(Arg.Any<Func<CancellationToken, Task<int>>>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task WithNoTransaction_TheEffectRunsWithNoSaveAndNoSavepoint()
    {
        _unitOfWork.HasActiveTransaction.Returns(false);

        var result = await BestEffortEffect.RunAsync(_unitOfWork, _ => Task.FromResult(7), _effectFailures.Add,
            CancellationToken.None);

        result.Should().Be(7);
        _effectFailures.Should().BeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInSavepointAsync<int>(default!, default);
    }
}
